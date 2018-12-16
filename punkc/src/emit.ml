open Llvm
open Ast
open Utils

let new_emitter () =
  let context = global_context () in
  let int_type = integer_type context 32 in
  let byte_type = integer_type context 8 in
  let bool_type = integer_type context 1 in
  let void_type = void_type context in
  let mdl = create_module context "punkage" in

  let entry_block =
    let ft = function_type int_type (Array.make 0 int_type) in
    let the_function = declare_function "main" ft mdl in
    append_block context "entry" the_function in

  let declare_printf mdl =
    let ft = var_arg_function_type int_type [| pointer_type byte_type |] in
    declare_function "printf" ft mdl in

  let declare_exit mdl =
    let ft = function_type void_type [| int_type |] in
    declare_function "exit" ft mdl in

  object (self)
    val mutable the_module = mdl
    val mutable main_block = entry_block
    val mutable named_values:(int, llvalue) Hashtbl.t
      = Hashtbl.create table_size
    val mutable builder = builder context
    val mutable current_block = entry_block

    method private get_addr env lv =
      match lv with
      | Evar (i, _) -> Hashtbl.find named_values i
      | Efield (b, (i, Some f)) ->
        let base = self#get_addr env b in
        assert (i >= 0);
        build_struct_gep base i f builder
      | _ -> self#emit_expr env lv

    method private switch_block blk =
      let ret = current_block in
      current_block <- blk;
      position_at_end blk builder;
      ret

    method private restore_block blk =
      current_block <- blk;
      position_at_end blk builder

    method emit_con env c =
      match c with
      | Cint -> int_type
      | Cnamed (v, Some c) -> self#emit_con env c
      | Cprod (cl, _) ->
        struct_type context (Array.of_list (List.map (self#emit_con env) cl))
      | _ -> raise (Fatal ("unimplemented type emission " ^ (string_of_con c)))

    method emit_expr env e =
      match e with
      | Evar (id, _) when id >= 0 ->
        let v = Hashtbl.find named_values id in
        if Hashtbl.mem env.Env.persistent_set id then
          build_load v ((Env.mangle_name id) ^ "_ld") builder
        else
          v
      | Evar (id, _) when id < 0 ->
        raise (Error "unknown variable name")
      | Eint i -> const_int int_type i
      | Estring s ->
        build_global_stringptr s "string_tmp" builder
      | Ebool b ->
        if b then const_int bool_type 1 else const_int bool_type 0
      | Eop (o, el) ->
        begin match o with
          | Add ->
            let lhs = self#emit_expr env (List.nth el 0) in
            let rhs = self#emit_expr env (List.nth el 1) in
            build_add lhs rhs "add_op" builder
          | Cprintf ->
            let vl = List.map (self#emit_expr env) el in
            begin match lookup_function "printf" the_module with
              | None -> raise (Fatal "printf should be declared")
              | Some printer ->
                build_call printer (Array.of_list vl) "unit" builder
            end
          | Lt ->
            let lhs = self#emit_expr env (List.nth el 0) in
            let rhs = self#emit_expr env (List.nth el 1) in
            build_icmp Icmp.Slt lhs rhs "lt_op" builder
          | Idx -> raise (Fatal "unimplemented Idx")
        end
      | Efunc (vmcl, cr, body) ->
        begin
          let env = { env with Env.is_top = false } in
          let ft =
            function_type
              (self#emit_con env cr)
              (Array.map
                 (fun (v, _, c) -> self#emit_con env c)
                 (Array.of_list vmcl)) in
          let the_function =
            declare_function (Env.new_func_name ()) ft the_module in
          let set_param i a =
            match (Array.of_list vmcl).(i) with
            | ((id, Some n), _, c) ->
              set_value_name n a;
              Hashtbl.add named_values id a;
            | _ -> raise (Error "unnamed param") in
          Array.iteri set_param (params the_function);
          (* Create a new basic block to start insertion into. *)
          let block = append_block context "entry" the_function in
          let parent = self#switch_block block in
          try
            let _ = self#emit_stmt env body in
            (* Validate the generated code, checking for consistency. *)
            Llvm_analysis.assert_valid_function the_function;
            self#restore_block parent;
            the_function
          with e ->
            delete_function the_function;
            raise e
        end
      | Efield (b, (i, Some f)) ->
        assert (i >= 0);
        build_extractvalue (self#emit_expr env b) i f builder
      | Earray (Some c, el) ->
        let res = build_array_alloca
          (self#emit_con env c)
          (const_int int_type (List.length el))
          "array_cns" builder in
        let init_elem i e' =
          let gep =
            build_gep res
              (Array.make 1 (const_int int_type i)) "gep" builder in
          let _ = build_store (self#emit_expr env e') gep builder in
          () in
        List.iteri init_elem el;
        res
      | Econ (Cnamed ((_, Some s), Some (Cprod (cl, _)))) ->
        let ty = named_struct_type context s in
        let _ =
          struct_set_body
            ty (Array.of_list (List.map (self#emit_con env) cl)) false in
        const_null int_type
      | Ector (Cnamed ((_, Some s), _), sel) ->
        begin match type_by_name the_module s with
          | None -> raise (Error "type not found")
          | Some ty ->
            let elems = struct_element_types ty in
            let start = const_struct context (Array.map undef elems) in
            let (_, res) = List.fold_left
              (fun (i, v) e ->
                 (i + 1,
                  build_insertvalue v (self#emit_expr env e) i "field" builder))
              (0, start) (List.map snd sel) in
            res
        end
      | Eapp (f, params) ->
        build_call (self#emit_expr env f) (Array.of_list (List.map (self#emit_expr env) params)) "res" builder
      | _ -> raise (Fatal "expr code emission unimplemented yet")

    method emit_stmt env s =
      match s with
      | Sret e ->
        let ret = self#emit_expr env e in
        let _ = build_ret ret builder in
        ()
      | Sdecl ((id, n), m, x, e) ->
        let value = self#emit_expr env e in
        let var =
          if env.is_top then begin
            Hashtbl.add env.persistent_set id ();
            define_global (Env.mangle_name id) value the_module
          end else if m = Mutable then begin
            Hashtbl.add env.persistent_set id ();
            match x with
            | Some c ->
              let addr =
                build_alloca
                  (self#emit_con env c) (Env.mangle_name id) builder in
              ignore (build_store value addr builder);
              addr
            | None ->
              raise (Fatal "symbol declaration should be type infered")
          end else
            value
        in
        Hashtbl.add named_values id var;
      | Sblk sl ->
        if env.is_top then begin
          let _ = declare_exit the_module in
          let _ = declare_printf the_module in
          List.iter (self#emit_stmt env) sl;
          let _ = build_ret (const_int int_type 0) builder in
          ()
        end else
          List.iter (self#emit_stmt env) sl;
      | Sexpr e ->
        let _ = self#emit_expr env e in ()
      | Sasgn (lval, e) ->
        ignore
          (build_store (self#emit_expr env e) (self#get_addr env lval) builder);
      | Sif (e, s0, s1) ->
        let pred = self#emit_expr env e in
        (* Grab the first block so that we might later add the conditional branch
         * to it at the end of the function. *)
        let start_bb = insertion_block builder in
        let the_function = block_parent start_bb in

        let then_bb = append_block context "then" the_function in

        (* Emit 'then' value. *)
        position_at_end then_bb builder;
        self#emit_stmt env s0;

        (* Codegen of 'then' can change the current block, update then_bb for the
         * phi. We create a new name because one is used for the phi node, and the
         * other is used for the conditional branch. *)
        let new_then_bb = insertion_block builder in

        (* Emit 'else' value. *)
        let else_bb = append_block context "else" the_function in
        position_at_end else_bb builder;
        self#emit_stmt env s1;

        (* Codegen of 'else' can change the current block, update else_bb for the
         * phi. *)
        let new_else_bb = insertion_block builder in

        (* Emit merge block. *)
        let merge_bb = append_block context "cont" the_function in

        (* Return to the start block to add the conditional branch. *)
        position_at_end start_bb builder;
        ignore (build_cond_br pred then_bb else_bb builder);

        (* Set a unconditional branch at the end of the 'then' block and the
         * 'else' block to the 'merge' block. *)
        position_at_end new_then_bb builder; ignore (build_br merge_bb builder);
        position_at_end new_else_bb builder; ignore (build_br merge_bb builder);

        (* Finally, set the builder to the end of the merge block. *)
        position_at_end merge_bb builder;

      | Swhile (e, s) ->

        let start_bb = insertion_block builder in
        let the_function = block_parent start_bb in

        let cond_bb = append_block context "cond" the_function in

        position_at_end cond_bb builder;
        let pred = self#emit_expr env e in

        let new_cond_bb = insertion_block builder in

        let loop_bb = append_block context "loop" the_function in
        position_at_end loop_bb builder;
        self#emit_stmt env s;

        let new_loop_bb = insertion_block builder in

        (* Emit merge block. *)
        let merge_bb = append_block context "cont" the_function in

        position_at_end start_bb builder;
        ignore (build_br cond_bb builder);

        position_at_end new_cond_bb builder;
        ignore (build_cond_br pred loop_bb merge_bb builder);

        position_at_end new_loop_bb builder;
        ignore (build_br new_cond_bb builder);

        position_at_end merge_bb builder;

    method get_module () = the_module
  end
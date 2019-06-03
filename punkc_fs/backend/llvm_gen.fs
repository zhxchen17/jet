module Llvm_gen

open System.Collections.Generic
open Bir
open Errors
open Config

let rec gen_type mdl ctx t =
    match t with
    | Bir_integer_type -> Llvm.integer_type ctx 64u
    | Bir_boolean_type -> Llvm.integer_type ctx 1u
    | Bir_byte_type -> Llvm.integer_type ctx 8u
    | Bir_pointer_type p -> Llvm.pointer_type (gen_type mdl ctx p)
    | Bir_unit_type -> Llvm.void_type ctx
    | Bir_function_type(r, ts) ->
        Llvm.pointer_type (gen_func_type mdl ctx (r, ts))
    | Bir_struct_type ts ->
        Llvm.struct_type ctx (Array.map (gen_type mdl ctx) ts)
    | Bir_named_struct_type(n, _) -> Llvm.named_struct_type ctx n
    | Bir_var_arg_function_type(r, ts) ->
        Llvm.pointer_type (gen_var_arg_func_type mdl ctx (r, ts))

and gen_func_type mdl ctx (r, ts) =
    Llvm.function_type (gen_type mdl ctx r) (Array.map (gen_type mdl ctx) ts)

and gen_var_arg_func_type mdl ctx (r, ts) =
    Llvm.var_arg_function_type
        (gen_type mdl ctx r) (Array.map (gen_type mdl ctx) ts)

let decl_type mdl ctx (_, t) =
    match t with
    | Bir_named_struct_type(name, _) ->
        Llvm.named_struct_type ctx name |> ignore
    | _ -> raise (BackendError "Only named user struct type can be declared.")

let def_type mdl ctx (_, t) =
    match t with
    | Bir_named_struct_type(name, ts) ->
        let s = Llvm.named_struct_type ctx name
        Llvm.struct_set_body
            s (Array.map (gen_type mdl ctx) !ts) false |> ignore
    | _ -> raise (BackendError "Only named user struct type can be generated.")

let decl_function mdl ctx (env : Dictionary<_, _>) (name, v) =
    let gen_param f i (id, _) =
        let p = Llvm.param f (uint32 i)
        env.Add(string id, p)
    match v with
    | (id, Bir_function(_, vs, t, name, attrs)) ->
        let ft =
            if attrs.is_var_arg then
                gen_var_arg_func_type mdl ctx t
            else
                gen_func_type mdl ctx t
        let f = Llvm.declare_function name ft mdl
        Array.iteri (gen_param f) vs
        env.Add(string id, f)
    | _ -> raise (BackendError "Function value expected for func declaration.")

let gen_op o =
    match o with
    | Icmp_eq -> Llvm.Icmp.Eq
    | Icmp_slt -> Llvm.Icmp.Slt

let rec gen_value mdl ctx
    (env : Dictionary<_, _>) (benv : Dictionary<_, _>) builder (id, v) =
    let sid = string id
    match env.TryGetValue(sid) with
    | false, _ ->
        let gen = gen_value mdl ctx env benv builder

        let value =
            match v with
            | Bir_nil -> Llvm.const_null (Llvm.integer_type ctx 8u)
            | Bir_gep(b, i, s) ->
                Llvm.build_gep (gen b) (Array.map gen i) s builder
            | Bir_const_integer i ->
                Llvm.const_int (Llvm.integer_type ctx 64u) (uint64 i)
            | Bir_const_boolean b ->
                Llvm.const_int (Llvm.integer_type ctx 1u)
                    (System.Convert.ToUInt64(b))
            | Bir_const_struct vs -> Llvm.const_struct ctx (Array.map gen vs)
            | Bir_load(v, s) -> Llvm.build_load (gen v) s builder
            | Bir_add(v0, v1) -> Llvm.build_add (gen v0) (gen v1) "add" builder
            | Bir_mul(v0, v1) -> Llvm.build_mul (gen v0) (gen v1) "mul" builder
            | Bir_sub(v0, v1) -> Llvm.build_sub (gen v0) (gen v1) "sub" builder
            | Bir_icmp(o, v0, v1) ->
                Llvm.build_icmp (gen_op o) (gen v0) (gen v1) "icmp" builder
            | Bir_call(v0, vs) ->
                Llvm.build_call (gen v0) (Array.map gen vs) "call" builder
            | Bir_extractvalue(v, i, s) ->
                Llvm.build_extractvalue (gen v) (uint32 i) s builder
            | Bir_insertvalue(b, v, i, s) ->
                Llvm.build_insertvalue (gen b) (gen v) (uint32 i) s builder
            | Bir_array_alloca(t, v, s) ->
                Llvm.build_array_alloca (gen_type mdl ctx t) (gen v) s builder
            | Bir_store(v0, v1) -> Llvm.build_store (gen v0) (gen v1) builder
            | Bir_ret v -> Llvm.build_ret (gen v) builder
            | Bir_undef t -> Llvm.undef (gen_type mdl ctx t)
            | Bir_var(_, name) ->
                raise (BackendError "Variables should be already generated.")
            | Bir_global_stringptr(s, n) ->
                Llvm.build_global_stringptr s n builder
            | Bir_cond_br(p, (b0, _, _), (b1, _, _)) ->
                Llvm.build_cond_br (gen p) (benv.Item(b0))
                    (benv.Item(b1)) builder
            | Bir_br(b, _, _) -> Llvm.build_br (benv.Item(b)) builder
            | Bir_alloca(t, s) ->
                Llvm.build_alloca (gen_type mdl ctx t) s builder
            | Bir_function _ ->
                raise (BackendFatal "Function is not supported in codegen.")
            | Bir_global_ref _ -> env.Item(sid)

        env.Add(sid, value)
        value
    | true, v -> v

let gen_global mdl ctx (env : Dictionary<_, _>) (name, (gid, v)) =
    let builder = Llvm.builder ctx
    match v with
    | Bir_global_ref t ->
        let g = Llvm.define_global name (Llvm.undef (gen_type mdl ctx t)) mdl
        env.Add(string gid, g)
    | _ ->
        raise (BackendFatal "global reference")

let decl_block mdl ctx (benv : Dictionary<_, _>) func (name, v, ts) =
    let b = Llvm.append_block ctx name func
    benv.Add(name, b)

let gen_block mdl ctx
    (env : Dictionary<_, _>) (benv : Dictionary<_, _>) (name, v, vs) =
    let b = benv.Item(name)
    let builder = Llvm.builder ctx
    Llvm.position_at_end b builder
    ignore (Array.map (gen_value mdl ctx env benv builder) !vs)

let gen_function mdl ctx (env : Dictionary<_, _>) (_, f) =
    match f with
    | (id, Bir_function(bs, _, _, _, _)) ->
        let func = env.Item(string id)
        let benv = new Dictionary<string, LLVMSharp.LLVMBasicBlockRef>()
        Array.iter (decl_block mdl ctx benv func) !bs
        Array.iter (gen_block mdl ctx env benv) !bs
    | _ -> raise (BackendError "Code generation on non-function values.")

let gen_module mdl =
    let ctx = Llvm.create_context()
    let llvm_mdl = Llvm.create_module ctx mdl.bir_module_name
    let env = new Dictionary<string, Llvm.llvalue>()
    Array.iter (decl_type llvm_mdl ctx) !mdl.bir_type_decls
    Array.iter (def_type llvm_mdl ctx) !mdl.bir_type_decls
    Array.iter (decl_function llvm_mdl ctx env) !mdl.bir_function_decls
    Array.iter (gen_global llvm_mdl ctx env) !mdl.bir_global_decls
    Array.iter (gen_function llvm_mdl ctx env) !mdl.bir_function_decls
    llvm_mdl

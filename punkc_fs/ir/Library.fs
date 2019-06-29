namespace ir

module Ast =
    type mut =
        | Mutable
        | Immutable

    type op =
        | Add
        | Multiply
        | Minus
        | Equal
        | Cprintf
        | Lt
        | Idx

    type kind =
        | Ktype
        | Ksing of ty
        | Kpi of kind * kind
        (* binds *)
        (* | Ksigma of kind * kind  (\* binds *\) *)
        | Kunit

    and ty =
        | Cint
        | Cstring
        | Cbool
        | Cvar of int
        | Cprod of ty list * string list option
        | Carrow of ty list * ty
        | Clam of kind * ty
        (* binds *)
        | Capp of ty * ty
        | Cforall of kind * ty
        (* binds *)
        | Carray of ty
        (* | Carray of ty * exp option *)
        (* | Cexists of kind * con  (\* binds **\) *)
        | Cref of ty
        | Cnamed of Var.id * ty
        | Cunit

    and iface =
        | Iplam of kind * iface * iface
        (* binds *)
        | Imthds of string * ty list * ty
        | Ivoid

    and exp =
        | Evar of Var.id
        | Eint of int
        | Estring of string
        | Ebool of bool
        | Eop of op * texp list
        | Efunc of Var.id * (Var.id * ty) list * ty * stmt
        | Eapp of texp * texp list
        | Etuple of texp list
        | Ector of ty * (string * texp) list
        | Econ of ty
        (* for decls only *)
        | Eplam of kind * iface * texp
        | Earray of ty * texp list
        | Efield of texp * Var.id

    (* | Eproj of expr * int *)
    (* | Epapp of expr * con *)
    (* | Epack of con * expr * con *)
    (* | Eunpack of variable * expr * expr   (\* binds *\) *)

    and texp = ty * exp

    and stmt =
        | Sexpr of texp
        | Sblk of stmt list
        | Sret of texp
        | Sif of texp * stmt * stmt
        | Swhile of texp * stmt
        | Sdecl of Var.id * mut * texp
        | Sasgn of texp * texp

module Ir =
    type op =
        | Add
        | Multiply
        | Minus
        | Equal
        | Cprintf
        | Lt
        | Idx

    type kind =
        | Ktype
        | Kpi of kind list * kind
        (* binds *)
        (* | Ksigma of kind * kind  (\* binds *\) *)

    and ty =
        | Cint
        | Cstring
        | Cbool
        | Cvar of int
        | Cprod of ty list
        | Carrow of ty list * ty
        | Clam of iface list * ty
        (* binds *)
        | Capp of ty * ty list
        | Cforall of iface list * ty
        (* binds *)
        | Carray of ty
        (* | Carray of ty * exp option *)
        | Cexists of iface list * ty (* binds *)
        | Cref of ty
        | Cstruct of Var.id
        | Cunit
        | Csym of int

    and iface =
        | Inamed of Var.id
        | Itop

    and exp =
        | Evar of Var.id
        | Eint of int
        | Estring of string
        | Ebool of bool
        | Eop of op * texp list
        | Efunc of Var.id * (Var.id * ty) list * ty * stmt
        | Eapp of texp * texp list
        | Etuple of texp list
        | Ector of ty * (string * texp) list
        // | Eplam of kind * iface * texp
        | Earray of texp list
        | Efield of texp * Var.id
        | Edefault of ty

    (* | Eproj of expr * int *)
    (* | Epapp of expr * con *)
    (* | Epack of con * expr * con *)
    (* | Eunpack of variable * expr * expr   (\* binds *\) *)

    and texp = ty * exp

    and stmt =
        | Sexpr of texp
        | Sblk of stmt list
        | Sret of texp
        | Sif of texp * stmt * stmt
        | Swhile of texp * stmt
        | Sdecl of Var.id * texp
        | Sasgn of texp * texp

    type mthd_def = string * ty list * ty

    type ty_def =
        | Dstruct of iface list * (string * ty) list
        | Diface of iface list * mthd_def list
        | Dalias of Var.id

    type ty_table = (Var.id * ty_def) list

    type mdl = ty_table * stmt list

    let gen_sym =
        let x = ref 0
        let f() =
            let ret = Csym !x
            x := !x + 1
            ret
        f

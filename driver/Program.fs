open System
open System.IO
open frontend.Frontend
open backend.Backend
open LLVMSharp

[<EntryPoint>]
let main argv =
    let frontend = new Frontend()
    let str = File.ReadAllText argv.[0]
    let mdl = frontend.compile str
    let backend = new Backend()
    let llvm_mdl = backend.compile mdl
    LLVM.PrintModuleToFile(llvm_mdl, argv.[1]) |> ignore
    0

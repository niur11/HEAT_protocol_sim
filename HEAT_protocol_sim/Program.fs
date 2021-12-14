// Learn more about F# at http://docs.microsoft.com/dotnet/fsharp
#load "ref.fsx"
open System.Collections.Generic
open Akka
open Akka.Actor
open Akka.FSharp
open Akka.Configuration
open System

// Define a function to construct a message to print
let from whom =
    sprintf "from %s" whom

[<EntryPoint>]
let main argv =
    let message = from "F#" // Call the function
    printfn "hello Hey"
    printfn "Hello world %s" message
    0 // return an integer exit code
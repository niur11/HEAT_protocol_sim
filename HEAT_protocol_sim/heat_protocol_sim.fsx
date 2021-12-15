#load "ref.fsx"
open System
open System.Collections.Generic
open System.Threading
open Akka
open Akka.Actor
open Akka.FSharp
open Akka.Configuration

let system = ActorSystem.Create("FSharp")

let k = 0.25
let maxNumReq = 3

let numNode = 20 // the number of nodes in the network

let numPack = 20 // the number of packets fed into the network
let numAddtionalGW = 3 // number of random gateway
let gwRate = 3
let fixedGWNeighbors = 3 //numNode / 10 + 1
let meshNCount = 10
let meshNeighborRate = 50 // 1/{} is the number of neighbors each node connects to initially
let gws = HashSet<int>()

let nodeAddrDict = new Dictionary<int, IActorRef>()
let nodeAddr = new Dictionary<int, string>()

type Message =
    | GWInit
    | PrepareBroadcast
    | Beacon of int * float // sourceId, temperature
    | Packet of int * int * int // sourceId, packet id, hopCount
    | MeshInit
    // gateway -> server group:
    | PacketDelivered of int * int // packetId, Hop Count
    // mesh -> server:
    | PacketReq
    | Halt
    | TempReport of int * List<float>
    | LostPacket of int
    | Report 


let rand = Random()

let getNeighbors (id:int) numNeighbor =
    let neighbors = List<int>()
    let mutable i = 0
    while i < numNeighbor do
       let pick = rand.Next(numNode)
       if pick <> id && not(neighbors.Contains(pick)) then
           neighbors.Add(pick)
           i <- i + 1
    neighbors
    
let meshNode id (initNeighbors:List<int>) (serverRef:IActorRef) (mailbox: Actor<_>) =
    let tempTable = new Dictionary<int, float>()
    let temperatures = new List<float>()
    let gwLink = new List<int>()
    let rec loop id (myTemp:float) state =
        actor {
            let! message = mailbox.Receive()
            if state % 10000 = 0 then
                mailbox.Self <! PrepareBroadcast
            match message with
            | MeshInit ->
                for nei in initNeighbors do
                    if not(tempTable.ContainsKey(nei)) then
                        tempTable.Add(nei, 0.0)
                //printfn "nei for %u are %A" id, initNeighbors
                mailbox.Self <! PrepareBroadcast
                return! loop id myTemp (state + 1)
            | PrepareBroadcast ->
                for nei in tempTable.Keys do
                    let neiRef = mailbox.Context.ActorSelection(nodeAddr.[nei])
                    neiRef <! Beacon(id, myTemp)
                //Async.Sleep 50 |> Async.RunSynchronously 
                //mailbox.Self <! PrepareBroadcast
                let newNeighbor = rand.Next(numNode)
                if not(tempTable.ContainsKey(newNeighbor)) then tempTable.Add(newNeighbor, 0.0)
                return! loop id myTemp (state+1)
            | Beacon(source, temp) -> //temperature is calculated very time the beacon message is received.
                //printfn "hbk"
                if not(tempTable.ContainsKey(source)) then tempTable.Add(source, temp)
                if temp = 1.0 then gwLink.Add(source)
                tempTable.[source] <- temp
                let temps = List<float>(tempTable.Values)
                temps |> Seq.sortDescending
                let mutable j = 0
                let mutable t:float = 0.0
                //printfn "ht3"
                
                while j < temps.Count && t < temps.[j] do
                        t <- t + (temps.[j] - t) * k
                        j <- j + 1
//                if t > 0.00 && t < 0.98 then
//                    printfn "node has temperature %1f" t
                temperatures.Add(t)
                serverRef <! TempReport(id, new List<float>(temperatures))
                //mailbox.Self <! PrepareBroadcast
                return! loop id t (state+1)
            | Packet(source, packId, hops) ->
                //printfn "pkt id %u arrives %u with current hop: %u" packId id hops
                if hops >= 500 then
                    serverRef <! LostPacket(packId)
                    return! loop id myTemp (state + 1)
                if source = id then serverRef <! PacketReq
                if gwLink.Count > 0 then
                    mailbox.Context.ActorSelection(nodeAddr.[gwLink.[0]]) <! Packet(source, packId, hops + 1)
                    return! loop id myTemp (state+1)
                let mutable maxTemp = -1.0
                let mutable maxTempNode = -1
                for id in tempTable.Keys do
                    if tempTable.[id] > maxTemp then
                        maxTemp <- tempTable.[id]
                        maxTempNode <- id
                
                let maxNodeRef = mailbox.Context.ActorSelection(nodeAddr.[maxTempNode])
                //printfn "node %u want to send packet %u to %u" id packId maxTempNode
                maxNodeRef <! Packet(source, packId, hops + 1)
                //mailbox.Self <! PrepareBroadcast
                return! loop id myTemp (state+1)
            | Halt ->
                //printfn "hh"
                //printfn "mesh node %u ended" id
                //printfn "node %u has temperature %A" id temperatures
                printfn ""
        }
    loop id 0.0 0
 
 
    
let gateway (id:int) (initNeighbors:List<int>) (serverRef:IActorRef) (mailbox: Actor<_>) =
    let neighborTable = new Dictionary<int, float>()
    let rec loop id =
        actor {
            let! message = mailbox.Receive()
            match message with
            | Beacon(source, temp) ->
                if not(neighborTable.ContainsKey(source)) then neighborTable.Add(source, 0.0)
                //mailbox.Self <! PrepareBroadcast
                return! loop id  // gateway can simply drop the Beacon message
            | Packet(source, packId, hops) ->
                //todo report the hop count to the server node
                serverRef <! PacketDelivered(packId, hops)
                mailbox.Self <! PrepareBroadcast
                return! loop id 
            | MeshInit ->
                //printfn "gateway neighbor : %A" initNeighbors
                for nei in initNeighbors do
                    if not(neighborTable.ContainsKey(nei)) then
                        neighborTable.Add(nei, 0.0)
                mailbox.Self <! PrepareBroadcast
                return! loop id 
            | Halt ->
                //printfn "%u stopped" id
                printfn ""
            | PrepareBroadcast ->
                for nei in neighborTable.Keys do
                    if nodeAddr.ContainsKey(nei) then
                        let neiRef = mailbox.Context.ActorSelection(nodeAddr.[nei])
                        neiRef <! Beacon(id, 1.0)
                    //mailbox.Self <! PrepareBroadcast
                return! loop id 
        }
    loop id 
    
    
let server (mailbox: Actor<_>) =
    let mutable totalPacketReq = 0
    let mutable totalPacketDelivered = 0
    let mutable totalPacketLost = 0
    let mutable totalHops = 0
    let rec loop =
        actor {
            let! message = mailbox.Receive()
            
            match message with
            | PacketDelivered(packid, hopCount) ->
                // add the performance to the stats
                printfn "packet %u has been delivered to the internet with hop count : %u" packid hopCount
                totalPacketDelivered <- totalPacketDelivered + 1
                printfn $"packet delivered {totalPacketDelivered}/{numPack} ``````current delivery rate is {float(totalPacketDelivered) / float(numPack) * 100.0} %%"
                totalHops <- totalHops + hopCount
                if totalPacketDelivered + totalPacketLost >= numPack then
                    mailbox.Self <! Report
//                    //todo report result and end simulation
//                    printfn("simulation finished")
//                    // todo report average hop count
//                    let avghop = float(totalHops) / float(numPack)
//                    printfn $"total number of packets delivered: {deliverTarget}"
//                    printfn $"total number of nodes {numNode}"
//                    printfn $"average hop count is {avghop}"
//                    //stop all the threads
//                    for id in nodeAddrDict.Keys do
//                        nodeAddrDict.[id] <! Halt
                return! loop
            | PacketReq ->
                totalPacketReq <- totalPacketReq + 1
                return! loop
            | LostPacket(pid) ->
                totalPacketLost <- totalPacketLost + 1
                if totalPacketDelivered + totalPacketLost >= numPack then
                    mailbox.Self <! Report
                return! loop
            | TempReport(id, temps) ->
                // todo let actor report temperature just uncomment
                //let revList = temps.Reverse()
                //printfn "%u has temperatures: %A" id temps;
                return! loop
            | Report ->
                printfn("simulation finished")
                // todo report average hop count
                let avghop = float(totalHops) / float(totalPacketDelivered)
                printfn $"total number of packets delivered: {totalPacketDelivered}"
                printfn $"total number of packet loss : {totalPacketLost}"
                printfn $"total number of nodes {numNode}"
                printfn $"average hop count is {avghop}"
                //stop all the threads
                for id in nodeAddrDict.Keys do
                    nodeAddrDict.[id] <! Halt
        }
    loop
    
    
// entry point:


let serverActor = spawn system "Server" <| server

let gatewayNeighbors1 = getNeighbors 0 fixedGWNeighbors//(rand.Next(numNode))

let gatewayNeighbors2 = getNeighbors (numNode - 1) fixedGWNeighbors // (rand.Next(numNode))
let gatewayActor1 = spawn system "gw0" <| gateway 0 gatewayNeighbors1 serverActor
nodeAddr.Add(0, string(gatewayActor1.Path))
gws.Add(0)

let gatewayActor2 = spawn system $"gw{numNode - 1 |> int}" <| gateway (numNode - 1) gatewayNeighbors2 serverActor
nodeAddr.Add(numNode - 1, string(gatewayActor2.Path))
gws.Add(numNode - 1)

nodeAddrDict.Add(0, gatewayActor1)
nodeAddrDict.Add(numNode - 1, gatewayActor2)

printfn "gw1 nbrs: %A" gatewayNeighbors1
printfn "gw2 nbrs: %A" gatewayNeighbors2

let mutable gwCount = 0
let mutable kk = 1
while gwCount < numAddtionalGW do
    let gwIndex = rand.Next(1, numNode - 1)
    if not(gws.Contains(gwIndex)) then
        let gwnbs = getNeighbors gwIndex (numNode / meshNeighborRate + 1) 
        let gwRef = spawn system ("gw" + string(gwIndex)) <| gateway gwIndex gwnbs serverActor
        gws.Add(gwIndex)
        gwCount <- gwCount + 1
        nodeAddrDict.Add(gwIndex, gwRef)
        nodeAddr.Add(gwIndex, string(gwRef.Path))
        printfn "GW %u has neighbors %A" gwIndex gwnbs
        gwRef <! MeshInit
        Async.Sleep 10 |> Async.RunSynchronously

for i in [1..numNode - 2] do
    if not(gws.Contains(i)) then
        let meshNeighbors = getNeighbors i (numNode / meshNeighborRate + 1)//(rand.Next(numNode))
        let meshActor = spawn system ("mesh-" + string(i)) <| meshNode i meshNeighbors serverActor
        nodeAddrDict.Add(i, meshActor)
        nodeAddr.Add(i, string(meshActor.Path))

Async.Sleep 20 |> Async.RunSynchronously

gatewayActor1 <! MeshInit
gatewayActor2 <! MeshInit



printfn "hit1"
Async.Sleep 1000 |> Async.RunSynchronously

for i in [1..numNode - 2] do
    if not(gws.Contains(i)) then
        let actorRef = system.ActorSelection(nodeAddr.[i])
        actorRef <! MeshInit
    
Async.Sleep 1000 |> Async.RunSynchronously

let mutable packCount = 0
while packCount < numPack do
    let pick = rand.Next(numNode)
    if not(gws.Contains(pick)) then
        let nodeRef = system.ActorSelection(nodeAddr.[pick])
        printfn "packet %u send to node %u" packCount pick
        nodeRef <! Packet(-1, packCount, 0)
        packCount <- packCount + 1
        
Console.ReadLine() |> ignore   
        

    
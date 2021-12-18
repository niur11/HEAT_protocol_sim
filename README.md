# HEAT_protocol_sim

### How to run?

go to the project directory:

```
cd Heat_protocol_sim
```

Run the .NET F# script file.

```
dotnet fsi heat_protocol_sim.fsx
```

Some parameters:

``` numNode``` : the number of nodes in the network.

```numPack```: the number of packets fed into the system.


Default topology: random; 
the topology can ge reconfigured by re-writing the ```getNeighbors``` function.

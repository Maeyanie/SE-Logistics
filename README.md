# SE-Logistics
Some WIP logistics scripts for Space Engineers.

Requirements:
- An antenna network allowing all leaves to communicate with their hub.
- A functioning SAM setup for the actual autopiloting from place to place.
- Every assigned cargo ship must be able to travel any route. Beware mountains and long-range trips.
- Every importer must be able to refuel every cargo ship. Easy for battery, less so for rocket fuel.

Current Features:
- Hubs (running Logistics Hub) will take import and export lists from both themselves and Leaves (running Logistics Leaf), and send cargo ships 
(running Logistics Ship) to carry cargo from export to import.
- Uses a job board to combine cargoes on the same trip, and prioritize based on total volume of the trip.
- Supports multiple independent networks using the Channel setting in custom data.
- Ships will self-load cargo.
- Ships will self-unload cargo if you have a cargo container with "Dropoff" in the name.
- Ships will refuel/recharge after each trip.
- Ships can have a max mass set in their config, which is used by Hubs to limit their assigned cargo quantity.
- If a station is importing Ingot/Whatever, it can optionally import Ore/Whatever to fulfill the demand if there aren't any exported ingots available.
- Some status LCDs: \[LogiJobs\], \[LogiStatus\], and \[LogiLog\]

Future work:
- Having ships return to a refuelling depot to refuel/recharge after each trip might be good, instead of expecting every importer to handle it.
- Transporting gases.

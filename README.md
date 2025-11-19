# SE-Logistics
Some WIP logistics scripts for Space Engineers.

Requires a SAM setup for the actual autopiloting from place to place.

Working:
- Hubs (running Logistics Hub) will take import and export lists from both themselves and Leaves (running Logistics Leaf), and send cargo ships 
(running Logistics Ship) to carry cargo from export to import.
- Supports multiple independent networks using the Channel setting in custom data.
- Ships will self-load cargo.
- Ships will refuel/recharge after each trip.
- Ships can have a max mass set in their config, which is used by Hubs to limit their assigned cargo quantity.
- If a station is importing Ingot/Whatever, it can optionally import Ore/Whatever to fulfill the demand if there aren't any exported ingots available.

Future work:
- Having ships return to a refuelling depot to refuel/recharge after each trip might be good, instead of expecting every importer to handle it.
- Ships will not currently self-UNload cargo, which may or may not be beneficial.
- Transporting gases.
- Status LCDs.
- Refactor Hub to construct a job board and task ships with the best job, instead of the first valid job.
- Ships currently only are assigned 1 cargo at a time. Using a job board would make it easier to merge trips to/from the same destinations.
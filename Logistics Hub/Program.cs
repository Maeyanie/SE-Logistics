using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Permissions;
using System.Text;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.Replication.StateGroups;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI.Ingame;
using VRage;
using VRage.Collections;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Game.ObjectBuilders.Definitions;
using VRageMath;
using VRageRender;

namespace IngameScript
{
    public partial class Program : MyGridProgram
    {
        public static Dictionary<string, float> oreYields = new Dictionary<string, float>() {
            {"Iron", 0.7f},
            {"Silicon", 0.7f},
            {"Nickel", 0.4f},
            {"Cobalt", 0.3f},
            {"Silver", 0.1f},
            {"Magnesium", 0.7f},
            {"Gold", 0.01f},
            {"Uranium", 0.01f},
            {"Platinum", 0.005f},
            {"Stone", 1.0f},
        };
        public static Dictionary<string, string> oreMap = new Dictionary<string, string>() {
            {"Ingot/Aluminum", "Ore/Bauxite"},
        };

        public static Program program;
        public static MyIni ini;
        public static IMyBroadcastListener igc;
        public static HashSet<string> items = new HashSet<string>();
        public static List<Ship> ships = new List<Ship>();
        public static Dictionary<string, Ship> shipsByName = new Dictionary<string, Ship>();
        public static List<Leaf> leaves = new List<Leaf>();
        public static Dictionary<string, Leaf> leavesByName = new Dictionary<string, Leaf>();

        static readonly char[] newline = new char[] { '\n' };
        static System.Text.RegularExpressions.Regex samName = new System.Text.RegularExpressions.Regex(@"\[SAM .*Name=([\w]+).*\]");
        public class Ship {
            public string name;
            public long igc;
            public State state;
            public MyFixedPoint maxVolume;
            public MyFixedPoint freeVolume;
            public MyFixedPoint maxMass;
            public MyFixedPoint freeMass;
            public string destination;
            public string dock;
            public ShipJob job;

            public enum State {
                Idle, Starting, Travelling, Loading, Unloading
            }

            public Ship(string name, long igc) {
                this.name = name;
                this.igc = igc;
                state = State.Idle;
                destination = "";
                dock = "";
            }

            public void startJob(ShipJob job, bool transmit = true) {
                state = State.Starting;
                this.job = job;
                foreach (var stage in job.stages) {
                    if (!leavesByName.ContainsKey(stage.destination)) {
                        program.Echo($"Error: Leaf {stage.destination} not found for job assignment.");
                        continue;
                    }
                    Leaf leaf = leavesByName[stage.destination];
                    leaf.reservedDocks.Add(stage.dock);

                    switch (stage.action) {
                        case ShipJob.Stage.Action.Load:
                        case ShipJob.Stage.Action.ChargeLoad:
                            if (stage.cargo != null) {
                                foreach (var cargo in stage.cargo) {
                                    leaf.reservedExports.Add(new Leaf.CargoReservation(name, cargo.item, cargo.qty));
                                }
                            }
                            break;
                        case ShipJob.Stage.Action.Unload:
                        case ShipJob.Stage.Action.ChargeUnload:
                            if (stage.cargo != null) {
                                foreach (var cargo in stage.cargo) {
                                    leaf.reservedImports.Add(new Leaf.CargoReservation(name, cargo.item, cargo.qty));
                                }
                            }
                            break;
                    }
                }
                if (transmit) {
                    string jobString = job.serialize();
                    program.IGC.SendUnicastMessage(igc, "MaeyLogistics-ShipJob", jobString);
                }
            }
            public void finishJob() {
                state = State.Idle;
                if (job == null) {
                    program.Echo($"ERR: finishJob called with null job.");
                    return;
                }
                foreach (var stage in job.stages) {
                    if (!leavesByName.ContainsKey(stage.destination)) {
                        program.Echo($"Error: Leaf {stage.destination} not found for job completion.");
                        continue;
                    }
                    Leaf leaf = leavesByName[stage.destination];
                    leaf.reservedDocks.Remove(stage.dock);
                    switch (stage.action) {
                        case ShipJob.Stage.Action.Load:
                        case ShipJob.Stage.Action.ChargeLoad:
                            leaf.reservedExports.RemoveWhere(i => i.ship == name);
                            break;
                        case ShipJob.Stage.Action.Unload:
                        case ShipJob.Stage.Action.ChargeUnload:
                            leaf.reservedImports.RemoveWhere(i => i.ship == name);
                            break;
                    }
                }
                job = null;
            }

            public void update(ShipUpdateMessage msg) {
                name = msg.shipName;
                if (job == null && msg.job != null) startJob(msg.job, false);

                switch (msg.state) {
                    case "Idle": 
                        if (state != State.Idle) {
                            program.Echo($"Ship {name} has finished her job.");
                            finishJob();
                            state = State.Idle;
                        }
                        break;
                    case "Starting": state = State.Starting; break;
                    case "Travelling": state = State.Travelling; break;
                    case "Loading": state = State.Loading; break;
                    case "Unloading": state = State.Unloading; break;
                    default: state = State.Idle; break;
                }
                maxVolume = msg.maxVolume;
                maxMass = msg.maxMass;
                destination = msg.destination;
                dock = msg.dock;
            }

            public static Ship firstAvailableShip() {
                return ships.FirstOrDefault(s => s.state == State.Idle);
            }

            public override int GetHashCode() {
                return igc.GetHashCode();
            }
            public override bool Equals(object obj) {
                Ship other = obj as Ship;
                if (other != null) return this.igc == other.igc;
                return false;
            }
        }
        public class Leaf {
            public string gridName;
            public DateTime lastUpdate;
            public List<string> docks;
            public Dictionary<string, MyFixedPoint> exports;
            public Dictionary<string, MyFixedPoint> imports;
            public List<string> reservedDocks;
            public HashSet<CargoReservation> reservedImports;
            public HashSet<CargoReservation> reservedExports;

            public class CargoReservation {
                public readonly string ship;
                public readonly string item;
                public MyFixedPoint qty;

                public CargoReservation(string ship, string item, MyFixedPoint qty) {
                    this.ship = ship;
                    this.item = item;
                    this.qty = qty;
                }

                public override int GetHashCode() {
                    return (ship + "|" + item).GetHashCode();
                }
                public override bool Equals(object obj) {
                    CargoReservation other = obj as CargoReservation;
                    if (other != null) return this.ship == other.ship && this.item == other.item;
                    return false;
                }
            }

            public Leaf(string gridName) {
                this.gridName = gridName;
                lastUpdate = DateTime.Now;
                docks = new List<string>();
                exports = new Dictionary<string, MyFixedPoint>();
                imports = new Dictionary<string, MyFixedPoint>();
                reservedDocks = new List<string>();
                reservedExports = new HashSet<CargoReservation>();
                reservedImports = new HashSet<CargoReservation>();
            }
            public void update(LeafUpdateMessage lum) {
                lastUpdate = DateTime.Now;
                docks = lum.docks;
                exports = lum.exports;
                imports = lum.imports;

                foreach (var item in exports.Keys) {
                    if (!items.Contains(item))
                        items.Add(item);
                }
                foreach (var item in imports.Keys) {
                    if (!items.Contains(item))
                        items.Add(item);
                }
            }
            public string firstAvailableDock() {
                foreach (var dock in docks)
                    if (!reservedDocks.Contains(dock))
                        return dock;
                return null;
            }
            public string firstAvailableDock(Ship ship) {
                if (ship.destination == gridName && ship.dock != null && ship.dock != "")
                    return ship.dock;

                foreach (var dock in docks) {
                    if (!reservedDocks.Contains(dock)) return dock;
                }
                return null;
            }
            public MyFixedPoint available(string item) {
                MyFixedPoint value = exports.GetValueOrDefault(item, MyFixedPoint.Zero);
                foreach (var res in reservedExports) {
                    if (res.item == item) value -= res.qty;
                }
                return value;
            }
            public MyFixedPoint needed(string item) {
                MyFixedPoint value = imports.GetValueOrDefault(item, MyFixedPoint.Zero);
                foreach (var res in reservedImports) {
                    if (res.item == item) value -= res.qty;
                }
                return value;
            }
            public List<string> needed() {
                List<string> ret = new List<string>();
                foreach (string item in imports.Keys) {
                    MyFixedPoint count = needed(item);
                    if (count > MyFixedPoint.Zero) ret.Add(item);
                }
                return ret;
            }
        }



        public Program() {
            program = this;

            ini = new MyIni();
            ini.TryParse(Me.CustomData);

            igc = IGC.RegisterBroadcastListener("MaeyLogistics-"+ini.Get("General", "Channel").ToString("Default"));
            Runtime.UpdateFrequency = UpdateFrequency.Update100;
            leaves.Add(new Leaf(Me.CubeGrid.CustomName));

            Echo("Ready.");
        }

        public void Save() { }

        static int counter = 0;
        public void Main(string argument, UpdateType updateSource) {
            while (igc.HasPendingMessage) {
                var igcMessage = igc.AcceptMessage();

                string[] parts = igcMessage.Data.ToString().Split(newline, 2);

                switch (parts[0]) {
                    case "LeafUpdate":
                        handleLeafUpdate(igcMessage.Source, parts[1]);
                        break;
                    case "ShipUpdate":
                        handleShipUpdate(igcMessage.Source, parts[1]);
                        break;
                    default:
                        Echo($"Unknown message type: {parts[0]}");
                        continue;
                }
            }

            if (updateSource == UpdateType.Update100) {
                if (++counter >= leaves.Count) counter = 0;
                var leaf = leaves[counter];
                if (counter == 0) {
                    if (ini.TryParse(Me.CustomData)) {
                        var shipString = ini.Get("General", "Ships").ToString();
                        if (shipString == null || shipString == "") {
                            ships.Clear();
                        } else {
                            var shipList = shipString.Split(',');
                            foreach (var s in ships) {
                                if (!shipList.Contains(s.name)) {
                                    Echo($"Removed ship:{s.name}");
                                    ships.Remove(s);
                                }
                            }
                        }
                    }
                    leaf.update(new LeafUpdateMessage(this));

                    updateLCDs();
                }

                TimeSpan staleness = DateTime.Now - leaf.lastUpdate;
                if (staleness.TotalSeconds > 60) {
                    Echo($"Warning: {leaf.gridName} hasn't reported in {staleness.TotalSeconds} seconds. Skipping.");
                    return;
                }

                Echo($"Checking {leaf.gridName}...");

                var needs = leaf.needed();
                if (needs == null || needs.Count == 0) {
                    Echo("No demands.");
                    return;
                }

                var ship = Ship.firstAvailableShip();
                if (ship == null) {
                    Echo("No free ships.");
                    return;
                }

                var dock = leaf.firstAvailableDock(ship);
                if (dock == null || dock == "") {
                    Echo("No free docks.");
                    return;
                }

                ship.freeVolume = ship.maxVolume;
                ship.freeMass = ship.maxMass;
                if (ship.freeMass == MyFixedPoint.Zero) ship.freeMass = MyFixedPoint.MaxValue;
                foreach (var need in needs) {
                    var needed = leaf.needed(need);
                    Echo($"Needs {needed} {need}");
                    if (fulfillNeed(ship, leaf, dock, need, needed)) return;
                    if (!ini.ContainsKey("General", "RequestOresForIngots")) {
                        ini.Set("General", "RequestOresForIngots", "false");
                        Me.CustomData = ini.ToString();
                    }
                    if (ini.Get("General", "RequestOresForIngots").ToBoolean(false) && need.StartsWith("Ingot/")) {
                        string ore = oreMap.GetValueOrDefault(need, "Ore/" + need.Substring(6));
                        float oreYield = oreYields.GetValueOrDefault(ore.Substring(4), 0.7f);

                        needed -= leaf.available(ore) * oreYield;
                        if (needed <= 0) {
                            Echo("Ore available sufficient to cover ingot need.");
                            continue;
                        }

                        Echo($"No ingots available, trying {ore} instead.");
                        if (fulfillNeed(ship, leaf, dock, ore, needed * (1.0f/oreYield))) return;
                    }
                }
            }
        }

        bool fulfillNeed(Ship ship, Leaf leaf, string dock, string need, MyFixedPoint needed) {
            MyItemType itemType = MyItemType.Parse("MyObjectBuilder_" + need);
            MyItemInfo itemInfo = itemType.GetItemInfo();

            foreach (var other in leaves) {
                if (other == leaf) continue;
                var available = other.available(need);
                if (available != null && available > 0) {
                    Echo($"Found {available} at {other.gridName}");
                    var otherDock = other.firstAvailableDock(ship);
                    if (otherDock == null || otherDock == "") {
                        Echo($"But no free docks at {other.gridName}");
                        continue;
                    }

                    ShipJob job = new ShipJob();

                    ShipJob.Cargo cargo = new ShipJob.Cargo();
                    cargo.item = need;
                    cargo.qty = MyFixedPoint.Min(needed, available);

                    MyFixedPoint maxQtyByVolume = ship.freeVolume * (1.0f/itemInfo.Volume);
                    cargo.qty = MyFixedPoint.Min(cargo.qty, maxQtyByVolume);

                    MyFixedPoint maxQtyByMass = ship.freeMass * (1.0f/itemInfo.Mass);
                    cargo.qty = MyFixedPoint.Min(cargo.qty, maxQtyByMass);

                    if (cargo.qty <= 0) {
                        Echo("Ship cannot carry any more.");
                        return false;
                    }
                    ship.freeVolume -= cargo.qty * itemInfo.Volume;
                    ship.freeMass -= cargo.qty * itemInfo.Mass;

                    if ((ship.freeMass < MyFixedPoint.MaxValue || ship.freeMass > ship.maxMass * 0.5f) && cargo.qty < needed * 0.25f) {
                        Echo("Job too small, waiting for more.");
                        continue;
                    }

                    ShipJob.Stage pickup = new ShipJob.Stage();
                    pickup.destination = other.gridName;
                    pickup.dock = otherDock;
                    pickup.action = ShipJob.Stage.Action.Load;
                    pickup.cargo = new List<ShipJob.Cargo>() { cargo };

                    ShipJob.Stage dropoff = new ShipJob.Stage();
                    dropoff.destination = leaf.gridName;
                    dropoff.dock = dock;
                    dropoff.action = ShipJob.Stage.Action.ChargeUnload;
                    dropoff.cargo = new List<ShipJob.Cargo>() { cargo };

                    job.stages = new List<ShipJob.Stage>() { pickup, dropoff };

                    Echo($"Assigning job to {ship.name}");
                    ship.startJob(job);
                    return true;
                }
            }
            return false;
        }

        void handleLeafUpdate(long source, string data) {
            LeafUpdateMessage lum = LeafUpdateMessage.deserialize(data, this);
            if (lum == null) return;
            Leaf leaf = leavesByName.GetValueOrDefault(lum.gridName);
            if (leaf == null) {
                Echo($"New leaf registered: {lum.gridName} ({source})");
                leaf = new Leaf(lum.gridName);
                leaves.Add(leaf);
                leavesByName[lum.gridName] = leaf;
            } else {
                Echo($"Leaf update from {lum.gridName} ({source})");
            }
            leaf.update(lum);
        }

        void handleShipUpdate(long source, string data) {
            Echo($"Received ShipUpdate from {source}: {data}");
            ShipUpdateMessage sum = ShipUpdateMessage.deserialize(data);
            if (sum == null) return;

            Ship ship = shipsByName.GetValueOrDefault(sum.shipName);
            if (ship == null) {
                var shipString = ini.Get("General", "Ships").ToString();
                var shipList = shipString.Split(',');
                if (!shipList.Contains(sum.shipName)) {
                    Echo($"Ignoring unregistered ship: {sum.shipName} ({source})");
                    return;
                }

                Echo($"New ship registered: {sum.shipName} ({source})");
                ship = new Ship(sum.shipName, source);
                ships.Add(ship);
                shipsByName[sum.shipName] = ship;
            } else {
                Echo($"Ship update from {sum.shipName} ({source})");
            }
            ship.update(sum);
        }

        void updateLCDs() {
            List<IMyTerminalBlock> blocks = new List<IMyTerminalBlock>();
            GridTerminalSystem.GetBlocksOfType(blocks, b => b.CustomName.Contains("[Logi]"));

            foreach (var block in blocks) {
                IMyTextSurface mts = block as IMyTextSurface;
                if (mts != null) {
                    updateLCDSurface(mts);
                }

                IMyTextSurfaceProvider mtsp = block as IMyTextSurfaceProvider;
                if (mtsp != null) {
                    updateLCDSurface(mtsp.GetSurface(0));
                }
            }
        }
        void updateLCDSurface(IMyTextSurface surface) {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("_Stations_");
            foreach (Leaf leaf in leaves) {
                sb.AppendLine($"{leaf.gridName}:");

                sb.Append("   Docks:");
                foreach (var d in leaf.docks) {
                    if (leaf.reservedDocks.Contains(d)) {
                        sb.Append($" *{d}");
                    } else {
                        sb.Append($" {d}");
                    }
                }
                sb.AppendLine("");

                if (leaf.imports != null && leaf.imports.Count > 0) {
                    sb.AppendLine("Imports:");
                    foreach (var import in leaf.imports) {
                        sb.AppendLine($"   {import.Key} x {import.Value}");
                    }
                }
                if (leaf.exports != null && leaf.exports.Count > 0) {
                    sb.AppendLine("Exports:");
                    foreach (var export in leaf.exports) {
                        sb.AppendLine($"   {export.Key} x {export.Value}");
                    }
                }
                sb.AppendLine("");
            }


            sb.AppendLine("_Ships_");
            foreach (Ship ship in ships) {
                sb.AppendLine($"{ship.name}: {ship.state}");
                sb.AppendLine($"   {ship.job}");
            }

            surface.WriteText(sb.ToString());
        }
    }
}

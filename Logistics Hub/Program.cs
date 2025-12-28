using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Permissions;
using System.Text;
using System.Text.RegularExpressions;
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
        const int LOG_LENGTH = 100;

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
        public static JobBoard jobs = new JobBoard();
        public static List<Ship> ships = new List<Ship>();
        public static Dictionary<string, Ship> shipsByName = new Dictionary<string, Ship>();
        public static List<Leaf> leaves = new List<Leaf>();
        public static Dictionary<string, Leaf> leavesByName = new Dictionary<string, Leaf>();
        public static LinkedList<string> logLines = new LinkedList<string>();
        public bool chargePickup = true;
        public bool chargeDropoff = true;

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
                        program.log($"Error: Leaf {stage.destination} not found for job assignment.");
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
                    program.log($"ERR: finishJob called with null job.");
                    return;
                }
                foreach (var stage in job.stages) {
                    if (!leavesByName.ContainsKey(stage.destination)) {
                        program.log($"Error: Leaf {stage.destination} not found for job completion.");
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
                            program.log($"Ship {name} has finished her job.");
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

            if (!ini.ContainsSection("General")) {
                ini.AddSection("General");
                ini.Set("General", "Channel", "Default");
                ini.Set("General", "Docks", "");
                ini.Set("General", "Ships", "");
                ini.Set("General", "RequestOresForIngots", true);
                ini.Set("General", "ChargePickup", true);
                ini.Set("General", "ChargeDropoff", true);
                Me.CustomData = ini.ToString();
            }

            igc = IGC.RegisterBroadcastListener("MaeyLogistics-"+ini.Get("General", "Channel").ToString("Default"));
            Runtime.UpdateFrequency = UpdateFrequency.Update100;

            Leaf self = new Leaf(Me.CubeGrid.CustomName);
            leaves.Add(self);
            leavesByName[self.gridName] = self;

            log("Ready.");
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
                        log($"Unknown message type: {parts[0]}");
                        continue;
                }
            }

            if (updateSource == UpdateType.Update100) {
                counter++;
                if (counter < leaves.Count) {
                    var leaf = leaves[counter];

                    if (counter == 0) {
                        leaf.update(new LeafUpdateMessage(this));
                        updateLCDs();
                    }

                    TimeSpan staleness = DateTime.Now - leaf.lastUpdate;
                    if (staleness.TotalSeconds > 60) {
                        log($"Warning: {leaf.gridName} hasn't reported in {staleness.TotalSeconds} seconds.");
                        jobs.removeAll(leaf.gridName);
                        return;
                    }

                    log($"Updating {leaf.gridName}...");
                    jobs.removeDest(leaf.gridName);

                    var needs = leaf.needed();
                    if (needs == null || needs.Count == 0) {
                        log("No demands.");
                        return;
                    }

                    foreach (var need in needs) {
                        var needed = leaf.needed(need);
                        log($"Needs {needed} {need}");
                        if (fulfillNeed(leaf, need, needed)) continue;

                        if (!ini.ContainsKey("General", "RequestOresForIngots")) {
                            ini.Set("General", "RequestOresForIngots", "false");
                            Me.CustomData = ini.ToString();
                        }
                        if (ini.Get("General", "RequestOresForIngots").ToBoolean(false) && need.StartsWith("Ingot/")) {
                            string ore = oreMap.GetValueOrDefault(need, "Ore/" + need.Substring(6));
                            float oreYield = oreYields.GetValueOrDefault(ore.Substring(4), 0.7f);

                            needed -= leaf.available(ore) * oreYield;
                            if (needed <= 0) {
                                log("Ore available sufficient to cover ingot need.");
                                continue;
                            }

                            log($"No ingots available, trying {ore} instead.");
                            fulfillNeed(leaf, ore, needed * (1.0f / oreYield));
                        }
                    }
                } else {
                    counter = -1;
                    if (ini.TryParse(Me.CustomData)) {
                        var shipString = ini.Get("General", "Ships").ToString();
                        if (shipString == null || shipString == "") {
                            ships.Clear();
                        } else {
                            var shipList = shipString.Split(',');
                            List<Ship> toRemove = null;
                            foreach (var s in ships) {
                                if (!shipList.Contains(s.name)) {
                                    log($"Removed ship:{s.name}");
                                    if (toRemove == null) toRemove = new List<Ship>();
                                    toRemove.Add(s);
                                }
                            }
                            if (toRemove != null) {
                                foreach (var s in toRemove) {
                                    ships.Remove(s);
                                }
                            }
                        }

                        chargePickup = ini.Get("General", "ChargePickup").ToBoolean(true);
                        chargeDropoff = ini.Get("General", "ChargeDropoff").ToBoolean(true);
                    }

                    var ship = Ship.firstAvailableShip();
                    if (ship == null) {
                        log("No free ships.");
                    } else {
                        dispatch(ship);
                    }
                }
            }
        }

        bool fulfillNeed(Leaf leaf, string need, MyFixedPoint needed) {
            MyItemType itemType = MyItemType.Parse("MyObjectBuilder_" + need);
            MyItemInfo itemInfo = itemType.GetItemInfo();

            foreach (var other in leaves) {
                if (other == leaf) continue;
                var available = other.available(need);
                if (available != null && available > MyFixedPoint.Zero) {
                    log($"Found {available} at {other.gridName}");
                    MyFixedPoint qty = MyFixedPoint.Min(needed, available);
                    jobs.setOrder(other.gridName, leaf.gridName, need, qty);
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
                log($"New leaf registered: {lum.gridName} ({source})");
                leaf = new Leaf(lum.gridName);
                leaves.Add(leaf);
                leavesByName[lum.gridName] = leaf;
            } else {
                log($"Leaf update from {lum.gridName} ({source})");
            }
            leaf.update(lum);
        }

        void handleShipUpdate(long source, string data) {
            log($"Received ShipUpdate from {source}");
            ShipUpdateMessage sum = ShipUpdateMessage.deserialize(data);
            if (sum == null) return;

            Ship ship = shipsByName.GetValueOrDefault(sum.shipName);
            if (ship == null) {
                var shipString = ini.Get("General", "Ships").ToString();
                var shipList = shipString.Split(',');
                if (!shipList.Contains(sum.shipName)) {
                    log($"Ignoring unregistered ship: {sum.shipName} ({source})");
                    return;
                }

                log($"New ship registered: {sum.shipName} ({source})");
                ship = new Ship(sum.shipName, source);
                ships.Add(ship);
                shipsByName[sum.shipName] = ship;
            } else {
                log($"Ship update from {sum.shipName} ({source})");
            }
            ship.update(sum);
        }

        void dispatch(Ship ship) {
            log($"Dispatching {ship.name}");
            ship.freeVolume = ship.maxVolume;
            ship.freeMass = ship.maxMass;
            var linksByVolume = jobs.linksByVolume();
            foreach (var link in linksByVolume) {
                string[] srcdst = link.Key.Split('|');
                log($"Checking {srcdst[0]} -> {srcdst[1]}");

                var src = leavesByName[srcdst[0]];
                var srcDock = src.firstAvailableDock(ship);
                if (srcDock == null || srcDock == "") {
                    log($"No free docks at {src.gridName}");
                    continue;
                }
                log($"From: {srcDock}");

                var dst = leavesByName[srcdst[1]];
                var dstDock = dst.firstAvailableDock(ship);
                if (dstDock == null || dstDock == "") {
                    log($"No free docks at {dst.gridName}");
                    continue;
                }
                log($"To: {dstDock}");


                ShipJob job = new ShipJob();
                var mainCargo = jobs.fillShipFromTo(ship, src.gridName, dst.gridName);

                if (ship.destination != "" && ship.destination != src.gridName) {
                    var opCargo = jobs.fillShipFromTo(ship, ship.destination, src.gridName);
                    if (opCargo != null && opCargo.Count > 0) {
                        ShipJob.Stage opPickup = new ShipJob.Stage();
                        opPickup.destination = ship.destination;
                        opPickup.dock = ship.dock;
                        opPickup.action = chargePickup ? ShipJob.Stage.Action.ChargeLoad : ShipJob.Stage.Action.Load;
                        opPickup.cargo = opCargo;

                        ShipJob.Stage opDropoff = new ShipJob.Stage();
                        opDropoff.destination = src.gridName;
                        opDropoff.dock = srcDock;
                        opDropoff.action = ShipJob.Stage.Action.Unload;
                        opDropoff.cargo = opCargo;

                        ShipJob.Stage mainPickup = new ShipJob.Stage();
                        mainPickup.destination = src.gridName;
                        mainPickup.dock = srcDock;
                        mainPickup.action = chargePickup ? ShipJob.Stage.Action.ChargeLoad : ShipJob.Stage.Action.Load;
                        mainPickup.cargo = mainCargo;

                        ShipJob.Stage mainDropoff = new ShipJob.Stage();
                        mainDropoff.destination = dst.gridName;
                        mainDropoff.dock = dstDock;
                        mainDropoff.action = chargeDropoff ? ShipJob.Stage.Action.ChargeUnload : ShipJob.Stage.Action.Unload;
                        mainDropoff.cargo = mainCargo;

                        job.stages = new List<ShipJob.Stage>() { opPickup, opDropoff, mainPickup, mainDropoff };

                        log($"Assigning job to {ship.name}:\n{job.ToString()}");
                        ship.startJob(job);
                        return;
                    }
                }

                ShipJob.Stage pickup = new ShipJob.Stage();
                pickup.destination = src.gridName;
                pickup.dock = srcDock;
                pickup.action = chargePickup ? ShipJob.Stage.Action.ChargeLoad : ShipJob.Stage.Action.Load;
                pickup.cargo = mainCargo;

                ShipJob.Stage dropoff = new ShipJob.Stage();
                dropoff.destination = dst.gridName;
                dropoff.dock = dstDock;
                dropoff.action = chargeDropoff ? ShipJob.Stage.Action.ChargeUnload : ShipJob.Stage.Action.Unload;
                dropoff.cargo = mainCargo;

                job.stages = new List<ShipJob.Stage>() { pickup, dropoff };

                log($"Assigning job to {ship.name}:\n{job.ToString()}");
                ship.startJob(job);
                return;
            }
        }

        static readonly System.Text.RegularExpressions.Regex reLogiStatus = new System.Text.RegularExpressions.Regex(@"\[LogiStatus(:[0-9]+)?\]");
        static readonly System.Text.RegularExpressions.Regex reLogiJobs = new System.Text.RegularExpressions.Regex(@"\[LogiJobs(:[0-9]+)?\]");
        static readonly System.Text.RegularExpressions.Regex reLogiLog = new System.Text.RegularExpressions.Regex(@"\[LogiLog(:[0-9]+)?\]");
        void updateLCDs() {
            List<IMyTerminalBlock> blocks = new List<IMyTerminalBlock>();
            GridTerminalSystem.SearchBlocksOfName("[Logi", blocks);

            foreach (var block in blocks) {
                var statusMatch = reLogiStatus.Match(block.CustomName);
                if (statusMatch.Success) {
                    IMyTextSurface mts = block as IMyTextSurface;
                    if (mts != null) updateSurface_LogiStatus(mts);

                    IMyTextSurfaceProvider mtsp = block as IMyTextSurfaceProvider;
                    int surfaceIndex = statusMatch.Groups[1].Success ? int.Parse(statusMatch.Groups[1].Value.Substring(1)) : 0;
                    if (mtsp != null) updateSurface_LogiStatus(mtsp.GetSurface(surfaceIndex));
                }

                var jobsMatch = reLogiJobs.Match(block.CustomName);
                if (jobsMatch.Success) {
                    IMyTextSurface mts = block as IMyTextSurface;
                    if (mts != null) updateSurface_LogiJobs(mts);

                    IMyTextSurfaceProvider mtsp = block as IMyTextSurfaceProvider;
                    int surfaceIndex = jobsMatch.Groups[1].Success ? int.Parse(jobsMatch.Groups[1].Value.Substring(1)) : 0;
                    if (mtsp != null) updateSurface_LogiJobs(mtsp.GetSurface(surfaceIndex));
                }

                var logMatch = reLogiLog.Match(block.CustomName);
                if (logMatch.Success) {
                    IMyTextSurface mts = block as IMyTextSurface;
                    if (mts != null) updateSurface_LogiLog(mts);

                    IMyTextSurfaceProvider mtsp = block as IMyTextSurfaceProvider;
                    int surfaceIndex = logMatch.Groups[1].Success ? int.Parse(logMatch.Groups[1].Value.Substring(1)) : 0;
                    if (mtsp != null) updateSurface_LogiLog(mtsp.GetSurface(surfaceIndex));
                }
            }
        }
        void updateSurface_LogiStatus(IMyTextSurface surface) {
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
                sb.AppendLine($"   Dest: {ship.destination} @ {ship.dock}");
                sb.AppendLine($"   {ship.job}");
            }

            surface.WriteText(sb.ToString());
        }
        void updateSurface_LogiJobs(IMyTextSurface surface) {
            var links = jobs.linksByVolume();
            StringBuilder sb = new StringBuilder();

            foreach (var link in links) {
                string[] srcdst = link.Key.Split('|');
                sb.AppendLine($"{srcdst[0]} -> {srcdst[1]}:");

                var orders = jobs.getOrdersFromTo(srcdst[0], srcdst[1]);
                foreach (var order in orders) {
                    sb.AppendLine($"   {order.item} x {order.qty}");
                }

                sb.AppendLine("");
            }

            surface.WriteText(sb.ToString());
        }
        void updateSurface_LogiLog(IMyTextSurface surface) {
            float screenSize = surface.SurfaceSize.Y;
            float fontSize = surface.FontSize;
            int lines = (int)Math.Floor(screenSize / fontSize);

            int i = 0;
            StringBuilder sb = new StringBuilder();
            foreach (var line in logLines) {
                if (i++ >= lines) break;
                sb.AppendLine(line);
            }
            surface.WriteText(sb.ToString());
        }

        public void log(string msg) {
            base.Echo(msg);
            logLines.AddFirst(msg);
            while (logLines.Count > LOG_LENGTH) 
                logLines.RemoveLast();
        }

    }

    public class JobBoard {
        public class Order {
            public readonly string source;
            public readonly string dest;
            public readonly string item;
            public MyFixedPoint qty;

            public Order(string source, string dest, string item, MyFixedPoint qty) {
                this.source = source;
                this.dest = dest;
                this.item = item;
                this.qty = qty;
            }
            public override int GetHashCode() {
                return (source + "|" + dest + "|" + item).GetHashCode();
            }
            public override bool Equals(object obj) {
                Order other = obj as Order;
                if (other != null) return this.source == other.source && this.dest == other.dest && this.item == other.item;
                return false;
            }
            public bool Equals(string source, string dest, string item) {
                return this.source == source && this.dest == dest && this.item == item;
            }
            public override string ToString() {
                return $"{source} -> {dest}: {qty} x {item}";
            }
        }

        public HashSet<Order> orders;
        public List<KeyValuePair<string,double>> linksByVolumeCache = null;

        public JobBoard() {
            orders = new HashSet<Order>();
        }

        /*public void setOrder(Order order) {
            var existing = orders.FirstOrDefault(o => o.Equals(order));
            if (existing != null) {
                var item = MyItemType.Parse("MyObjectBuilder_" + order.item);
                var itemInfo = item.GetItemInfo();
                MyFixedPoint volume = existing.qty * itemInfo.Volume;

                if (order.qty == MyFixedPoint.Zero) {
                    orders.Remove(existing);
                } else {
                    volume = order.qty * itemInfo.Volume;
                    existing.qty = order.qty;
                }
            } else {
                if (order.qty != MyFixedPoint.Zero) {
                    var item = MyItemType.Parse("MyObjectBuilder_" + order.item);
                    var itemInfo = item.GetItemInfo();
                    MyFixedPoint volume = order.qty * itemInfo.Volume;

                    orders.Add(order);
                }
            }
        }*/
        public void setOrder(string source, string dest, string item, MyFixedPoint qty) {
            var existing = orders.FirstOrDefault(o => o.Equals(source, dest, item));
            if (existing != null) {
                if (existing.qty == qty) return;

                if (qty == MyFixedPoint.Zero) {
                    orders.Remove(existing);
                } else {
                    existing.qty = qty;
                }
                linksByVolumeCache = null;
            } else {
                if (qty != MyFixedPoint.Zero) {
                    orders.Add(new Order(source, dest, item, qty));
                    linksByVolumeCache = null;
                }
            }
        }

        public List<Order> getOrdersFromTo(string from, string to) {
            return orders.Where(o => o.source == from && o.dest == to).ToList();
        }

        public List<ShipJob.Cargo> fillShipFromTo(Program.Ship ship, string from, string to) {
            var orders = getOrdersFromTo(from, to);
            Program.program.log($"Orders: {orders.Count}");

            orders.Sort((a, b) => {
                MyItemType itemA = MyItemType.Parse("MyObjectBuilder_" + a.item);
                MyItemInfo itemInfoA = itemA.GetItemInfo();
                MyFixedPoint volumeA = a.qty * itemInfoA.Volume;
                MyItemType itemB = MyItemType.Parse("MyObjectBuilder_" + b.item);
                MyItemInfo itemInfoB = itemB.GetItemInfo();
                MyFixedPoint volumeB = b.qty * itemInfoB.Volume;
                return volumeB.ToIntSafe() - volumeA.ToIntSafe();
            });

            List<ShipJob.Cargo> cargo = new List<ShipJob.Cargo>();

            foreach (var order in orders) {
                Program.program.log($"Ordered cargo: {order.item} x {order.qty}");
                MyItemType itemType = MyItemType.Parse("MyObjectBuilder_" + order.item);
                MyItemInfo itemInfo = itemType.GetItemInfo();

                ShipJob.Cargo item = new ShipJob.Cargo();
                item.item = order.item;
                item.qty = order.qty;

                if (!itemInfo.UsesFractions) item.qty = MyFixedPoint.Ceiling(item.qty);

                MyFixedPoint maxQtyByVolume = ship.freeVolume * (1.0f / itemInfo.Volume);
                item.qty = MyFixedPoint.Min(item.qty, maxQtyByVolume);

                MyFixedPoint maxQtyByMass = (ship.maxMass == MyFixedPoint.Zero || ship.maxMass == MyFixedPoint.MaxValue)
                    ? MyFixedPoint.MaxValue : ship.freeMass * (1.0f / itemInfo.Mass);
                item.qty = MyFixedPoint.Min(item.qty, maxQtyByMass);

                if (!itemInfo.UsesFractions) item.qty = MyFixedPoint.Floor(item.qty);

                if (item.qty <= 0) {
                    Program.program.log("Ship cannot carry any more.");
                    Program.program.log($"ordered={order.qty}\nvolume={maxQtyByVolume}\nmass={maxQtyByMass}");
                    Program.program.log($"freeMass={ship.freeMass}\nitemMass={itemInfo.Mass}");
                    break;
                }

                ship.freeVolume -= item.qty * itemInfo.Volume;
                ship.freeMass -= item.qty * itemInfo.Mass;
                cargo.Add(item);
                Program.program.log($"Added cargo: {item.item} x {item.qty}");
            }

            return cargo;
        }

        public List<KeyValuePair<string,double>> linksByVolume() {
            if (linksByVolumeCache != null) return linksByVolumeCache;

            Dictionary<string,double> links = new Dictionary<string, double>();

            foreach (var order in orders) {
                string link = $"{order.source}|{order.dest}";
                var mobItem = MyItemType.Parse("MyObjectBuilder_" + order.item);
                var itemInfo = mobItem.GetItemInfo();
                double volume = (double)order.qty * (double)itemInfo.Volume;

                if (links.ContainsKey(link)) {
                    links[link] += volume;
                } else {
                    links[link] = volume;
                }
            }

            linksByVolumeCache = links.ToList();
            linksByVolumeCache.Sort((a, b) => b.Value.CompareTo(a.Value));

            return linksByVolumeCache;
        }

        public void removeAll(string name) {
            if (orders.RemoveWhere(o => o.source == name || o.dest == name) > 0) linksByVolumeCache = null;
        }
        public void removeDest(string name) {
            if (orders.RemoveWhere(o => o.dest == name) > 0) linksByVolumeCache = null;
        }

        public override string ToString() {
            StringBuilder sb = new StringBuilder();

            foreach (var order in orders) {
                sb.AppendLine(order.ToString());
            }

            return sb.ToString();
        }
    }
}



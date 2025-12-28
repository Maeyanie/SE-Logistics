using Sandbox;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI.Ingame;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Text;
using VRage;
using VRage.Collections;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Game.ObjectBuilders.Definitions;
using VRage.Game.ObjectBuilders.VisualScripting;
using VRageMath;

namespace IngameScript
{
    public partial class Program : MyGridProgram
    {
        const float FULL_CARGO_THRESHOLD = 0.95f;
        const float FULL_CHARGE_THRESHOLD = 0.95f;
        const float FULL_TANK_THRESHOLD = 0.95f;



        static Program program;
        static MyIni ini;
        string channel {
            get {
                MyIni ini = new MyIni();
                ini.TryParse(Me.CustomData);
                return "MaeyLogistics-" + ini.Get("General", "Channel").ToString("Default");
            }
        }
        public enum State {
            Idle, Starting, Travelling, Loading, Unloading, Charging
        }

        State state;
        string destination;
        string dock;
        int step;
        ShipJob job;

        static readonly System.Text.RegularExpressions.Regex samName = new System.Text.RegularExpressions.Regex(@"\[SAM .*Name=([\w]+).*\]");

        public Program()
        {
            program = this;
            Runtime.UpdateFrequency = UpdateFrequency.Update100;

            ini = new MyIni();
            if (ini.TryParse(Me.CustomData)) {
                if (!ini.ContainsSection("General")) {
                    ini.AddSection("General");
                    ini.Set("General", "Channel", "Default");
                    Me.CustomData = ini.ToString();
                }
                if (!ini.ContainsKey("General", "Channel")) {
                    ini.Set("General", "Channel", "Default");
                    Me.CustomData = ini.ToString();
                }
                if (!ini.ContainsKey("General", "RatedCargoMass")) {
                    ini.Set("General", "RatedCargoMass", 0);
                    Me.CustomData = ini.ToString();
                }

                string statestr = ini.Get("State", "State").ToString();
                if (statestr == "Travelling")
                    state = State.Travelling;
                else if (statestr == "Loading")
                    state = State.Loading;
                else if (statestr == "Unloading")
                    state = State.Unloading;
                else if (statestr == "Starting")
                    state = State.Starting;
                else
                    state = State.Idle;

                destination = ini.Get("State", "Destination").ToString("");
                dock = ini.Get("State", "Dock").ToString("");
                step = ini.Get("State", "Step").ToInt32(0);

                string jobstr = ini.Get("State", "Job").ToString("");
                if (jobstr != null && jobstr != "") {
                    job = ShipJob.deserialize(jobstr);
                }
            } else {
                state = State.Idle;
                step = 0;
                job = null;
            }

            // Just in case we aren't docked where we think we are.
            List<IMyShipConnector> connectors = new List<IMyShipConnector>();
            GridTerminalSystem.GetBlocksOfType(connectors, b => b.CubeGrid == program.Me.CubeGrid && b.IsConnected && b.OtherConnector?.CubeGrid != program.Me.CubeGrid);
            foreach (var connector in connectors) {
                var match = samName.Match(connector.OtherConnector.CustomName);
                if (match.Success) {
                    destination = connector.OtherConnector.CubeGrid.CustomName;
                    dock = match.Groups[1].Captures[0].Value;
                    ini.Set("State", "Destination", destination);
                    ini.Set("State", "Dock", dock);
                    Me.CustomData = ini.ToString();
                    break;
                }
            }
        }

        public void Save()
        {
            ini.Set("State", "State", state.ToString());
            ini.Set("State", "Destination", destination);
            ini.Set("State", "Dock", dock);
            ini.Set("State", "Step", step);
            string jobstr;
            if (job != null) {
                jobstr = job.serialize();
            } else {
                jobstr = "";
            }
            ini.Set("State", "Job", jobstr);

            Me.CustomData = ini.ToString();
        }

        int counter = 0;
        public void Main(string argument, UpdateType updateSource) {
            switch (argument) {
                case "reset":
                    state = State.Idle;
                    destination = "";
                    dock = "";
                    step = 0;
                    job = null;

                    List<IMyShipConnector> connectors = new List<IMyShipConnector>();
                    GridTerminalSystem.GetBlocksOfType(connectors, b => b.CubeGrid == program.Me.CubeGrid && b.IsConnected && b.OtherConnector?.CubeGrid != program.Me.CubeGrid);
                    foreach (var connector in connectors) {
                        var match = samName.Match(connector.OtherConnector.CustomName);
                        if (match.Success) {
                            destination = connector.OtherConnector.CubeGrid.CustomName;
                            dock = match.Groups[1].Captures[0].Value;
                            break;
                        }
                    }

                    Save();
                    Echo("Reset state.");
                    return;
            }

            bool dirty = false;
            while (IGC.UnicastListener.HasPendingMessage) {
                var message = IGC.UnicastListener.AcceptMessage();
                if (message.Tag == "MaeyLogistics-ShipJob") {
                    Echo($"Got new job: {message.Data as string}");
                    state = State.Starting;
                    step = 0;
                    job = ShipJob.deserialize(message.Data as string);
                    dirty = true;
                }
            }

            /*Echo($"Current State: {state.ToString()}");
            Echo($"Destination: {destination}");
            Echo($"Dock: {dock}");
            if (job != null) {
                Echo($"Step: {step+1}/{job.stages.Count}");
            } else {
                Echo("No current job.");
            }*/

            switch (state) {
                case State.Idle: break;
                case State.Starting: runStarting(); break;
                case State.Travelling: runTravel(); break;
                case State.Loading: runLoading(); break;
                case State.Unloading: runUnloading(); break;
                case State.Charging: if (finishedCharging()) { step++; startNextStep(); } break;
            }

            if (dirty || (updateSource == UpdateType.Update100 && counter++ % 10 == 0)) {
                ini.TryParse(Me.CustomData);

                ShipUpdateMessage message = new ShipUpdateMessage();
                message.shipName = Me.CubeGrid.CustomName;
                message.state = state.ToString();
                message.maxVolume = MyFixedPoint.Zero;
                message.maxMass = MyFixedPoint.DeserializeStringSafe(ini.Get("General", "RatedCargoMass").ToString("0"));
                message.destination = destination;
                message.dock = dock;
                message.job = job;

                List<IMyCargoContainer> containers = new List<IMyCargoContainer>();
                GridTerminalSystem.GetBlocksOfType(containers, b => program.Me.CubeGrid == b.CubeGrid && b.CustomName.Contains("[SAM]"));
                foreach (var container in containers) {
                    var inv = container.GetInventory(0);
                    if (inv == null) continue;
                    message.maxVolume += inv.MaxVolume;
                }

                string msg = message.serialize();
                //Echo($"Update Sending: {msg}");
                IGC.SendBroadcastMessage(channel, "ShipUpdate\n"+msg);
            }
        }

        void runStarting() {
            ShipJob.Stage firstStage = job.stages.First();
            if (firstStage == null) {
                program.Echo("ERR: Started with null first job stage.");
                state = State.Idle;
                step = 0;
                job = null;
                Save();
                return;
            }
            startNextStep();
        }

        void startNextStep() {            
            if (step >= job.stages.Count) {
                Echo("All done.");
                state = State.Idle;
                step = 0;
                job = null;
                Save();
                return;
            }
            var stage = job.stages[step];

            if (destination != stage.destination || dock != stage.dock) {
                Echo($"Travelling to next stage: {destination} @ {dock} -> {stage.destination} @ {stage.dock}");
                destination = stage.destination;
                dock = stage.dock;
                travelTo(stage.destination, stage.dock);
                Save();
                return;
            }

            switch (stage.action) {
                case ShipJob.Stage.Action.Charge:
                    state = State.Charging;
                    goto case ShipJob.Stage.Action.ChargeLoad;
                case ShipJob.Stage.Action.ChargeLoad:
                case ShipJob.Stage.Action.ChargeUnload:
                    Echo("Starting charging.");
                    chargeMode(true);
                    break;
                }

                switch (stage.action) {
                case ShipJob.Stage.Action.ChargeLoad:
                case ShipJob.Stage.Action.Load:
                    Echo("Starting loading.");
                    startLoading();
                    break;
                case ShipJob.Stage.Action.ChargeUnload:
                case ShipJob.Stage.Action.Unload:
                    Echo("Starting unloading.");
                    startUnloading();
                    break;
                case ShipJob.Stage.Action.None:
                    Echo("No action, moving to next step.");
                    step++;
                    startNextStep();
                    break;
            }
            Save();
        }

        void runTravel() {
            List<IMyShipConnector> connectors = new List<IMyShipConnector>();
            GridTerminalSystem.GetBlocksOfType(connectors, b => b.CubeGrid == Me.CubeGrid);
            foreach (var connector in connectors) {
                if (connector.IsConnected && connector.OtherConnector?.CubeGrid != Me.CubeGrid) {
                    // Arrived
                    Echo("Arrived at destination.");
                    startNextStep();
                    return;
                }
            }
        }

        void runLoading() {
            bool isFull = true;
            List<IMyCargoContainer> containers = new List<IMyCargoContainer>();
            GridTerminalSystem.GetBlocksOfType(containers, b => Me.CubeGrid == b.CubeGrid && b.CustomName.Contains("[SAM]"));
            foreach (var container in containers) {
                var inv = container.GetInventory(0);
                if (inv == null) continue;
                if (inv.CurrentVolume < inv.MaxVolume * FULL_CARGO_THRESHOLD) {
                    isFull = false;
                    break;
                }
            }
            if (isFull) {
                Echo("Containers are full.");
                if (!finishedCharging()) {
                    Echo("Still charging...");
                    return;
                }
                step++;
                startNextStep();
                return;
            }


            List<ShipJob.Cargo> cargoMissing = new List<ShipJob.Cargo>(job.stages[step].cargo);
            foreach (var b in containers) {
                var inv = b.GetInventory(0);
                if (inv == null) continue;
                foreach (var cargo in cargoMissing) {
                    MyFixedPoint contains = inv.GetItemAmount(MyItemType.Parse("MyObjectBuilder_" + cargo.item));
                    if (contains != null) {
                        if (contains >= cargo.qty) {
                            cargo.qty = 0;
                        } else {
                            cargo.qty -= contains;
                        }
                    }
                }
            }
            cargoMissing.RemoveAll(c => c.qty <= 0);
            if (cargoMissing.Count > 0) {
                Echo($"Still waiting for cargo to load: {cargoMissing.Count} items remaining.");

                IMyShipConnector dockedConnector = null;
                List<IMyShipConnector> connectors = new List<IMyShipConnector>();
                GridTerminalSystem.GetBlocksOfType(connectors, b => b.CubeGrid == program.Me.CubeGrid && b.IsConnected);
                foreach (var connector in connectors) {
                    var match = samName.Match(connector.OtherConnector.CustomName);
                    if (match.Success) {
                        dockedConnector = connector;
                        break;
                    }
                }
                if (dockedConnector == null) {
                    Echo("ERR: Not docked to any connector for loading?");
                    return;
                }

                var otherGrid = dockedConnector.OtherConnector.CubeGrid;
                List<IMyCargoContainer> otherContainers = new List<IMyCargoContainer>();
                GridTerminalSystem.GetBlocksOfType(otherContainers, b => b.CubeGrid == otherGrid);

                var cargo = cargoMissing.First();
                var item = MyItemType.Parse("MyObjectBuilder_" + cargo.item);
                var itemInfo = item.GetItemInfo();
                if (!itemInfo.UsesFractions) cargo.qty = MyFixedPoint.Ceiling(cargo.qty);

                foreach (var container in containers) {
                    var inv = container.GetInventory(0);
                    if (inv == null) continue;
                    MyFixedPoint emptySpace = (MyFixedPoint)(((float)(inv.MaxVolume - inv.CurrentVolume)) / item.GetItemInfo().Volume);
                    if (emptySpace < 1) continue;

                    foreach (var otherContainer in otherContainers) {
                        var otherInv = otherContainer.GetInventory(0);
                        if (otherInv == null) continue;

                        List<MyInventoryItem> otherItems = new List<MyInventoryItem>();
                        otherInv.GetItems(otherItems, i => i.Type == item);

                        foreach (var otherItem in otherItems) {
                            MyFixedPoint toTransfer = MyFixedPoint.Min(MyFixedPoint.Min(otherItem.Amount, cargo.qty), emptySpace);
                            if (!itemInfo.UsesFractions) toTransfer = MyFixedPoint.Floor(toTransfer);

                            Echo($"Loading {toTransfer} of {cargo.item} from {otherContainer.CustomName} to {container.CustomName}");
                            if (otherInv.TransferItemTo(inv, otherItem, toTransfer)) {
                                cargo.qty -= toTransfer;
                                emptySpace -= toTransfer * itemInfo.Volume;
                            }
                            if (cargo.qty <= MyFixedPoint.Zero) return;
                        }
                    }
                }

                return;
            } else {
                Echo("All cargo loaded.");
                if (!finishedCharging()) {
                    Echo("Still charging...");
                    return;
                }
                step++;
                startNextStep();
            }
        }

        void runUnloading() {
            List<IMyCargoContainer> containers = new List<IMyCargoContainer>();
            GridTerminalSystem.GetBlocksOfType(containers, b => program.Me.CubeGrid == b.CubeGrid && b.CustomName.Contains("[SAM]"));

            List<IMyCargoContainer> dropContainers = new List<IMyCargoContainer>();
            GridTerminalSystem.GetBlocksOfType(dropContainers, b => program.Me.CubeGrid != b.CubeGrid && b.CustomName.Contains("Dropoff"));

            var isEmpty = true;
            foreach (var b in containers) {
                var inv = b.GetInventory(0);
                if (inv == null) continue;
                var item = inv.GetItemAt(0);
                if (item != null) {
                    isEmpty = false;
                    foreach (var d in dropContainers) {
                        var dinv = d.GetInventory(0);
                        if (dinv == null) continue;
                        if (dinv.CurrentVolume >= dinv.MaxVolume * FULL_CARGO_THRESHOLD) continue;

                        if (inv.TransferItemTo(dinv, (MyInventoryItem)item)) return;
                    }
                }
            }
            if (!isEmpty) return;

            if (!finishedCharging()) {
                Echo("Still charging...");
                return;
            }

            step++;
            startNextStep();
        }

        void travelTo(string grid, string dock) {
            program.Echo($"Travelling to: {dock}");
            state = State.Travelling;

            List<IMyProgrammableBlock> sam = new List<IMyProgrammableBlock>();
            GridTerminalSystem.GetBlocksOfType(sam, b => program.Me.CubeGrid == b.CubeGrid && b.CustomName.Contains("[SAM"));
            sam.First().TryRun($"go {dock}");
        }

        void chargeMode(bool onoff) {
            List<IMyBatteryBlock> batteries = new List<IMyBatteryBlock>();
            GridTerminalSystem.GetBlocksOfType(batteries, b => program.Me.CubeGrid == b.CubeGrid && b.CustomName.Contains("[SAM FORCE]"));
            foreach (var b in batteries) {
                if (onoff) {
                    b.ChargeMode = ChargeMode.Recharge;
                } else {
                    b.ChargeMode = ChargeMode.Auto;
                }
            }

            List<IMyGasTank> tanks = new List<IMyGasTank>();
            GridTerminalSystem.GetBlocksOfType(tanks, b => program.Me.CubeGrid == b.CubeGrid && b.CustomName.Contains("[SAM FORCE]"));
            foreach (var b in tanks) {
                if (onoff) {
                    b.Stockpile = true;
                } else {
                    b.Stockpile = false;
                }
            }
        }
        bool finishedCharging() {
            if (job.stages[step].action != ShipJob.Stage.Action.Charge
                && job.stages[step].action != ShipJob.Stage.Action.ChargeLoad
                && job.stages[step].action != ShipJob.Stage.Action.ChargeUnload)
            {
                return true;
            }

            float batteryLevel = 0f;
            float tankLevel = 0f;
            int batteryCount = 0;
            int tankCount = 0;
            List<IMyBatteryBlock> batteries = new List<IMyBatteryBlock>();
            GridTerminalSystem.GetBlocksOfType(batteries, b => program.Me.CubeGrid == b.CubeGrid && b.CustomName.Contains("[SAM FORCE]"));
            foreach (var b in batteries) {
                batteryLevel += b.CurrentStoredPower / b.MaxStoredPower;
                batteryCount++;
            }
            if (batteryCount > 0) {
                batteryLevel /= batteryCount;
            } else {
                batteryLevel = 1f;
            }

            List<IMyGasTank> tanks = new List<IMyGasTank>();
            GridTerminalSystem.GetBlocksOfType(tanks, b => program.Me.CubeGrid == b.CubeGrid && b.CustomName.Contains("[SAM FORCE]"));
            foreach (var b in tanks) {
                tankLevel += (float)(b.FilledRatio);
                tankCount++;
            }
            if (tankCount > 0) {
                tankLevel /= tankCount;
            } else {
                tankLevel = 1f;
            }
            program.Echo($"Battery Level: {batteryLevel*100f:0.0}%");
            program.Echo($"Tank Level: {tankLevel*100f:0.0}%");
            return batteryLevel >= FULL_CHARGE_THRESHOLD && tankLevel >= FULL_TANK_THRESHOLD;
        }

        void startLoading() {
            state = State.Loading;
            runLoading();
        }
        void startUnloading() {
            state = State.Unloading;
            runUnloading();
        }
    }
}

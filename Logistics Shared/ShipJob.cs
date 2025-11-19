using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI.Ingame;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using VRage;
using VRage.Collections;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Game.ObjectBuilders.Definitions;
using VRageMath;

namespace IngameScript {
    public class ShipJob {
        public class Cargo {
            public string item;
            public MyFixedPoint qty;
        }

        public class Stage {
            public enum Action { Load, Unload, ChargeLoad, ChargeUnload, Charge, None }

            public Action action;
            public string destination;
            public string dock;
            public List<Cargo> cargo;
        }

        public List<Stage> stages = new List<Stage>();

        public string serialize() {
            StringBuilder sb = new StringBuilder();
            sb.Append(stages.Count());
            sb.Append("\n");
            foreach (var stage in stages) {
                sb.Append(stage.action.ToString());
                sb.Append("\n");
                sb.Append(stage.destination);
                sb.Append("\n");
                sb.Append(stage.dock);
                sb.Append("\n");
                sb.Append(stage.cargo.Count());
                sb.Append("\n");
                foreach (var cargo in stage.cargo) {
                    sb.Append(cargo.item);
                    sb.Append("\n");
                    sb.Append(cargo.qty.SerializeString());
                    sb.Append("\n");
                }
            }
            return sb.ToString();
        }
        public static ShipJob deserialize(string str) {
            ShipJob job = new ShipJob();
            string[] lines = str.Split('\n');
            int position = 0;

            int stageCount = Int32.Parse(lines[position++]);
            for (int s = 0; s < stageCount; s++) {
                Stage stage = new Stage();

                string action = lines[position++];
                switch (action) {
                    case "Load": stage.action = Stage.Action.Load; break;
                    case "Unload": stage.action = Stage.Action.Unload; break;
                    case "ChargeLoad": stage.action = Stage.Action.ChargeLoad; break;
                    case "ChargeUnload": stage.action = Stage.Action.ChargeUnload; break;
                    case "Charge": stage.action = Stage.Action.Charge; break;
                    case "None": stage.action = Stage.Action.None; break;
                }
                stage.destination = lines[position++];
                stage.dock = lines[position++];

                stage.cargo = new List<Cargo>();
                int cargoCount = Int32.Parse(lines[position++]);
                for (int i = 0; i < cargoCount; i++) {
                    Cargo cargo = new Cargo();
                    cargo.item = lines[position++];
                    cargo.qty = MyFixedPoint.DeserializeStringSafe(lines[position++]);
                    stage.cargo.Add(cargo);
                }

                job.stages.Add(stage);
            }
            return job;
        }
    }
}

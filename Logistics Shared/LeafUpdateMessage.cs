using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using EmptyKeys.UserInterface.Generated.DataTemplatesStoreBlock_Bindings;
using Sandbox.Game.EntityComponents;
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

namespace IngameScript
{
    public class LeafUpdateMessage
    {
        const bool ADD_ITEMS_TO_INI = false;

        public string gridName;
        public List<string> docks;
        public Dictionary<string, MyFixedPoint> exports;
        public Dictionary<string, MyFixedPoint> imports;

        static readonly System.Text.RegularExpressions.Regex samName = new System.Text.RegularExpressions.Regex(@"\[SAM .*Name=([\w]+).*\]");

        internal LeafUpdateMessage() {}
        public LeafUpdateMessage(MyGridProgram parent)
        {
            gridName = parent.Me.CubeGrid.CustomName;
            docks = new List<string>();
            exports = new Dictionary<string, MyFixedPoint>();
            imports = new Dictionary<string, MyFixedPoint>();

            MyIni ini = new MyIni();
            if (!ini.TryParse(parent.Me.CustomData)) return;
            bool iniDirty = false;
            var stored = new Dictionary<string, MyFixedPoint>();


            if (!ini.ContainsSection("General")) {
                iniDirty = true;
                ini.AddSection("General");
                ini.Set("General", "Docks", "");
            } else {
                var dockList = ini.Get("General", "Docks").ToString();
                var dockNames = dockList.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

                var connectors = new List<IMyShipConnector>();
                parent.GridTerminalSystem.GetBlocksOfType<IMyShipConnector>(connectors, b => {
                    return b.CubeGrid == parent.Me.CubeGrid && b.Status == MyShipConnectorStatus.Unconnected && b.CustomName.Contains("[SAM ");
                });
                foreach (var conn in connectors) {
                    var match = samName.Match(conn.CustomName);
                    if (match.Success) {
                        var name = match.Groups[1].Captures[0].Value;
                        parent.Echo($"Checking connector named: {name}");
                        if (dockNames.Contains(name)) {
                            parent.Echo("Adding.");
                            docks.Add(name);
                        } else {
                            parent.Echo("Not in Docks list.");
                        }
                    }
                }
            }

            var containers = new List<IMyCargoContainer>();
            var storedItems = new List<MyInventoryItem>();
            parent.GridTerminalSystem.GetBlocksOfType<IMyCargoContainer>(containers, b => b.CubeGrid == parent.Me.CubeGrid);
            foreach (var c in containers) {
                var inventory = c.GetInventory(0);
                inventory.GetItems(storedItems); // Appends items to the list.
            }

            foreach (var item in storedItems) {
                string type = item.Type.ToString();
                if (!stored.ContainsKey(type)) stored[type] = item.Amount;
                else stored[type] += item.Amount;

                if (ADD_ITEMS_TO_INI) {
                    // MyObjectBuilder_Whatever -> Whatever
                    type = item.Type.TypeId.Substring(16);
                    if (!ini.ContainsKey(type, item.Type.SubtypeId))
                    {
                        iniDirty = true;
                        ini.Set(type, item.Type.SubtypeId, "0");
                    }
                }
            }

            processCategory(ini, "Ore", stored, ref iniDirty, parent);
            processCategory(ini, "Ingot", stored, ref iniDirty, parent);
            processCategory(ini, "Component", stored, ref iniDirty, parent);
            processCategory(ini, "AmmoMagazine", stored, ref iniDirty, parent);
            processCategory(ini, "SeedItem", stored, ref iniDirty, parent);
            processCategory(ini, "ConsumableItem", stored, ref iniDirty, parent);
            processCategory(ini, "PhysicalGunObject", stored, ref iniDirty, parent);

            if (iniDirty) parent.Me.CustomData = ini.ToString();
        }

        void processCategory(MyIni ini, string category, Dictionary<string, MyFixedPoint> stored, ref bool iniDirty, MyGridProgram parent)
        {
            if (!ini.ContainsSection(category)) {
                iniDirty = true;
                ini.AddSection(category);
                return;
            }

            List<MyIniKey> keys = new List<MyIniKey>();
            ini.GetKeys(category, keys);
            foreach (var key in keys) {
                var type = $"{category}/{key.Name}";
                var entry = ini.Get(category, key.Name).ToString();

                bool import = false;
                bool export = false;
                do {
                    if (entry.EndsWith("I")) {
                        import = true;
                        entry = entry.Substring(0, entry.Length - 1);
                        continue;
                    }
                    if (entry.EndsWith("E")) {
                        export = true;
                        entry = entry.Substring(0, entry.Length - 1);
                        continue;
                    }
                    break;
                } while (true);

                var target = MyFixedPoint.DeserializeString(entry);
                var current = stored.ContainsKey("MyObjectBuilder_" + type) ? stored["MyObjectBuilder_" + type] : MyFixedPoint.Zero;

                parent.Echo($"{type}: {current}/{target} " + (import ? 'I' : '-') + (export ? 'E' : '-'));

                if (target > current && import) {
                    parent.Echo($"  Importing {target - current}");
                    imports[type] = target - current;
                }
                if (current > target && export) {
                    parent.Echo($"  Exporting {current - target}");
                    exports[type] = current - target;
                }
            }
        }
        public string serialize()
        {
            var sb = new StringBuilder();

            // Section 0
            sb.Append(gridName);
            sb.Append("\n");

            // Section 1
            foreach (string dock in docks) {
                sb.Append(dock);
                sb.Append("\t");
            }
            sb.Append("\n");

            // Section 2
            foreach (var entry in exports) {
                sb.Append(entry.Key);
                sb.Append("=");
                sb.Append(entry.Value.SerializeString());
                sb.Append("\t");
            }
            sb.Append("\n");

            // Section 3
            foreach (var entry in imports) {
                sb.Append(entry.Key);
                sb.Append("=");
                sb.Append(entry.Value.SerializeString());
                sb.Append("\t");
            }
            sb.Append("\n");

            return sb.ToString();
        }

        public static LeafUpdateMessage deserialize(string message, MyGridProgram parent)
        {
            LeafUpdateMessage lum = new LeafUpdateMessage();

            string[] sections = message.Split('\n');
            //parent.Echo($"Got {sections.Length} sections.");

            lum.gridName = sections[0];

            lum.docks = new List<String>(sections[1].Split(new char[] { '\t' }, StringSplitOptions.RemoveEmptyEntries));

            lum.exports = new Dictionary<string, MyFixedPoint>();
            string[] exports = sections[2].Split('\t');
            foreach (var export in exports) {
                if (export == "") continue;
                string[] item = export.Split('=');
                lum.exports.Add(item[0], MyFixedPoint.DeserializeString(item[1]));
            }

            lum.imports = new Dictionary<string, MyFixedPoint>();
            string[] imports = sections[3].Split('\t');
            foreach (var import in imports) {
                if (import == "") continue;
                string[] item = import.Split('=');
                lum.imports.Add(item[0], MyFixedPoint.DeserializeString(item[1]));
            }

            return lum;
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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
    public partial class Program : MyGridProgram {
        Random rng;
        public Program() {
            Runtime.UpdateFrequency = UpdateFrequency.Update100;
            rng = new Random();
        }

        public void Save() {
            
        }

        static int counter = 0;
        public void Main(string argument, UpdateType updateSource) {
            if (--counter <= 0) {
                counter = rng.Next(10, 20);

                var msg = new LeafUpdateMessage(this).serialize();
                Echo($"Sending: {msg}");

                MyIni ini = new MyIni();
                ini.TryParse(Me.CustomData);
                if (!ini.ContainsSection("General") || !ini.ContainsKey("General", "Channel")) {
                    ini.Set("General", "Channel", "Default");
                    Me.CustomData = ini.ToString();
                }
                string channel = "MaeyLogistics-" + ini.Get("General", "Channel").ToString("Default");

                IGC.SendBroadcastMessage(channel, "LeafUpdate\n"+msg);
            }
        }
    }
}

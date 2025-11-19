using Sandbox.Game.AI.Pathfinding;
using Sandbox.Game.Replication.StateGroups;
using Sandbox.ModAPI.Ingame;
using System;
using System.Collections.Generic;
using System.Text;
using System.Xml;
using VRage;

namespace IngameScript
{
    public class ShipUpdateMessage
    {
        public static char[] newline = new char[] { '\n' };

        public string shipName;
        public string state;
        public MyFixedPoint maxVolume;
        public MyFixedPoint maxMass;
        public string destination;
        public string dock;
        public ShipJob job;

        public string serialize() {
            var sb = new StringBuilder();
            sb.Append(shipName);
            sb.Append("\n");
            sb.Append(state);
            sb.Append("\n");
            sb.Append(maxVolume.SerializeString());
            sb.Append("\n");
            sb.Append(maxMass.SerializeString());
            sb.Append("\n");
            sb.Append(destination);
            sb.Append("\n");
            sb.Append(dock);
            sb.Append("\n");
            if (job != null) sb.Append(job.serialize());
            return sb.ToString();
        }
        public static ShipUpdateMessage deserialize(string data) {
            ShipUpdateMessage msg = new ShipUpdateMessage();
            string[] parts = data.Split(newline, 7);
            msg.shipName = parts[0];
            msg.state = parts[1];
            msg.maxVolume = MyFixedPoint.DeserializeString(parts[2]);
            msg.maxMass = MyFixedPoint.DeserializeString(parts[3]);
            msg.destination = parts[4];
            msg.dock = parts[5];

            if (parts.Length > 6 && parts[6] != "") {
                msg.job = ShipJob.deserialize(parts[6]);
            } else {
                msg.job = null;
            }
            return msg;
        }
    }
}

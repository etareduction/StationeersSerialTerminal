using Assets.Scripts;
using Assets.Scripts.Networking;
using LaunchPadBooster.Networking;

namespace SerialTerminal
{
    /// <summary>Client → server: a line the player typed into a terminal.</summary>
    public class TerminalInputMessage : ModNetworkMessage<TerminalInputMessage>
    {
        public long TerminalId;
        public string Text;

        public override void Serialize(RocketBinaryWriter writer)
        {
            writer.WriteInt64(TerminalId);
            writer.WriteString(Text ?? string.Empty);
        }

        public override void Deserialize(RocketBinaryReader reader)
        {
            TerminalId = reader.ReadInt64();
            Text = reader.ReadString();
        }

        public override void Process(long hostId)
        {
            if (!GameManager.RunSimulation)
            {
                return;
            }
            if (Referencable.Exists<SerialTerminalDevice>(TerminalId, out SerialTerminalDevice terminal))
            {
                terminal.EnqueueInputLine(Text ?? string.Empty);
            }
        }
    }
}

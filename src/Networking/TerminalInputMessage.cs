using Assets.Scripts;
using Assets.Scripts.Networking;
using LaunchPadBooster.Networking;
using SerialTerminal.Devices;

namespace SerialTerminal.Networking
{
    /// <summary>Client → server: raw keystrokes the player typed into a terminal.</summary>
    public class TerminalInputMessage : INetworkMessage
    {
        public long TerminalId;
        public string Text;

        public void Serialize(RocketBinaryWriter writer)
        {
            writer.WriteInt64(TerminalId);
            writer.WriteString(Text ?? string.Empty);
        }

        public void Deserialize(RocketBinaryReader reader)
        {
            TerminalId = reader.ReadInt64();
            Text = reader.ReadString();
        }

        public void Process(long clientId)
        {
            if (!GameManager.RunSimulation)
            {
                return;
            }
            if (Referencable.Exists(TerminalId, out SerialTerminalDevice terminal))
            {
                // Deserialize ran before Process, so Text is never null here.
                terminal.EnqueueInput(Text);
            }
        }
    }
}

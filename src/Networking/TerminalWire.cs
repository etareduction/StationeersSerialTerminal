using Assets.Scripts.Networking;
using SerialTerminal.Core;

namespace SerialTerminal.Networking
{
    /// <summary>
    /// Wire format of the terminal's two sync payloads (screen bit 1024, status
    /// bit 2048), as extension members on the game's binary reader/writer.
    /// Write and Read pairs must mirror each other exactly.
    /// </summary>
    internal static class TerminalWire
    {
        extension(RocketBinaryWriter writer)
        {
            /// <summary>Writes the screen payload: text + colour plane + cursor cell.</summary>
            /// <param name="screen">Wire form of the screen.</param>
            public void Write(ScreenContent screen)
            {
                writer.WriteString(screen.Text);
                writer.WriteString(screen.Colors);
                writer.WriteByte((byte)screen.CursorRow);
                writer.WriteByte((byte)screen.CursorCol);
            }

            /// <summary>Writes the status payload: overflow + FIFO depth.</summary>
            /// <param name="status">Wire form of the status.</param>
            public void Write(TerminalStatus status)
            {
                writer.WriteBoolean(status.Overflow);
                writer.WriteUInt16((ushort)status.RxCount);
            }
        }

        extension(RocketBinaryReader reader)
        {
            /// <summary>Reads the screen payload written by Write(ScreenContent).</summary>
            public ScreenContent ReadScreenContent()
            {
                return new ScreenContent
                {
                    Text = reader.ReadString(),
                    Colors = reader.ReadString(),
                    CursorRow = reader.ReadByte(),
                    CursorCol = reader.ReadByte()
                };
            }

            /// <summary>Reads the status payload written by Write(TerminalStatus).</summary>
            public TerminalStatus ReadTerminalStatus()
            {
                return new TerminalStatus
                {
                    Overflow = reader.ReadBoolean(),
                    RxCount = reader.ReadUInt16()
                };
            }
        }
    }
}

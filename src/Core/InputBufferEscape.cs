using System.Globalization;
using System.Text;

namespace SerialTerminal.Core
{
    /// <summary>
    /// Control characters are not valid in XML 1.0, so the saved input FIFO
    /// escapes them as \xNN (plus \\ for a literal backslash).
    /// Pure string functions; the save layer applies them at the XML boundary.
    /// </summary>
    internal static class InputBufferEscape
    {
        /// <summary>Escapes raw FIFO contents for storage in save XML.</summary>
        /// <param name="text">Raw FIFO contents.</param>
        public static string Escape(string text)
        {
            StringBuilder sb = new(text.Length);
            foreach (char c in text)
            {
                if (c == '\\')
                {
                    _ = sb.Append("\\\\");
                    continue;
                }
                if (c is < ' ' or TerminalState.CH_DEL)
                {
                    _ = sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    continue;
                }
                _ = sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Reverses <see cref="Escape"/> when loading a save.</summary>
        /// <param name="text">Escaped FIFO contents from a save.</param>
        public static string Unescape(string text)
        {
            StringBuilder sb = new(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c != '\\' || i == text.Length - 1)
                {
                    _ = sb.Append(c);
                    continue;
                }
                char next = text[++i];
                switch (next)
                {
                    case '\\': _ = sb.Append('\\'); break;
                    case 'x':
                        if (i + 2 < text.Length
                            && int.TryParse(text.Substring(i + 1, 2),
                                NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                        {
                            _ = sb.Append((char)code);
                            i += 2;
                        }
                        break;
                    default: _ = sb.Append(next); break;
                }
            }
            return sb.ToString();
        }
    }
}

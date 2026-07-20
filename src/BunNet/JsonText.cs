using System.Text;

namespace BunNet
{
    /// <summary>Kleine JSON-Helfer, damit die Bibliothek ohne externe Pakete auskommt.</summary>
    internal static class JsonText
    {
        /// <summary>Verpackt einen Text als JSON-String inklusive Anführungszeichen.</summary>
        public static string Escape(string value)
        {
            StringBuilder result = new StringBuilder(value.Length + 2);
            result.Append('"');
            foreach (char c in value)
            {
                if (c == '"') result.Append("\\\"");
                else if (c == '\\') result.Append("\\\\");
                else if (c == '\b') result.Append("\\b");
                else if (c == '\f') result.Append("\\f");
                else if (c == '\n') result.Append("\\n");
                else if (c == '\r') result.Append("\\r");
                else if (c == '\t') result.Append("\\t");
                else if (c < ' ') result.Append("\\u").Append(((int)c).ToString("x4"));
                else result.Append(c);
            }
            result.Append('"');
            return result.ToString();
        }

        /// <summary>
        /// Liest ein String-Feld auf der obersten Ebene eines JSON-Objekts,
        /// z. B. <c>username</c> aus <c>{"username":"admin"}</c>. Liefert <c>""</c>,
        /// wenn das Feld fehlt, kein String ist oder das JSON ungültig ist.
        /// </summary>
        public static string ReadStringField(string json, string fieldName)
        {
            int i = 0;
            int depth = 0;
            while (i < json.Length)
            {
                char c = json[i];
                if (c == '{' || c == '[')
                {
                    depth++;
                    i++;
                }
                else if (c == '}' || c == ']')
                {
                    depth--;
                    i++;
                }
                else if (c == '"')
                {
                    string token = ReadStringToken(json, ref i);
                    int next = i;
                    while (next < json.Length && char.IsWhiteSpace(json[next])) next++;

                    // Folgt ein ':' ist das Token ein Feldname, sonst ein Wert.
                    if (next < json.Length && json[next] == ':')
                    {
                        i = next + 1;
                        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                        if (depth == 1 && token == fieldName)
                        {
                            if (i < json.Length && json[i] == '"')
                                return ReadStringToken(json, ref i);
                            return ""; // Feld vorhanden, aber kein String
                        }
                    }
                }
                else
                {
                    i++;
                }
            }
            return "";
        }

        /// <summary>Liest ab dem öffnenden <c>"</c> einen JSON-String und löst Escapes auf.</summary>
        private static string ReadStringToken(string json, ref int i)
        {
            StringBuilder result = new StringBuilder();
            i++; // öffnendes '"'
            while (i < json.Length && json[i] != '"')
            {
                char c = json[i++];
                if (c != '\\' || i >= json.Length)
                {
                    result.Append(c);
                    continue;
                }
                char escape = json[i++];
                if (escape == 'n') result.Append('\n');
                else if (escape == 't') result.Append('\t');
                else if (escape == 'r') result.Append('\r');
                else if (escape == 'b') result.Append('\b');
                else if (escape == 'f') result.Append('\f');
                else if (escape == 'u' && i + 4 <= json.Length)
                {
                    int code;
                    if (int.TryParse(json.Substring(i, 4), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out code))
                    {
                        result.Append((char)code);
                    }
                    i += 4;
                }
                else result.Append(escape); // \" \\ \/ und alles Übrige
            }
            i++; // schließendes '"'
            return result.ToString();
        }
    }
}

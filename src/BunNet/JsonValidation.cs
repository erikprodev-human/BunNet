namespace BunNet
{
    /// <summary>
    /// Automatische Eingangsprüfung: In JSON-Bodys darf kein Wert <c>null</c>,
    /// leer oder nur Leerraum sein. Ein einfacher Scanner läuft über den Text,
    /// überspringt String-Inhalte korrekt (inkl. Escapes) und merkt sich den
    /// zuletzt gesehenen Feldnamen für eine verständliche Fehlermeldung.
    /// </summary>
    internal static class JsonValidation
    {
        /// <summary>
        /// Prüft einen JSON-Text. Liefert <c>null</c>, wenn alles in Ordnung ist,
        /// sonst eine Fehlermeldung für den Client.
        /// </summary>
        public static string? FindInvalidValue(string json)
        {
            string lastFieldName = "?";
            int i = 0;

            while (i < json.Length)
            {
                char c = json[i];

                if (c == '"')
                {
                    // String-Token einlesen (Escapes überspringen, damit \" nicht als Ende zählt).
                    int start = i + 1;
                    int end = start;
                    while (end < json.Length && json[end] != '"')
                    {
                        if (json[end] == '\\') end++;
                        end++;
                    }
                    if (end >= json.Length) return null; // abgeschnittenes JSON — nicht unsere Baustelle
                    string token = json.Substring(start, end - start);
                    i = end + 1;

                    // Folgt ein ':' ist das Token ein Feldname, sonst ein Wert.
                    int next = i;
                    while (next < json.Length && char.IsWhiteSpace(json[next])) next++;
                    if (next < json.Length && json[next] == ':')
                    {
                        if (token.Trim().Length == 0)
                            return "Leerer Feldname ist nicht erlaubt.";
                        lastFieldName = token;
                        i = next + 1;
                    }
                    else if (token.Trim().Length == 0)
                    {
                        return "Feld '" + lastFieldName + "' ist leer.";
                    }
                }
                else if (c == 'n' && i + 4 <= json.Length && json.Substring(i, 4) == "null")
                {
                    // 'null' kann außerhalb von Strings nur das JSON-Literal sein.
                    return "Feld '" + lastFieldName + "' ist null.";
                }
                else
                {
                    i++;
                }
            }

            return null;
        }
    }
}

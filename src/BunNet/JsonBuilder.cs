using System;
using System.Globalization;
using System.Text;

namespace BunNet
{
    /// <summary>
    /// Baut ein flaches JSON-Objekt ohne Reflection zusammen — funktioniert damit
    /// uneingeschränkt unter Native AOT und netstandard2.1. Gedacht für Antworten
    /// von Endpoints:
    /// <code>
    /// return BunResponse.Json(new JsonBuilder()
    ///     .Add("token", token)
    ///     .Add("user", username));
    /// </code>
    /// </summary>
    public sealed class JsonBuilder
    {
        private readonly StringBuilder _fields = new StringBuilder(64);

        /// <summary>Fügt ein String-Feld hinzu (Wert wird JSON-konform escaped).</summary>
        public JsonBuilder Add(string name, string value)
        {
            return AddRaw(name, JsonText.Escape(value));
        }

        /// <summary>Fügt ein Ganzzahl-Feld hinzu.</summary>
        public JsonBuilder Add(string name, long value)
        {
            return AddRaw(name, value.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Fügt ein Gleitkomma-Feld hinzu.</summary>
        public JsonBuilder Add(string name, double value)
        {
            return AddRaw(name, value.ToString("R", CultureInfo.InvariantCulture));
        }

        /// <summary>Fügt ein Wahrheitswert-Feld hinzu.</summary>
        public JsonBuilder Add(string name, bool value)
        {
            return AddRaw(name, value ? "true" : "false");
        }

        /// <summary>Fügt einen Zeitstempel im ISO-8601-Format hinzu.</summary>
        public JsonBuilder Add(string name, DateTimeOffset value)
        {
            return AddRaw(name, JsonText.Escape(value.ToString("o", CultureInfo.InvariantCulture)));
        }

        /// <summary>Fügt ein <c>null</c>-Feld hinzu.</summary>
        public JsonBuilder AddNull(string name)
        {
            return AddRaw(name, "null");
        }

        /// <summary>Fügt ein verschachteltes Objekt hinzu.</summary>
        public JsonBuilder Add(string name, JsonBuilder value)
        {
            return AddRaw(name, value.ToString());
        }

        private JsonBuilder AddRaw(string name, string rawValue)
        {
            if (_fields.Length > 0) _fields.Append(',');
            _fields.Append(JsonText.Escape(name)).Append(':').Append(rawValue);
            return this;
        }

        /// <summary>Liefert das fertige JSON-Objekt, z. B. <c>{"token":"…","user":"admin"}</c>.</summary>
        public override string ToString()
        {
            return "{" + _fields + "}";
        }
    }
}

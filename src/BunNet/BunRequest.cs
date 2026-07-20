using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace BunNet
{
    /// <summary>Signatur eines in C# implementierten HTTP-Endpoints.</summary>
    public delegate Task<BunResponse> BunEndpointHandler(BunRequest request);

    /// <summary>
    /// Ein von Bun weitergeleiteter HTTP-Request. Alle Daten sind bereits
    /// vollständig eingelesen — Handler arbeiten nie mit halbfertigen Requests.
    /// </summary>
    public sealed class BunRequest
    {
        private Dictionary<string, string>? _query;
        private string? _bodyText;

        internal BunRequest(
            string method,
            string path,
            string queryString,
            string remoteAddress,
            IReadOnlyDictionary<string, string> headers,
            byte[] body,
            string bodyFilePath,
            long bodyFileSize)
        {
            Method = method;
            Path = path;
            QueryString = queryString;
            RemoteAddress = remoteAddress;
            Headers = headers;
            Body = body;
            BodyFilePath = bodyFilePath;
            _bodyFileSize = bodyFileSize;
        }

        private readonly long _bodyFileSize;

        /// <summary>HTTP-Methode, z. B. <c>POST</c>.</summary>
        public string Method { get; }

        /// <summary>Pfad der URL, z. B. <c>/api/login</c>.</summary>
        public string Path { get; }

        /// <summary>Roher Query-String ohne führendes <c>?</c>.</summary>
        public string QueryString { get; }

        /// <summary>IP-Adresse des Clients.</summary>
        public string RemoteAddress { get; }

        /// <summary>Request-Header (Namen sind case-insensitiv).</summary>
        public IReadOnlyDictionary<string, string> Headers { get; }

        /// <summary>
        /// Vollständiger Request-Body. Bei Upload-Endpoints
        /// (<see cref="BunServer.MapUpload(string, BunEndpointHandler)"/>) leer —
        /// die Daten liegen dann in der Datei <see cref="BodyFilePath"/>.
        /// </summary>
        public byte[] Body { get; }

        /// <summary>
        /// Pfad der Temp-Datei mit dem Body bei Upload-Endpoints; <c>""</c> wenn der
        /// Body wie üblich in <see cref="Body"/> liegt. Am einfachsten per
        /// <see cref="SaveBodyTo"/> übernehmen — nicht übernommene Temp-Dateien
        /// räumt die Bibliothek nach dem Handler automatisch weg.
        /// </summary>
        public string BodyFilePath { get; }

        /// <summary>Größe des Bodys in Bytes — egal ob im Speicher oder als Datei.</summary>
        public long BodyLength => BodyFilePath == "" ? Body.Length : _bodyFileSize;

        /// <summary>
        /// Speichert den Body als Datei unter <paramref name="targetPath"/>.
        /// Bei Upload-Endpoints wird die Temp-Datei verschoben (kein Kopieren,
        /// kein RAM — auch bei vielen GiB), sonst wird <see cref="Body"/> geschrieben.
        /// Eine vorhandene Zieldatei wird ersetzt.
        /// </summary>
        public void SaveBodyTo(string targetPath)
        {
            if (BodyFilePath == "")
            {
                System.IO.File.WriteAllBytes(targetPath, Body);
                return;
            }

            if (System.IO.File.Exists(targetPath)) System.IO.File.Delete(targetPath);
            try
            {
                System.IO.File.Move(BodyFilePath, targetPath);
            }
            catch (System.IO.IOException)
            {
                // Ziel liegt auf einem anderen Laufwerk — dann kopieren.
                System.IO.File.Copy(BodyFilePath, targetPath, true);
                System.IO.File.Delete(BodyFilePath);
            }
        }

        /// <summary>Body als UTF-8-Text.</summary>
        public string BodyAsText() => _bodyText ?? (_bodyText = Encoding.UTF8.GetString(Body));

        /// <summary>
        /// Liest ein String-Feld aus dem JSON-Body, z. B. <c>request["username"]</c>.
        /// Liefert <c>""</c>, wenn das Feld fehlt oder kein String ist — dank der
        /// automatischen Eingangsprüfung ist ein vorhandener Wert nie leer.
        /// </summary>
        public string this[string fieldName] => JsonText.ReadStringField(BodyAsText(), fieldName);

        /// <summary>Bearer-Token aus dem Authorization-Header; <c>""</c>, wenn keins gesendet wurde.</summary>
        public string BearerToken
        {
            get
            {
                string authorization = GetHeader("Authorization") ?? "";
                if (authorization.StartsWith("Bearer ", StringComparison.Ordinal))
                    return authorization.Substring("Bearer ".Length);
                return "";
            }
        }

        /// <summary>Liefert einen Header oder <c>null</c>, falls nicht vorhanden.</summary>
        public string? GetHeader(string name)
        {
            string? value;
            if (Headers.TryGetValue(name, out value)) return value;
            return null;
        }

        /// <summary>Liefert einen Query-Parameter (URL-dekodiert) oder <c>null</c>.</summary>
        public string? GetQuery(string name)
        {
            if (_query == null) _query = ParseQuery(QueryString);
            string? value;
            if (_query.TryGetValue(name, out value)) return value;
            return null;
        }

        private static Dictionary<string, string> ParseQuery(string queryString)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in queryString.Split('&'))
            {
                if (pair.Length == 0) continue;
                var eq = pair.IndexOf('=');
                var name = eq < 0 ? pair : pair.Substring(0, eq);
                var value = eq < 0 ? "" : pair.Substring(eq + 1);
                result[Uri.UnescapeDataString(name.Replace('+', ' '))] =
                    Uri.UnescapeDataString(value.Replace('+', ' '));
            }
            return result;
        }

#if NET
        /// <summary>
        /// Deserialisiert den JSON-Body in <typeparamref name="T"/> (per Reflection —
        /// unter Native AOT stattdessen die Überladung mit <c>JsonTypeInfo</c> verwenden).
        /// </summary>
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Nutzt Reflection-basierte JSON-Deserialisierung; unter Trimming/AOT die Überladung mit JsonTypeInfo verwenden.")]
        [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Nutzt Reflection-basierte JSON-Deserialisierung; unter Native AOT die Überladung mit JsonTypeInfo verwenden.")]
        public T? BodyAsJson<T>(System.Text.Json.JsonSerializerOptions? options = null) =>
            System.Text.Json.JsonSerializer.Deserialize<T>(Body, options ?? BunResponse.DefaultJsonOptions);

        /// <summary>
        /// Deserialisiert den JSON-Body in <typeparamref name="T"/> — AOT-sicher über
        /// einen source-generierten <c>JsonSerializerContext</c>.
        /// </summary>
        public T? BodyAsJson<T>(System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
            System.Text.Json.JsonSerializer.Deserialize(Body, typeInfo);
#endif
    }

    /// <summary>Antwort eines C#-Endpoints. Über die statischen Factories erzeugen.</summary>
    public sealed class BunResponse
    {
        /// <summary>HTTP-Statuscode.</summary>
        public int Status { get; set; } = 200;

        /// <summary>Response-Header.</summary>
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Response-Body (leer = kein Body).</summary>
        public byte[] Body { get; set; } = Array.Empty<byte>();

        /// <summary>Antwort mit reinem Text (<c>text/plain</c>).</summary>
        public static BunResponse Text(string text, int status = 200) =>
            WithBody(text, "text/plain; charset=utf-8", status);

        /// <summary>Antwort mit HTML (<c>text/html</c>).</summary>
        public static BunResponse Html(string html, int status = 200) =>
            WithBody(html, "text/html; charset=utf-8", status);

        /// <summary>Antwort mit einem bereits fertig serialisierten JSON-String.</summary>
        public static BunResponse JsonText(string json, int status = 200) =>
            WithBody(json, "application/json; charset=utf-8", status);

        /// <summary>Antwort nur mit Statuscode (z. B. 204, 401, 404).</summary>
        public static BunResponse StatusCode(int status) => new BunResponse { Status = status };

        /// <summary>Fügt einen Header hinzu und gibt die Antwort zur Verkettung zurück.</summary>
        public BunResponse WithHeader(string name, string value)
        {
            Headers[name] = value;
            return this;
        }

        private static BunResponse WithBody(string content, string contentType, int status)
        {
            var response = new BunResponse
            {
                Status = status,
                Body = Encoding.UTF8.GetBytes(content),
            };
            response.Headers["Content-Type"] = contentType;
            return response;
        }

        /// <summary>
        /// Antwort aus einem <see cref="JsonBuilder"/> — ohne Reflection, funktioniert
        /// damit uneingeschränkt unter Native AOT und netstandard2.1.
        /// </summary>
        public static BunResponse Json(JsonBuilder json) =>
            JsonText(json.ToString(), 200);

        /// <summary>Antwort aus einem <see cref="JsonBuilder"/> mit Statuscode (siehe <see cref="Json(JsonBuilder)"/>).</summary>
        public static BunResponse Json(JsonBuilder json, int status) =>
            JsonText(json.ToString(), status);

#if NET
        internal static readonly System.Text.Json.JsonSerializerOptions DefaultJsonOptions =
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);

        /// <summary>
        /// Serialisiert ein Objekt als JSON-Antwort (nur .NET, nicht netstandard2.1;
        /// per Reflection — unter Native AOT stattdessen <see cref="Json(JsonBuilder, int)"/>
        /// oder die Überladung mit <c>JsonTypeInfo</c> verwenden).
        /// </summary>
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Nutzt Reflection-basierte JSON-Serialisierung; unter Trimming/AOT JsonBuilder oder die Überladung mit JsonTypeInfo verwenden.")]
        [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Nutzt Reflection-basierte JSON-Serialisierung; unter Native AOT JsonBuilder oder die Überladung mit JsonTypeInfo verwenden.")]
        public static BunResponse Json<T>(T value, int status = 200,
            System.Text.Json.JsonSerializerOptions? options = null)
        {
            var response = new BunResponse
            {
                Status = status,
                Body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, options ?? DefaultJsonOptions),
            };
            response.Headers["Content-Type"] = "application/json; charset=utf-8";
            return response;
        }

        /// <summary>
        /// Serialisiert ein Objekt als JSON-Antwort — AOT-sicher über einen
        /// source-generierten <c>JsonSerializerContext</c>.
        /// </summary>
        public static BunResponse Json<T>(T value,
            System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, int status = 200)
        {
            var response = new BunResponse
            {
                Status = status,
                Body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, typeInfo),
            };
            response.Headers["Content-Type"] = "application/json; charset=utf-8";
            return response;
        }
#endif
    }
}

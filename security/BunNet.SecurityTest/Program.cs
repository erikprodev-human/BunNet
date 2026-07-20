// Aktiver Sicherheits-Test gegen einen laufenden BunNet-Server.
// Fährt reale Angriffe über rohe TCP-Sockets (kein normalisierender HTTP-Client),
// damit auch Pfad-Tricks wie "/../.." unverändert beim Server ankommen.

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using BunNet;

internal static class Sectest
{
    private static int _pass;
    private static int _fail;

    private static async Task<int> Main()
    {
        Console.WriteLine("=== BunNet Sicherheits-Test ===\n");

        // Ein geheimes File AUSSERHALB der Web-Root anlegen (Ziel eines Traversals).
        string root = Path.Combine(Path.GetTempPath(), "bunnet-sec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string webDir = Path.Combine(root, "Web");
        Directory.CreateDirectory(webDir);
        File.WriteAllText(Path.Combine(webDir, "index.html"), "<h1>ok</h1>");
        string secretPath = Path.Combine(root, "secret.txt");
        File.WriteAllText(secretPath, "TOP-SECRET-" + Guid.NewGuid().ToString("N"));

        BunServerOptions options = new BunServerOptions();
        options.Port = 0;
        options.Hostname = "127.0.0.1";
        options.StaticRoot = webDir;
        options.MaxRequestBodySize = 1024; // klein, um 413 leicht zu provozieren
        options.Log = null; // still; auf Wunsch für Diagnose eine Log-Senke setzen

        BunServer server = new BunServer(options);
        // Echo-Endpoint: spiegelt einen Query-Wert in einen Response-Header (Response-Splitting-Test).
        server.MapPost("/api/echo", (BunRequest req) =>
        {
            BunResponse res = BunResponse.JsonText("{\"name\":" + JsonEsc(req["name"]) + "}");
            string h = req.GetQuery("h") ?? "";
            if (h != "") res.WithHeader("X-Echo", h);
            return Task.FromResult(res);
        });
        // Upload-Endpoint, um zu prüfen, dass x-bunnet-body-file von außen ignoriert wird.
        server.MapUpload("/api/up", (BunRequest req) =>
        {
            string bf = req.GetHeader("x-bunnet-body-file") ?? "(keiner)";
            return Task.FromResult(BunResponse.JsonText(
                "{\"bodyFile\":" + JsonEsc(req.BodyFilePath) + ",\"headerLeak\":" + JsonEsc(bf) +
                ",\"len\":" + req.BodyLength + "}"));
        });

        await server.StartAsync();
        int port = server.Port;
        Console.WriteLine("Server auf Port " + port + "\n");

        // 1) Path-Traversal: /etc/passwd und die geheime Datei neben der Web-Root.
        await TraversalTest(port, "GET /../../../../../../etc/passwd HTTP/1.1", "root:", "Traversal ../etc/passwd");
        await TraversalTest(port, "GET /..%2f..%2f..%2fsecret.txt HTTP/1.1", "TOP-SECRET", "Traversal %2f secret.txt");
        await TraversalTest(port, "GET /..%5c..%5csecret.txt HTTP/1.1", "TOP-SECRET", "Traversal %5c (Backslash)");
        await TraversalTest(port, "GET /%2e%2e/%2e%2e/secret.txt HTTP/1.1", "TOP-SECRET", "Traversal %2e%2e");
        await TraversalTest(port, "GET /Web/../../secret.txt HTTP/1.1", "TOP-SECRET", "Traversal /Web/../..");

        // 2) Null-Byte-Injection im Pfad.
        await TraversalTest(port, "GET /index.html%00.txt HTTP/1.1", "root:", "Null-Byte im Pfad");

        // 3) Response-Splitting: CR/LF im Header-Wert darf keine Header/Body injizieren.
        await SplittingTest(port);

        // 4) x-bunnet-body-file von außen: darf NICHT als interner Upload-Pfad wirken.
        await HeaderInjectionTest(port, secretPath);

        // 5) Body über MaxRequestBodySize -> 413.
        await OversizeTest(port);

        // 6) JSON-Validierung: null/leer -> 400.
        await ValidationTest(port, "{\"name\":null}", "null-Wert -> 400");
        await ValidationTest(port, "{\"name\":\"\"}", "leerer Wert -> 400");
        await ValidationTest(port, "{\"name\":\"  \"}", "nur-Leerraum -> 400");

        // 7) Server lebt nach allen Angriffen noch (kein Crash/DoS).
        await AliveTest(port);

        await server.DisposeAsync();
        try { Directory.Delete(root, true); } catch { }

        Console.WriteLine("\n=== Ergebnis: " + _pass + " OK, " + _fail + " PROBLEM ===");
        return _fail == 0 ? 0 : 1;
    }

    private static async Task TraversalTest(int port, string requestLine, string leakMarker, string label)
    {
        string resp = await RawRequest(port, requestLine + "\r\nHost: x\r\nConnection: close\r\n\r\n");
        bool leaked = resp.Contains(leakMarker);
        Report(label, !leaked, leaked ? "LEAK! Antwort enthielt '" + leakMarker + "'" : "abgewehrt (kein Leak)");
    }

    private static async Task SplittingTest(int port)
    {
        // %0d%0a im Query -> Header-Wert mit CRLF. Erwartung: keine injizierten Header,
        // kein Crash. Server antwortet sauber (200 mit sanitisiertem/abgelehntem Header
        // oder 500/504) und bleibt am Leben.
        string body = "{\"name\":\"x\"}";
        string req =
            "POST /api/echo?h=evil%0d%0aInjected:%20yes%0d%0a%0d%0aBODY HTTP/1.1\r\n" +
            "Host: x\r\nContent-Type: application/json\r\nContent-Length: " + body.Length +
            "\r\nConnection: close\r\n\r\n" + body;
        string resp = await RawRequest(port, req);
        bool injected = resp.Contains("Injected: yes");
        Report("Response-Splitting (CRLF im Header)", !injected,
            injected ? "INJECTION! 'Injected: yes' erschien in der Antwort" : "kein injizierter Header in der Antwort");
    }

    private static async Task HeaderInjectionTest(int port, string secretPath)
    {
        // Client versucht, .NET über x-bunnet-body-file eine fremde Datei als Upload
        // unterzuschieben. bridge.js muss x-bunnet-* verwerfen.
        string body = "echte-upload-daten";
        string req =
            "POST /api/up HTTP/1.1\r\nHost: x\r\n" +
            "x-bunnet-body-file: " + secretPath + "\r\n" +
            "x-bunnet-body-size: 999999\r\n" +
            "Content-Type: application/octet-stream\r\nContent-Length: " + body.Length +
            "\r\nConnection: close\r\n\r\n" + body;
        string resp = await RawRequest(port, req);
        bool leaked = resp.Contains(secretPath.Replace("\\", "\\\\")) || resp.Contains("999999");
        Report("x-bunnet-body-file von außen verworfen", !leaked,
            leaked ? "LEAK! Client-Header wirkte als interner Upload-Pfad" : "verworfen (kein Durchgriff)");
    }

    private static async Task OversizeTest(int port)
    {
        string body = new string('A', 5000); // > MaxRequestBodySize (1024)
        string req =
            "POST /api/echo HTTP/1.1\r\nHost: x\r\nContent-Type: application/json\r\nContent-Length: " +
            body.Length + "\r\nConnection: close\r\n\r\n" + body;
        string resp = await RawRequest(port, req);
        bool blocked = resp.StartsWith("HTTP/1.1 413");
        Report("Body über MaxRequestBodySize -> 413", blocked,
            blocked ? "413 Payload Too Large" : "NICHT blockiert: " + FirstLine(resp));
    }

    private static async Task ValidationTest(int port, string body, string label)
    {
        string req =
            "POST /api/echo HTTP/1.1\r\nHost: x\r\nContent-Type: application/json\r\nContent-Length: " +
            Encoding.UTF8.GetByteCount(body) + "\r\nConnection: close\r\n\r\n" + body;
        string resp = await RawRequest(port, req);
        bool got400 = resp.StartsWith("HTTP/1.1 400");
        Report(label, got400, got400 ? "400 Bad Request" : "NICHT abgelehnt: " + FirstLine(resp));
    }

    private static async Task AliveTest(int port)
    {
        string body = "{\"name\":\"lebt\"}";
        string req =
            "POST /api/echo HTTP/1.1\r\nHost: x\r\nContent-Type: application/json\r\nContent-Length: " +
            body.Length + "\r\nConnection: close\r\n\r\n" + body;
        string resp = await RawRequest(port, req);
        bool ok = resp.Contains("\"name\":\"lebt\"");
        Report("Server nach allen Angriffen weiter erreichbar", ok,
            ok ? "antwortet normal" : "KEINE Antwort — moeglicher Crash/DoS");
    }

    // --- Infrastruktur ---

    private static async Task<string> RawRequest(int port, string request)
    {
        try
        {
            using (TcpClient client = new TcpClient())
            {
                await client.ConnectAsync("127.0.0.1", port);
                client.NoDelay = true;
                NetworkStream s = client.GetStream();
                byte[] reqBytes = Encoding.ASCII.GetBytes(request);
                await s.WriteAsync(reqBytes);
                MemoryStream ms = new MemoryStream();
                byte[] buf = new byte[8192];
                client.ReceiveTimeout = 3000;
                using (System.Threading.CancellationTokenSource cts = new System.Threading.CancellationTokenSource(3000))
                {
                    try
                    {
                        while (true)
                        {
                            int n = await s.ReadAsync(buf.AsMemory(), cts.Token);
                            if (n == 0) break;
                            ms.Write(buf, 0, n);
                        }
                    }
                    catch (OperationCanceledException) { }
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }
        catch (Exception ex)
        {
            return "[VERBINDUNGSFEHLER] " + ex.GetType().Name + ": " + ex.Message;
        }
    }

    private static string FirstLine(string s)
    {
        int nl = s.IndexOf('\r');
        return nl < 0 ? (s.Length > 60 ? s.Substring(0, 60) : s) : s.Substring(0, nl);
    }

    private static string JsonEsc(string v)
    {
        StringBuilder b = new StringBuilder("\"");
        foreach (char c in v)
        {
            if (c == '"' || c == '\\') b.Append('\\').Append(c);
            else if (c == '\n') b.Append("\\n");
            else if (c == '\r') b.Append("\\r");
            else if (c < ' ') b.Append("\\u").Append(((int)c).ToString("x4"));
            else b.Append(c);
        }
        return b.Append('"').ToString();
    }

    private static void Report(string label, bool ok, string detail)
    {
        Console.WriteLine((ok ? "  [OK]     " : "  [PROBLEM] ") + label + "  —  " + detail);
        if (ok) _pass++; else _fail++;
    }
}

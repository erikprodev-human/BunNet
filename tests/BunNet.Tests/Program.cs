// ---------------------------------------------------------------------------
// BunNet.Tests — Testsuite ohne externe Frameworks (nur .NET-Bordmittel).
//
// Geprüfte Bereiche:
//   1. Funktion      — statische Dateien, Endpoints, Routing, Fehlerfälle
//   2. Validierung   — automatische Ablehnung von null-/Leer-Werten
//   3. HTTPS         — selbstsigniertes Zertifikat aus BunCertificate
//   4. Shutdown      — sauberes Beenden, Port wird wieder frei
//   5. Performance   — Requests/Sekunde und Latenz (siehe README)
//
// Aufruf:  dotnet run -c Release --project tests/BunNet.Tests
//          dotnet run -c Release --project tests/BunNet.Tests -- --skip-perf
// ---------------------------------------------------------------------------

using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using BunNet;

namespace BunNet.Tests
{
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static readonly HttpClient Http = CreateHttpClient();

        private static async Task<int> Main(string[] args)
        {
            bool skipPerf = false;
            foreach (string arg in args)
            {
                if (arg == "--skip-perf") skipPerf = true;
            }

            Console.WriteLine("=== BunNet Testsuite ===");
            Console.WriteLine();

            await RunFunctionTests();
            await RunValidationTests();
            await RunHttpsTests();
            await RunShutdownTests();
            if (!skipPerf) await RunPerformanceTests();

            Console.WriteLine();
            Console.WriteLine("=== Ergebnis: " + _passed + " bestanden, " + _failed + " fehlgeschlagen ===");
            return _failed == 0 ? 0 : 1;
        }

        // -------------------------------------------------------------------
        // 1. Funktionstests
        // -------------------------------------------------------------------

        private static async Task RunFunctionTests()
        {
            Console.WriteLine("--- Funktionstests ---");

            // Statisches Verzeichnis mit Testdateien anlegen. Daneben liegt eine
            // "geheime" Datei, die über Path-Traversal NICHT erreichbar sein darf.
            string baseDir = CreateTempDirectory();
            string webDir = Path.Combine(baseDir, "Web");
            Directory.CreateDirectory(Path.Combine(webDir, "unterordner"));
            File.WriteAllText(Path.Combine(webDir, "index.html"), "<h1>BUNNET-STATIC-OK</h1>");
            File.WriteAllText(Path.Combine(webDir, "unterordner", "seite.html"), "UNTERSEITE-OK");
            File.WriteAllText(Path.Combine(baseDir, "geheim.txt"), "STRENG-GEHEIM");

            BunServerOptions options = new BunServerOptions();
            options.Port = 0; // freien Port wählen lassen
            options.StaticRoot = webDir;
            options.Log = null; // Tests sollen still sein
            options.MaxRequestBodySize = 256 * 1024; // klein, um das 413-Verhalten zu testen
            options.MaxUploadSize = 2 * 1024 * 1024; // Upload-Limit separat testen

            BunServer server = new BunServer(options);
            server.MapPost("/api/echo", HandleEcho);
            server.MapPost("/api/query", HandleQuery);
            server.MapPost("/api/header", HandleHeader);
            server.MapPost("/api/crash", HandleCrash);
            server.MapPost("/api/nichts", HandleNothing);
            server.MapGet("/api/zeit", HandleTime);
            server.MapPost("/api/feld", HandleField);   // synchroner Handler
            server.MapPost("/api/token", HandleToken);  // synchroner Handler
            server.MapPost("/api/bytes", HandleBytes);      // Binär-Body im Speicher
            server.MapUpload("/api/hochladen", HandleUpload); // Streaming auf die Platte
            await server.StartAsync();

            string baseUrl = server.Url;

            // Statische Dateien (liefert Bun direkt)
            HttpResponseMessage indexResponse = await Http.GetAsync(baseUrl + "/");
            Check("GET / liefert index.html", indexResponse.IsSuccessStatusCode &&
                (await indexResponse.Content.ReadAsStringAsync()).Contains("BUNNET-STATIC-OK"));

            HttpResponseMessage subResponse = await Http.GetAsync(baseUrl + "/unterordner/seite.html");
            Check("GET auf Unterordner funktioniert", subResponse.IsSuccessStatusCode &&
                (await subResponse.Content.ReadAsStringAsync()).Contains("UNTERSEITE-OK"));

            HttpResponseMessage missingResponse = await Http.GetAsync(baseUrl + "/gibt-es-nicht.html");
            Check("GET auf fehlende Datei liefert 404", (int)missingResponse.StatusCode == 404);

            // Path-Traversal (roh gesendet, damit kein Client den Pfad normalisiert)
            string traversal1 = await SendRawRequest(server.Port, "GET /../geheim.txt HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
            Check("Path-Traversal /../ wird geblockt", !traversal1.Contains("STRENG-GEHEIM"));

            string traversal2 = await SendRawRequest(server.Port, "GET /%2e%2e/geheim.txt HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
            Check("Path-Traversal %2e%2e wird geblockt", !traversal2.Contains("STRENG-GEHEIM"));

            // Endpoints (verarbeitet C#)
            HttpResponseMessage echoResponse = await PostText(baseUrl + "/api/echo", "Hallo BunNet!");
            Check("POST /api/echo gibt den Body zurück", echoResponse.IsSuccessStatusCode &&
                await echoResponse.Content.ReadAsStringAsync() == "Hallo BunNet!");

            HttpResponseMessage queryResponse = await PostText(baseUrl + "/api/query?name=Erik%20Test", "x");
            Check("Query-Parameter kommen dekodiert an", await queryResponse.Content.ReadAsStringAsync() == "Erik Test");

            HttpRequestMessage headerRequest = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/header");
            headerRequest.Content = new StringContent("x");
            headerRequest.Headers.Add("X-Test", "Header-Wert-42");
            HttpResponseMessage headerResponse = await Http.SendAsync(headerRequest);
            Check("Request-Header kommen an", await headerResponse.Content.ReadAsStringAsync() == "Header-Wert-42");

            HttpResponseMessage getResponse = await Http.GetAsync(baseUrl + "/api/zeit");
            Check("MapGet-Endpoint funktioniert", getResponse.IsSuccessStatusCode);

            HttpResponseMessage wrongMethod = await PostText(baseUrl + "/api/zeit", "x");
            Check("Falsche Methode auf Route liefert 404", (int)wrongMethod.StatusCode == 404);

            HttpResponseMessage unknownRoute = await PostText(baseUrl + "/api/unbekannt", "x");
            Check("Unbekannte Route liefert 404", (int)unknownRoute.StatusCode == 404);

            HttpResponseMessage crashResponse = await PostText(baseUrl + "/api/crash", "x");
            string crashBody = await crashResponse.Content.ReadAsStringAsync();
            Check("Handler-Exception liefert 500 ohne interne Details",
                (int)crashResponse.StatusCode == 500 && !crashBody.Contains("Absichtlicher"));

            HttpResponseMessage nothingResponse = await PostText(baseUrl + "/api/nichts", "x");
            Check("Handler ohne Antwort liefert 204", (int)nothingResponse.StatusCode == 204);

            // Neue Komfort-Zugriffe: request["feld"], BearerToken, synchrone Handler
            HttpResponseMessage fieldResponse = await PostJson(baseUrl + "/api/feld",
                "{\"anderes\":\"x\",\"name\":\"Erik \\u0026 \\\"Test\\\"\"}");
            Check("request[\"…\"] liest JSON-Feld (synchroner Handler)",
                await fieldResponse.Content.ReadAsStringAsync() == "Erik & \"Test\"");

            HttpResponseMessage nestedFieldResponse = await PostJson(baseUrl + "/api/feld",
                "{\"innen\":{\"name\":\"falsch\"},\"name\":\"richtig\"}");
            Check("request[\"…\"] ignoriert verschachtelte Felder",
                await nestedFieldResponse.Content.ReadAsStringAsync() == "richtig");

            HttpRequestMessage tokenRequest = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/token");
            tokenRequest.Content = new StringContent("x");
            tokenRequest.Headers.Add("Authorization", "Bearer abc123");
            HttpResponseMessage tokenResponse = await Http.SendAsync(tokenRequest);
            Check("request.BearerToken liest den Authorization-Header",
                await tokenResponse.Content.ReadAsStringAsync() == "abc123");

            // Datei-Upload: Binärdaten müssen unverändert ankommen (SHA-256-Vergleich) …
            byte[] fileData = new byte[100 * 1024];
            new Random(42).NextBytes(fileData);
            string expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fileData));
            HttpResponseMessage bytesResponse = await Http.PostAsync(
                baseUrl + "/api/bytes", new ByteArrayContent(fileData));
            Check("Binärer Body (100 KiB) kommt unverändert an",
                await bytesResponse.Content.ReadAsStringAsync() == expectedHash);

            // … und Requests über MaxRequestBodySize lehnt Bun selbst ab: mit 413 —
            // oder er kappt die Verbindung schon während des Sendens.
            bool tooLargeRejected;
            try
            {
                HttpRequestMessage tooLargeRequest = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/bytes");
                tooLargeRequest.Content = new ByteArrayContent(new byte[300 * 1024]);
                using (HttpResponseMessage tooLargeResponse = await Http.SendAsync(
                    tooLargeRequest, HttpCompletionOption.ResponseHeadersRead))
                {
                    tooLargeRejected = (int)tooLargeResponse.StatusCode == 413;
                }
            }
            catch (HttpRequestException)
            {
                tooLargeRejected = true; // Verbindung gekappt = ebenfalls abgelehnt
            }
            Check("Body über MaxRequestBodySize wird abgelehnt (413)", tooLargeRejected);

            // Upload-Route (MapUpload): 1 MiB liegt ÜBER MaxRequestBodySize (256 KiB),
            // wird aber auf die Platte gestreamt und muss unverändert ankommen.
            byte[] uploadData = new byte[1024 * 1024];
            new Random(7).NextBytes(uploadData);
            string uploadHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(uploadData));
            HttpResponseMessage uploadResponse = await Http.PostAsync(
                baseUrl + "/api/hochladen", new ByteArrayContent(uploadData));
            Check("Upload-Route streamt 1 MiB an MaxRequestBodySize vorbei",
                await uploadResponse.Content.ReadAsStringAsync() == uploadHash + ":" + uploadData.Length);

            // Der interne Übergabe-Header darf sich nicht von außen unterschieben lassen.
            HttpRequestMessage smuggleRequest = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/hochladen");
            smuggleRequest.Content = new ByteArrayContent(uploadData);
            smuggleRequest.Headers.TryAddWithoutValidation("x-bunnet-body-file", "/etc/passwd");
            HttpResponseMessage smuggleResponse = await Http.SendAsync(smuggleRequest);
            Check("x-bunnet-Header von außen wird verworfen",
                await smuggleResponse.Content.ReadAsStringAsync() == uploadHash + ":" + uploadData.Length);

            // 3 MiB > MaxUploadSize (2 MiB) → abgelehnt.
            bool uploadRejected;
            try
            {
                HttpRequestMessage bigUploadRequest = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/hochladen");
                bigUploadRequest.Content = new ByteArrayContent(new byte[3 * 1024 * 1024]);
                using (HttpResponseMessage bigUploadResponse = await Http.SendAsync(
                    bigUploadRequest, HttpCompletionOption.ResponseHeadersRead))
                {
                    uploadRejected = (int)bigUploadResponse.StatusCode == 413;
                }
            }
            catch (HttpRequestException)
            {
                uploadRejected = true; // Verbindung gekappt = ebenfalls abgelehnt
            }
            Check("Upload über MaxUploadSize wird abgelehnt (413)", uploadRejected);

            await server.DisposeAsync();
            Directory.Delete(baseDir, true);
            Console.WriteLine();
        }

        private static Task<BunResponse> HandleEcho(BunRequest request)
        {
            return Task.FromResult(BunResponse.Text(request.BodyAsText()));
        }

        private static Task<BunResponse> HandleQuery(BunRequest request)
        {
            return Task.FromResult(BunResponse.Text(request.GetQuery("name") ?? ""));
        }

        private static Task<BunResponse> HandleHeader(BunRequest request)
        {
            return Task.FromResult(BunResponse.Text(request.GetHeader("X-Test") ?? ""));
        }

        private static Task<BunResponse> HandleCrash(BunRequest request)
        {
            throw new InvalidOperationException("Absichtlicher Testfehler");
        }

        private static Task<BunResponse> HandleNothing(BunRequest request)
        {
            return Task.FromResult<BunResponse>(null!);
        }

        private static Task<BunResponse> HandleTime(BunRequest request)
        {
            return Task.FromResult(BunResponse.Text(DateTimeOffset.Now.ToString("O")));
        }

        private static BunResponse HandleField(BunRequest request)
        {
            return BunResponse.Text(request["name"]);
        }

        private static BunResponse HandleToken(BunRequest request)
        {
            return BunResponse.Text(request.BearerToken);
        }

        private static BunResponse HandleBytes(BunRequest request)
        {
            byte[] hash = System.Security.Cryptography.SHA256.HashData(request.Body);
            return BunResponse.Text(Convert.ToHexString(hash));
        }

        // Upload-Handler: übernimmt die gestreamte Datei per SaveBodyTo und
        // antwortet mit "SHA256:Größe" der gespeicherten Datei.
        private static BunResponse HandleUpload(BunRequest request)
        {
            string target = Path.Combine(Path.GetTempPath(), "bunnet-test-upload-" + Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                request.SaveBodyTo(target);
                byte[] data = File.ReadAllBytes(target);
                byte[] hash = System.Security.Cryptography.SHA256.HashData(data);
                return BunResponse.Text(Convert.ToHexString(hash) + ":" + data.Length);
            }
            finally
            {
                if (File.Exists(target)) File.Delete(target);
            }
        }

        // -------------------------------------------------------------------
        // 2. Validierungstests — keine null-/Leer-Werte in JSON-Bodys
        // -------------------------------------------------------------------

        private static async Task RunValidationTests()
        {
            Console.WriteLine("--- Validierungstests ---");

            BunServerOptions options = new BunServerOptions();
            options.Port = 0;
            options.Log = null;

            BunServer server = new BunServer(options);
            server.MapPost("/api/daten", HandleEcho);
            await server.StartAsync();
            string url = server.Url + "/api/daten";

            HttpResponseMessage valid = await PostJson(url, "{\"name\":\"Erik\",\"alter\":30}");
            Check("Gültiges JSON wird verarbeitet", valid.IsSuccessStatusCode);

            HttpResponseMessage withNull = await PostJson(url, "{\"name\":null}");
            string withNullBody = await withNull.Content.ReadAsStringAsync();
            Check("null-Wert wird automatisch abgelehnt (400)",
                (int)withNull.StatusCode == 400 && withNullBody.Contains("name"));

            HttpResponseMessage withEmpty = await PostJson(url, "{\"name\":\"\"}");
            Check("Leerer Wert wird automatisch abgelehnt (400)", (int)withEmpty.StatusCode == 400);

            HttpResponseMessage withWhitespace = await PostJson(url, "{\"name\":\"   \"}");
            Check("Nur-Leerraum-Wert wird automatisch abgelehnt (400)", (int)withWhitespace.StatusCode == 400);

            HttpResponseMessage nestedNull = await PostJson(url, "{\"benutzer\":{\"stadt\":null}}");
            Check("Verschachtelter null-Wert wird abgelehnt (400)", (int)nestedNull.StatusCode == 400);

            HttpResponseMessage nullInArray = await PostJson(url, "{\"liste\":[1,null,3]}");
            Check("null in Arrays wird abgelehnt (400)", (int)nullInArray.StatusCode == 400);

            HttpResponseMessage nullAsText = await PostJson(url, "{\"kommentar\":\"null ist hier nur ein Wort\"}");
            Check("'null' als Text-Inhalt ist erlaubt", nullAsText.IsSuccessStatusCode);

            HttpResponseMessage plainText = await PostText(url, "");
            Check("Leerer Body ohne JSON bleibt erlaubt (für z. B. Logout)", plainText.IsSuccessStatusCode ||
                (int)plainText.StatusCode == 204);

            await server.DisposeAsync();
            Console.WriteLine();
        }

        // -------------------------------------------------------------------
        // 3. HTTPS-Test — selbstsigniertes Zertifikat aus der Bibliothek
        // -------------------------------------------------------------------

        private static async Task RunHttpsTests()
        {
            Console.WriteLine("--- HTTPS-Test ---");

            string certDir = CreateTempDirectory();
            string certPath;
            string keyPath;
            BunCertificate.EnsureSelfSigned(certDir, "localhost", out certPath, out keyPath);
            Check("Selbstsigniertes Zertifikat wird erzeugt", File.Exists(certPath) && File.Exists(keyPath));

            BunServerOptions options = new BunServerOptions();
            options.Port = 0;
            options.Log = null;
            options.CertificatePemPath = certPath;
            options.PrivateKeyPemPath = keyPath;

            BunServer server = new BunServer(options);
            server.MapPost("/api/ping", HandleEcho);
            await server.StartAsync();

            // Eigener Client, der das selbstsignierte Zertifikat akzeptiert.
            HttpClientHandler handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = delegate { return true; };
            using (HttpClient httpsClient = new HttpClient(handler))
            {
                HttpResponseMessage response = await httpsClient.PostAsync(
                    server.Url + "/api/ping", new StringContent("sicher"));
                Check("HTTPS-Request wird beantwortet",
                    response.IsSuccessStatusCode && await response.Content.ReadAsStringAsync() == "sicher");
            }

            await server.DisposeAsync();
            Directory.Delete(certDir, true);
            Console.WriteLine();
        }

        // -------------------------------------------------------------------
        // 4. Shutdown-Test — Ressourcen werden wirklich freigegeben
        // -------------------------------------------------------------------

        private static async Task RunShutdownTests()
        {
            Console.WriteLine("--- Shutdown-Test ---");

            BunServerOptions options = new BunServerOptions();
            options.Port = 0;
            options.Log = null;

            BunServer server = new BunServer(options);
            server.MapPost("/api/ping", HandleEcho);
            await server.StartAsync();
            int port = server.Port;

            HttpResponseMessage before = await PostText("http://127.0.0.1:" + port + "/api/ping", "x");
            Check("Server antwortet vor dem Stop", before.IsSuccessStatusCode);

            await server.StopAsync();

            bool refused = false;
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    await client.ConnectAsync("127.0.0.1", port);
                }
            }
            catch (SocketException)
            {
                refused = true;
            }
            Check("Port ist nach dem Stop wieder frei", refused);
            Console.WriteLine();
        }

        // -------------------------------------------------------------------
        // 5. Performancetests
        // -------------------------------------------------------------------

        private static async Task RunPerformanceTests()
        {
            Console.WriteLine("--- Performancetests ---");
            Console.WriteLine("(Release-Build verwenden: dotnet run -c Release)");
            Console.WriteLine();

            string webDir = CreateTempDirectory();
            File.WriteAllText(Path.Combine(webDir, "index.html"), "<h1>ok</h1>");

            BunServerOptions options = new BunServerOptions();
            options.Port = 0;
            options.StaticRoot = webDir;
            options.Log = null;

            BunServer server = new BunServer(options);
            server.MapPost("/api/ping", HandlePing);
            await server.StartAsync();
            int port = server.Port;

            byte[] staticRequest = BuildRawRequest("GET", "/index.html", port, "");
            byte[] apiRequest = BuildRawRequest("POST", "/api/ping", port, "ping");

            TimeSpan duration = TimeSpan.FromSeconds(5);

            // Latenz: eine Verbindung, ein Request nach dem anderen.
            double latencyMicros = await MeasureLatency(port, apiRequest, 2000);
            Console.WriteLine("  Latenz .NET-Endpoint (seriell):    " + latencyMicros.ToString("0") + " µs/Request");

            // Durchsatz statisch (Bun liefert direkt, ohne IPC).
            double staticRate = await MeasureThroughput(port, staticRequest, 64, duration);
            Console.WriteLine("  Statische Datei (Bun direkt):      " + FormatRate(staticRate));

            // Durchsatz .NET-Endpoint über IPC mit unterschiedlicher Parallelität.
            double apiRate64 = await MeasureThroughput(port, apiRequest, 64, duration);
            Console.WriteLine("  .NET-Endpoint (64 Verbindungen):   " + FormatRate(apiRate64));

            double apiRate256 = await MeasureThroughput(port, apiRequest, 256, duration);
            Console.WriteLine("  .NET-Endpoint (256 Verbindungen):  " + FormatRate(apiRate256));

            Check("Durchsatz .NET-Endpoint über 10.000 Requests/s", apiRate64 > 10_000,
                FormatRate(apiRate64));

            await server.DisposeAsync();
            Directory.Delete(webDir, true);
            Console.WriteLine();
        }

        private static readonly BunResponse PingResponse = BunResponse.JsonText("{\"pong\":true}");

        private static Task<BunResponse> HandlePing(BunRequest request)
        {
            return Task.FromResult(PingResponse);
        }

        private static byte[] BuildRawRequest(string method, string path, int port, string body)
        {
            string request =
                method + " " + path + " HTTP/1.1\r\n" +
                "Host: 127.0.0.1:" + port + "\r\n" +
                "Content-Type: text/plain\r\n" +
                "Content-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\n" +
                "\r\n" + body;
            return Encoding.UTF8.GetBytes(request);
        }

        // Misst die mittlere Antwortzeit über eine einzelne Verbindung.
        private static async Task<double> MeasureLatency(int port, byte[] request, int iterations)
        {
            using (TcpClient client = new TcpClient())
            {
                await client.ConnectAsync("127.0.0.1", port);
                client.NoDelay = true;
                NetworkStream stream = client.GetStream();
                int responseSize = await ReadOneResponse(stream, request);

                Stopwatch watch = Stopwatch.StartNew();
                byte[] buffer = new byte[responseSize];
                for (int i = 0; i < iterations; i++)
                {
                    await stream.WriteAsync(request);
                    await ReadExactly(stream, buffer, responseSize);
                }
                watch.Stop();
                return watch.Elapsed.TotalMilliseconds * 1000.0 / iterations;
            }
        }

        // Misst Requests/Sekunde mit vielen parallelen Keep-Alive-Verbindungen.
        private static async Task<double> MeasureThroughput(int port, byte[] request, int connections, TimeSpan duration)
        {
            // Antwortgröße einmalig ermitteln — alle Antworten sind identisch groß.
            int responseSize;
            using (TcpClient probe = new TcpClient())
            {
                await probe.ConnectAsync("127.0.0.1", port);
                responseSize = await ReadOneResponse(probe.GetStream(), request);
            }

            using (CancellationTokenSource stop = new CancellationTokenSource(duration))
            {
                Task<long>[] workers = new Task<long>[connections];
                for (int i = 0; i < connections; i++)
                {
                    workers[i] = Task.Run(() => LoadWorker(port, request, responseSize, stop.Token));
                }

                long total = 0;
                for (int i = 0; i < connections; i++)
                {
                    total += await workers[i];
                }
                return total / duration.TotalSeconds;
            }
        }

        private static async Task<long> LoadWorker(int port, byte[] request, int responseSize, CancellationToken stop)
        {
            long count = 0;
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    await client.ConnectAsync("127.0.0.1", port);
                    client.NoDelay = true;
                    NetworkStream stream = client.GetStream();
                    byte[] buffer = new byte[responseSize];

                    while (!stop.IsCancellationRequested)
                    {
                        await stream.WriteAsync(request);
                        await ReadExactly(stream, buffer, responseSize);
                        count++;
                    }
                }
            }
            catch (IOException)
            {
                // Server hat die Verbindung geschlossen — bis hierhin gezählte Requests behalten.
            }
            return count;
        }

        // Sendet einen Request und liest genau eine Antwort; liefert deren Gesamtgröße.
        private static async Task<int> ReadOneResponse(NetworkStream stream, byte[] request)
        {
            await stream.WriteAsync(request);

            byte[] buffer = new byte[65536];
            int filled = 0;
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(filled));
                if (read == 0) throw new IOException("Verbindung wurde geschlossen.");
                filled += read;

                string text = Encoding.ASCII.GetString(buffer, 0, filled);
                int headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd < 0) continue;

                if (!text.StartsWith("HTTP/1.1 200", StringComparison.Ordinal))
                    throw new IOException("Unerwartete Antwort: " + text.Substring(0, Math.Min(60, text.Length)));

                int contentLength = ParseContentLength(text.Substring(0, headerEnd));
                int totalSize = headerEnd + 4 + contentLength;

                // Rest des Bodys einlesen, falls noch nicht komplett da.
                while (filled < totalSize)
                {
                    read = await stream.ReadAsync(buffer.AsMemory(filled));
                    if (read == 0) throw new IOException("Verbindung wurde geschlossen.");
                    filled += read;
                }
                return totalSize;
            }
        }

        private static int ParseContentLength(string headers)
        {
            foreach (string line in headers.Split("\r\n"))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    return int.Parse(line.Substring("Content-Length:".Length).Trim());
            }
            throw new IOException("Antwort ohne Content-Length.");
        }

        private static async Task ReadExactly(NetworkStream stream, byte[] buffer, int count)
        {
            int done = 0;
            while (done < count)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(done, count - done));
                if (read == 0) throw new IOException("Verbindung wurde geschlossen.");
                done += read;
            }
        }

        private static string FormatRate(double requestsPerSecond)
        {
            return requestsPerSecond.ToString("#,0") + " Requests/s";
        }

        // -------------------------------------------------------------------
        // Test-Infrastruktur
        // -------------------------------------------------------------------

        private static void Check(string name, bool ok, string details = "")
        {
            if (ok)
            {
                _passed++;
                Console.WriteLine("  [OK]     " + name);
            }
            else
            {
                _failed++;
                Console.WriteLine("  [FEHLER] " + name + (details == "" ? "" : " — " + details));
            }
        }

        private static HttpClient CreateHttpClient()
        {
            HttpClient client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            return client;
        }

        private static Task<HttpResponseMessage> PostText(string url, string body)
        {
            return Http.PostAsync(url, new StringContent(body, Encoding.UTF8, "text/plain"));
        }

        private static Task<HttpResponseMessage> PostJson(string url, string json)
        {
            return Http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
        }

        private static async Task<string> SendRawRequest(int port, string rawRequest)
        {
            using (TcpClient client = new TcpClient())
            {
                await client.ConnectAsync("127.0.0.1", port);
                NetworkStream stream = client.GetStream();
                byte[] requestBytes = Encoding.ASCII.GetBytes(rawRequest);
                await stream.WriteAsync(requestBytes);

                MemoryStream response = new MemoryStream();
                byte[] buffer = new byte[8192];
                while (true)
                {
                    int read = await stream.ReadAsync(buffer);
                    if (read == 0) break;
                    response.Write(buffer, 0, read);
                }
                return Encoding.UTF8.GetString(response.ToArray());
            }
        }

        private static string CreateTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "bunnet-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}

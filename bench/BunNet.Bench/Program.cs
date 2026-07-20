// BunNet Performance-Benchmark (Linux)
// Misst Requests/Sekunde eines .NET-Endpoints über IPC bei verschiedenen
// Kombinationen aus BunWorkers (parallele Bun-Prozesse, SO_REUSEPORT) und
// IpcConnections (parallele IPC-Kanäle pro Worker).
//
// Der Lastgenerator entspricht dem der Testsuite: viele parallele
// Keep-Alive-TCP-Verbindungen, die pipeline-frei Request→Response fahren.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BunNet;

internal static class Bench
{
    private static readonly BunResponse PingResponse = BunResponse.JsonText("{\"pong\":true}");

    private static Task<BunResponse> HandlePing(BunRequest request)
    {
        return Task.FromResult(PingResponse);
    }

    private static async Task<int> Main(string[] args)
    {
        int cores = Environment.ProcessorCount;
        Console.WriteLine("=== BunNet Performance-Benchmark ===");
        Console.WriteLine("CPU-Kerne: " + cores + "   Plattform: " + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        Console.WriteLine();

        // Aufwärmphase und Messphase.
        TimeSpan warmup = TimeSpan.FromSeconds(2);
        TimeSpan measure = TimeSpan.FromSeconds(6);

        // Last: Anzahl paralleler Client-Verbindungen des Lastgenerators.
        int loadConnections = 256;

        // Matrix: (BunWorkers, IpcConnections). 0 = automatisch.
        int[][] matrix = new int[][]
        {
            new int[] { 1, 1 },
            new int[] { 1, 2 },
            new int[] { 1, 4 },
            new int[] { 2, 2 },
            new int[] { 3, 2 },
            new int[] { 4, 2 },
            new int[] { 6, 2 },
            new int[] { 8, 2 },
            new int[] { 0, 0 }, // Auto (Standard-Empfehlung der Bibliothek)
        };

        List<string> summary = new List<string>();
        summary.Add(string.Format("{0,-10} {1,-8} {2,-8} {3,-8} {4,-8} {5}",
            "Workers", "IpcConn", "Load", "req/s", "µs/req", "Netto"));

        foreach (int[] combo in matrix)
        {
            int workers = combo[0];
            int ipc = combo[1];

            string webDir = Path.Combine(Path.GetTempPath(), "bunnet-bench-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(webDir);
            File.WriteAllText(Path.Combine(webDir, "index.html"), "<h1>ok</h1>");

            BunServerOptions options = new BunServerOptions();
            options.Port = 0;
            options.StaticRoot = webDir;
            options.Log = null;
            options.BunWorkers = workers;
            options.IpcConnections = ipc;

            BunServer server = new BunServer(options);
            server.MapPost("/api/ping", HandlePing);
            await server.StartAsync();
            int port = server.Port;

            byte[] apiRequest = BuildRawRequest("POST", "/api/ping", port, "ping");

            // Latenz seriell (eine Verbindung).
            double latency = await MeasureLatency(port, apiRequest, 3000);

            // Aufwärmen (Ergebnis verwerfen), dann messen.
            await MeasureThroughput(port, apiRequest, loadConnections, warmup);
            double rate = await MeasureThroughput(port, apiRequest, loadConnections, measure);

            string wLabel = workers == 0 ? "auto" : workers.ToString();
            string iLabel = ipc == 0 ? "auto" : ipc.ToString();

            Console.WriteLine(string.Format(
                "  Workers={0,-4} IpcConn={1,-4} Load={2,-4}  ->  {3,12}  ({4} µs seriell)",
                wLabel, iLabel, loadConnections, FormatRate(rate), latency.ToString("0")));

            summary.Add(string.Format("{0,-10} {1,-8} {2,-8} {3,-8} {4,-8} {5}",
                wLabel, iLabel, loadConnections, ((long)rate).ToString(), latency.ToString("0"),
                (workers == 0 && ipc == 0) ? "<- Auto" : ""));

            await server.DisposeAsync();
            try { Directory.Delete(webDir, true); } catch { }

            // Kurze Pause, damit Ports/Prozesse sicher frei sind.
            await Task.Delay(500);
        }

        Console.WriteLine();
        Console.WriteLine("=== Zusammenfassung ===");
        foreach (string line in summary) Console.WriteLine(line);
        return 0;
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

    private static async Task<double> MeasureThroughput(int port, byte[] request, int connections, TimeSpan duration)
    {
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
            for (int i = 0; i < connections; i++) total += await workers[i];
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
        catch (IOException) { }
        catch (SocketException) { }
        return count;
    }

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
        return requestsPerSecond.ToString("#,0") + " req/s";
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BunNet
{
    /// <summary>
    /// Ein von .NET gesteuerter Webserver auf Bun-Basis.
    ///
    /// Der .NET-Prozess ist der Master: Er startet Bun, wartet auf dessen
    /// IPC-Verbindungen (Unix Domain Socket bzw. Named Pipe), empfängt darüber
    /// die von Bun weitergeleiteten HTTP-Requests und beantwortet sie mit den
    /// in C# registrierten Endpoint-Handlern. Für hohen Durchsatz laufen mehrere
    /// IPC-Verbindungen parallel (<see cref="BunServerOptions.IpcConnections"/>):
    /// Bun verteilt die Requests darauf, .NET liest, verarbeitet und beantwortet
    /// jede Verbindung mit eigenen Read-/Write-Tasks.
    ///
    /// Typische Verwendung:
    /// <code>
    /// BunServer server = new BunServer(new BunServerOptions());
    /// server.MapPost("/api/login", HandleLogin);
    /// await server.StartAsync();
    /// </code>
    /// </summary>
    public sealed class BunServer : IAsyncDisposable
    {
        private const int StateNew = 0;
        private const int StateStarted = 1;
        private const int StateStopped = 2;

        /// <summary>
        /// Eine IPC-Verbindung zu Bun mit eigenem Reader und eigener Sendewarteschlange:
        /// Antworten paralleler Handler werden gesammelt und von EINEM Writer-Task
        /// pro Verbindung in möglichst wenigen Schreibvorgängen rausgeschickt.
        /// </summary>
        private sealed class IpcChannel
        {
            public Stream Stream;
            public FrameReader Reader;
            public readonly object SendLock = new object();
            public readonly SemaphoreSlim SendSignal = new SemaphoreSlim(0, 1);
            public List<byte[]> SendQueue = new List<byte[]>();
            public Task? ReadLoop;
            public Task? WriteLoop;

            public IpcChannel(Stream stream)
            {
                Stream = stream;
                Reader = new FrameReader(stream);
            }
        }

        private readonly BunServerOptions _options;
        private readonly Action<BunLogLevel, string> _log;
        private readonly Dictionary<string, BunEndpointHandler> _routes = new Dictionary<string, BunEndpointHandler>(StringComparer.Ordinal);
        private readonly HashSet<string> _uploadRoutes = new HashSet<string>(StringComparer.Ordinal);
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly object _stateLock = new object();

        private int _state = StateNew;
        private IpcListener[]? _listeners;
        private BunProcessHost[]? _workers;
        private IpcChannel[]? _channels;   // flach, Worker-weise: [w * VerbProWorker + i]
        private int _connectionsPerWorker;

        /// <summary>Erzeugt einen Server mit den angegebenen Optionen.</summary>
        public BunServer(BunServerOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            _options = options;
            _log = options.Log ?? delegate { };
        }

        /// <summary>Tatsächlich gebundener Port (nach <see cref="StartAsync"/> verfügbar).</summary>
        public int Port { get; private set; }

        /// <summary>Basis-URL des laufenden Servers, z. B. <c>http://127.0.0.1:8080</c>.</summary>
        public string Url
        {
            get { return (_options.UseHttps ? "https" : "http") + "://" + _options.Hostname + ":" + Port; }
        }

        /// <summary><c>true</c>, solange der Server läuft und mindestens ein Bun-Worker lebt.</summary>
        public bool IsRunning
        {
            get
            {
                if (_state != StateStarted || _workers == null) return false;
                foreach (BunProcessHost worker in _workers)
                {
                    if (!worker.HasExited) return true;
                }
                return false;
            }
        }

        // -------------------------------------------------------------------
        // Endpoint-Registrierung
        // -------------------------------------------------------------------

        /// <summary>Registriert einen Endpoint für eine beliebige HTTP-Methode (exakter Pfadvergleich).</summary>
        public BunServer Map(string method, string path, BunEndpointHandler handler)
        {
            if (string.IsNullOrEmpty(method)) throw new ArgumentException("Methode fehlt.", nameof(method));
            if (string.IsNullOrEmpty(path) || path[0] != '/')
                throw new ArgumentException("Pfad muss mit '/' beginnen.", nameof(path));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (_state != StateNew)
                throw new InvalidOperationException("Endpoints müssen vor StartAsync() registriert werden.");

            _routes[method.ToUpperInvariant() + " " + path] = handler;
            return this;
        }

        /// <summary>Registriert einen POST-Endpoint (die Standardmethode dieser Bibliothek).</summary>
        public BunServer MapPost(string path, BunEndpointHandler handler)
        {
            return Map("POST", path, handler);
        }

        /// <summary>Registriert einen GET-Endpoint.</summary>
        public BunServer MapGet(string path, BunEndpointHandler handler)
        {
            return Map("GET", path, handler);
        }

        /// <summary>Registriert einen synchronen Endpoint für eine beliebige HTTP-Methode.</summary>
        public BunServer Map(string method, string path, Func<BunRequest, BunResponse> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return Map(method, path, delegate (BunRequest request)
            {
                return Task.FromResult(handler(request));
            });
        }

        /// <summary>Registriert einen synchronen POST-Endpoint.</summary>
        public BunServer MapPost(string path, Func<BunRequest, BunResponse> handler)
        {
            return Map("POST", path, handler);
        }

        /// <summary>Registriert einen synchronen GET-Endpoint.</summary>
        public BunServer MapGet(string path, Func<BunRequest, BunResponse> handler)
        {
            return Map("GET", path, handler);
        }

        /// <summary>
        /// Registriert einen POST-Endpoint für große Datei-Uploads. Der Body wird
        /// von Bun direkt auf die Platte gestreamt statt in den Speicher — auch
        /// Dateien mit vielen GiB belasten das RAM nicht. Der Handler findet die
        /// Datei in <see cref="BunRequest.BodyFilePath"/> und übernimmt sie am
        /// einfachsten mit <see cref="BunRequest.SaveBodyTo"/> (verschiebt, kopiert nicht).
        /// Größenlimit: <see cref="BunServerOptions.MaxUploadSize"/> (0 = unbegrenzt);
        /// <see cref="BunServerOptions.MaxRequestBodySize"/> gilt hier nicht.
        /// </summary>
        public BunServer MapUpload(string path, BunEndpointHandler handler)
        {
            Map("POST", path, handler);
            _uploadRoutes.Add("POST " + path);
            return this;
        }

        /// <summary>Registriert einen synchronen Upload-Endpoint (siehe <see cref="MapUpload(string, BunEndpointHandler)"/>).</summary>
        public BunServer MapUpload(string path, Func<BunRequest, BunResponse> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return MapUpload(path, delegate (BunRequest request)
            {
                return Task.FromResult(handler(request));
            });
        }

        // -------------------------------------------------------------------
        // Lebenszyklus
        // -------------------------------------------------------------------

        /// <summary>
        /// Startet die Bun-Worker (je eigener IPC-Endpunkt) und wartet, bis jeder
        /// Worker auf allen IPC-Verbindungen die Bereit-Meldung sendet. Danach ist
        /// der Server unter <see cref="Url"/> erreichbar. Bei mehreren Workern
        /// teilen sich die Bun-Prozesse den Port per SO_REUSEPORT; der erste Worker
        /// bestimmt den tatsächlichen Port (wichtig bei Port 0).
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            lock (_stateLock)
            {
                if (_state != StateNew)
                    throw new InvalidOperationException("Der Server wurde bereits gestartet.");
                _state = StateStarted;
            }

            if (_options.UseHttps)
            {
                if (!File.Exists(_options.CertificatePemPath))
                    throw new FileNotFoundException("Zertifikat nicht gefunden.", _options.CertificatePemPath);
                if (!File.Exists(_options.PrivateKeyPemPath))
                    throw new FileNotFoundException("Privater Schlüssel nicht gefunden.", _options.PrivateKeyPemPath);
            }
            if (_options.StaticRoot != null && !Directory.Exists(_options.StaticRoot))
                throw new DirectoryNotFoundException("StaticRoot nicht gefunden: " + _options.StaticRoot);

            int workerCount = ResolveWorkerCount();
            int connectionsPerWorker = ResolveIpcConnectionCount();
            bool reusePort = workerCount > 1;
            _connectionsPerWorker = connectionsPerWorker;

            if (reusePort && !System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Linux))
            {
                _log(BunLogLevel.Warning, "BunWorkers > 1: SO_REUSEPORT verteilt die Last nur " +
                    "unter Linux auf die Worker — unter macOS/Windows erhält meist nur eine Instanz die Verbindungen.");
            }

            try
            {
                using (CancellationTokenSource startupTimeout =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token))
                {
                    startupTimeout.CancelAfter(_options.StartupTimeout);

                    _listeners = new IpcListener[workerCount];
                    _workers = new BunProcessHost[workerCount];
                    IpcChannel[] channels = new IpcChannel[workerCount * connectionsPerWorker];

                    // Worker nacheinander starten: Der erste bindet den Port (und
                    // bestimmt ihn bei Port 0), alle weiteren binden denselben Port
                    // mit SO_REUSEPORT. Jeder Worker hat einen EIGENEN IPC-Endpunkt —
                    // so ist eindeutig, welche Verbindung zu welchem Worker gehört.
                    int actualPort = _options.Port;
                    for (int w = 0; w < workerCount; w++)
                    {
                        IpcListener listener = new IpcListener(connectionsPerWorker);
                        _listeners[w] = listener;
                        _log(BunLogLevel.Debug, "IPC-Endpunkt Worker " + w + ": " + listener.BunConnectPath +
                            " (" + connectionsPerWorker + " Verbindung(en))");

                        BunProcessHost worker = new BunProcessHost(_options, _log);
                        _workers[w] = worker;
                        worker.Start(listener.BunConnectPath, connectionsPerWorker, actualPort, reusePort,
                            _routes.Keys, _uploadRoutes);

                        // Alle Verbindungen dieses Workers annehmen — oder abbrechen,
                        // falls er vorher stirbt. Jede Verbindung meldet sich mit
                        // einem READY-Frame (inkl. tatsächlich gebundenem Port).
                        for (int i = 0; i < connectionsPerWorker; i++)
                        {
                            Task<Stream> acceptTask = listener.AcceptAsync(i, startupTimeout.Token);
                            Task completed = await Task.WhenAny(acceptTask, worker.Exited).ConfigureAwait(false);
                            if (completed == worker.Exited)
                                throw new InvalidOperationException(
                                    "Bun-Worker " + w + " wurde unerwartet beendet (Exit-Code " + worker.Exited.Result +
                                    "), bevor die IPC-Verbindung zustande kam. Siehe Log.");

                            IpcChannel channel = new IpcChannel(await acceptTask.ConfigureAwait(false));
                            byte[]? readyPayload = await channel.Reader.ReadFrameAsync(startupTimeout.Token).ConfigureAwait(false);
                            if (readyPayload == null || readyPayload.Length == 0 || readyPayload[0] != Protocol.TypeReady)
                                throw new InvalidDataException("Unerwartete erste IPC-Nachricht von Bun (READY erwartet).");

                            if (w == 0 && i == 0)
                            {
                                Port = Protocol.ParseReady(readyPayload);
                                actualPort = Port;
                            }
                            channels[w * connectionsPerWorker + i] = channel;
                        }
                    }

                    _channels = channels;
                    for (int i = 0; i < channels.Length; i++)
                    {
                        IpcChannel channel = channels[i];
                        channel.ReadLoop = Task.Run(() => ReadLoopAsync(channel, _lifetime.Token));
                        channel.WriteLoop = Task.Run(() => WriteLoopAsync(channel, _lifetime.Token));
                    }

                    _log(BunLogLevel.Info, "Server läuft auf " + Url + " (" + _routes.Count +
                        " Endpoint(s), " + workerCount + " Worker, " +
                        connectionsPerWorker + " IPC-Verbindung(en) pro Worker).");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await CleanupAsync().ConfigureAwait(false);
                throw new TimeoutException(
                    "Bun hat sich nicht innerhalb von " + _options.StartupTimeout.TotalSeconds + " s gemeldet.");
            }
            catch
            {
                await CleanupAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// Anzahl der IPC-Verbindungen pro Worker bestimmen: explizit gesetzter Wert
        /// oder automatisch anhand der CPU-Kerne.
        /// </summary>
        private int ResolveIpcConnectionCount()
        {
            int count = _options.IpcConnections;
            if (count <= 0) count = Environment.ProcessorCount / 2;
            if (count < 1) count = 1;
            if (count > 16) count = 16;
            return count;
        }

        /// <summary>
        /// Anzahl der Bun-Worker bestimmen: explizit gesetzter Wert oder automatisch —
        /// unter Linux anhand der CPU-Kerne (dort verteilt SO_REUSEPORT die Last),
        /// auf anderen Systemen 1.
        /// </summary>
        private int ResolveWorkerCount()
        {
            int count = _options.BunWorkers;
            if (count <= 0)
            {
                bool isLinux = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Linux);
                count = isLinux ? Environment.ProcessorCount / 2 : 1;
            }
            if (count < 1) count = 1;
            if (count > 32) count = 32;
            return count;
        }

        /// <summary>
        /// Fährt den Server sauber herunter: Bun erhält ein SHUTDOWN-Signal,
        /// bekommt eine Frist zum Beenden und wird andernfalls zwangsweise gestoppt.
        /// Anschließend sind alle Ressourcen (Prozess, IPC, Temp-Dateien) freigegeben.
        /// </summary>
        public async Task StopAsync()
        {
            lock (_stateLock)
            {
                if (_state != StateStarted)
                {
                    _state = StateStopped;
                    return;
                }
                _state = StateStopped;
            }

            _log(BunLogLevel.Info, "Server wird beendet …");

            // SHUTDOWN an JEDEN Worker: hinter alle noch ausstehenden Antworten in die
            // Warteschlange seiner ersten Verbindung legen; die Writer-Tasks schicken es
            // raus. Reagiert ein Worker nicht (z. B. weil die Verbindung schon tot ist),
            // greift unten die Kill-Frist.
            if (_channels != null && _workers != null && _connectionsPerWorker > 0)
            {
                for (int w = 0; w < _workers.Length; w++)
                    EnqueueFrame(_channels[w * _connectionsPerWorker], Protocol.BuildShutdownFrame());
            }

            if (_workers != null)
            {
                List<Task> stopping = new List<Task>(_workers.Length);
                foreach (BunProcessHost worker in _workers)
                    stopping.Add(worker.StopAsync(TimeSpan.FromSeconds(5)));
                await Task.WhenAll(stopping).ConfigureAwait(false);
            }

            await CleanupAsync().ConfigureAwait(false);
            _log(BunLogLevel.Info, "Server beendet, alle Ressourcen freigegeben.");
        }

        /// <summary>
        /// Wartet auf Strg+C (SIGINT) bzw. <c>kill</c> (SIGTERM) und fährt den Server
        /// anschließend sauber herunter. Typischer Abschluss von <c>Main</c>:
        /// <code>await server.WaitForShutdownAsync();</code>
        /// </summary>
        public async Task WaitForShutdownAsync()
        {
            TaskCompletionSource<bool> shutdown = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
#if NET
            Action<System.Runtime.InteropServices.PosixSignalContext> onSignal =
                delegate (System.Runtime.InteropServices.PosixSignalContext context)
                {
                    context.Cancel = true; // Prozess nicht hart abbrechen — wir beenden selbst
                    shutdown.TrySetResult(true);
                };
            using (System.Runtime.InteropServices.PosixSignalRegistration.Create(
                System.Runtime.InteropServices.PosixSignal.SIGINT, onSignal))
            using (System.Runtime.InteropServices.PosixSignalRegistration.Create(
                System.Runtime.InteropServices.PosixSignal.SIGTERM, onSignal))
            {
                await shutdown.Task.ConfigureAwait(false);
            }
#else
            ConsoleCancelEventHandler onCancel = delegate (object? sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                shutdown.TrySetResult(true);
            };
            Console.CancelKeyPress += onCancel;
            try { await shutdown.Task.ConfigureAwait(false); }
            finally { Console.CancelKeyPress -= onCancel; }
#endif
            await StopAsync().ConfigureAwait(false);
        }

        /// <summary>Entspricht <see cref="StopAsync"/>.</summary>
        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            _lifetime.Dispose();
        }

        private async Task CleanupAsync()
        {
            _lifetime.Cancel();

            IpcChannel[]? channels = _channels;
            _channels = null;

            if (channels != null)
            {
                foreach (IpcChannel channel in channels)
                {
                    try { channel.Stream.Dispose(); } catch (IOException) { }
                }
                foreach (IpcChannel channel in channels)
                {
                    if (channel.ReadLoop != null)
                    {
                        try { await channel.ReadLoop.ConfigureAwait(false); }
                        catch { /* Fehler des Read-Loops wurden bereits geloggt */ }
                    }
                    if (channel.WriteLoop != null)
                    {
                        try { await channel.WriteLoop.ConfigureAwait(false); }
                        catch { /* Fehler des Writer-Tasks wurden bereits geloggt */ }
                    }
                    channel.SendSignal.Dispose();
                }
            }

            if (_workers != null)
            {
                foreach (BunProcessHost worker in _workers) worker.Dispose();
                _workers = null;
            }
            if (_listeners != null)
            {
                foreach (IpcListener listener in _listeners) listener.Dispose();
                _listeners = null;
            }
        }

        // -------------------------------------------------------------------
        // Request-Verarbeitung
        // -------------------------------------------------------------------

        private async Task ReadLoopAsync(IpcChannel channel, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    byte[]? payload = await channel.Reader.ReadFrameAsync(ct).ConfigureAwait(false);
                    if (payload == null) break; // Bun hat die Verbindung geschlossen

                    if (payload.Length > 0 && payload[0] == Protocol.TypeRequest)
                    {
                        uint id;
                        BunRequest request = Protocol.ParseRequest(payload, out id);
                        // Jeder Request läuft parallel im Thread-Pool, damit der
                        // Read-Loop sofort den nächsten Frame lesen kann. Die Antwort
                        // geht über dieselbe Verbindung zurück, auf der der Request kam.
                        _ = Task.Run(() => DispatchAsync(channel, id, request), CancellationToken.None);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
                // Beim regulären Stop wird der Stream geschlossen — nur im Betrieb melden.
                if (!ct.IsCancellationRequested)
                    _log(BunLogLevel.Error, "IPC-Verbindung verloren: " + ex.Message);
            }
            catch (Exception ex)
            {
                _log(BunLogLevel.Error, "Fehler im IPC-Read-Loop: " + ex);
            }
        }

        private async Task DispatchAsync(IpcChannel channel, uint id, BunRequest request)
        {
            BunResponse response;
            try
            {
                // Automatische Eingangsprüfung: keine null- oder Leer-Werte in JSON-Bodys.
                string? validationError = ValidateRequest(request);
                if (validationError != null)
                {
                    response = BunResponse.JsonText("{\"error\":" + JsonText.Escape(validationError) + "}", 400);
                }
                else
                {
                    BunEndpointHandler? handler;
                    if (_routes.TryGetValue(request.Method + " " + request.Path, out handler))
                    {
                        response = await handler(request).ConfigureAwait(false);
                        if (response == null) response = BunResponse.StatusCode(204);
                    }
                    else
                    {
                        // Sollte nicht vorkommen: Bun leitet nur registrierte Routen weiter.
                        response = BunResponse.StatusCode(404);
                    }
                }
            }
            catch (Exception ex)
            {
                _log(BunLogLevel.Error, "Handler-Fehler bei " + request.Method + " " + request.Path + ": " + ex);
                // Bewusst ohne Details im Body — interne Fehler gehören nicht zum Client.
                response = BunResponse.Text("Internal Server Error", 500);
            }
            finally
            {
                // Upload-Temp-Datei wegräumen, falls der Handler sie nicht übernommen hat.
                if (request.BodyFilePath != "" && File.Exists(request.BodyFilePath))
                {
                    try { File.Delete(request.BodyFilePath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }

            EnqueueFrame(channel, Protocol.BuildResponseFrame(id, response));
        }

        /// <summary>Liefert eine Fehlermeldung, wenn der Request die Eingangsprüfung nicht besteht, sonst null.</summary>
        private string? ValidateRequest(BunRequest request)
        {
            if (!_options.RejectNullOrEmptyValues) return null;
            if (request.Body.Length == 0) return null;

            string contentType = request.GetHeader("Content-Type") ?? "";
            if (contentType.IndexOf("json", StringComparison.OrdinalIgnoreCase) < 0) return null;

            return JsonValidation.FindInvalidValue(request.BodyAsText());
        }

        /// <summary>Legt einen Frame in die Sendewarteschlange der Verbindung und weckt deren Writer-Task.</summary>
        private static void EnqueueFrame(IpcChannel channel, byte[] frame)
        {
            lock (channel.SendLock)
            {
                channel.SendQueue.Add(frame);
            }
            if (channel.SendSignal.CurrentCount == 0)
            {
                try { channel.SendSignal.Release(); }
                catch (SemaphoreFullException) { /* Writer ist bereits geweckt */ }
                catch (ObjectDisposedException) { /* Server wurde bereits entsorgt */ }
            }
        }

        /// <summary>
        /// Einziger Schreiber auf der jeweiligen IPC-Verbindung: sammelt alle wartenden
        /// Frames ein und schreibt sie als einen Block — bei hoher Last werden so viele
        /// Antworten mit einem einzigen Systemaufruf verschickt.
        /// </summary>
        private async Task WriteLoopAsync(IpcChannel channel, CancellationToken ct)
        {
            Stream ipc = channel.Stream;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await channel.SendSignal.WaitAsync(ct).ConfigureAwait(false);

                    List<byte[]> frames;
                    lock (channel.SendLock)
                    {
                        if (channel.SendQueue.Count == 0) continue;
                        frames = channel.SendQueue;
                        channel.SendQueue = new List<byte[]>();
                    }

                    if (frames.Count == 1)
                    {
                        await ipc.WriteAsync(frames[0].AsMemory(), ct).ConfigureAwait(false);
                    }
                    else
                    {
                        int total = 0;
                        for (int i = 0; i < frames.Count; i++) total += frames[i].Length;

                        byte[] block = new byte[total];
                        int offset = 0;
                        for (int i = 0; i < frames.Count; i++)
                        {
                            Buffer.BlockCopy(frames[i], 0, block, offset, frames[i].Length);
                            offset += frames[i].Length;
                        }
                        await ipc.WriteAsync(block.AsMemory(), ct).ConfigureAwait(false);
                    }
                    await ipc.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
                // Beim regulären Stop wird der Stream geschlossen — nur im Betrieb melden.
                if (!ct.IsCancellationRequested)
                    _log(BunLogLevel.Error, "IPC-Schreibkanal verloren: " + ex.Message);
            }
        }
    }
}

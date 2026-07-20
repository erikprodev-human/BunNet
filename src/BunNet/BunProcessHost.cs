using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace BunNet
{
    /// <summary>
    /// Verwaltet die Bun-Instanz: extrahiert das Bridge-Skript, schreibt die
    /// Konfigurationsdatei, startet den Prozess und beendet ihn wieder sauber.
    /// </summary>
    internal sealed class BunProcessHost : IDisposable
    {
        private readonly BunServerOptions _options;
        private readonly Action<BunLogLevel, string> _log;
        private readonly string _workDir;
        private readonly TaskCompletionSource<int> _exited =
            new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        private Process? _process;

        public BunProcessHost(BunServerOptions options, Action<BunLogLevel, string> log)
        {
            _options = options;
            _log = log;
            _workDir = Path.Combine(Path.GetTempPath(), "bunnet-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        }

        /// <summary>Wird abgeschlossen, sobald der Bun-Prozess endet (Ergebnis = Exit-Code).</summary>
        public Task<int> Exited => _exited.Task;

        public bool HasExited => _process == null || _process.HasExited;

        /// <summary>
        /// Extrahiert bridge.js, schreibt die Konfiguration und startet Bun.
        /// <paramref name="ipcConnections"/> ist die Anzahl paralleler IPC-Verbindungen,
        /// die Bun aufbauen soll; <paramref name="port"/> der zu bindende Port (bei
        /// mehreren Workern der bereits vom ersten Worker gebundene tatsächliche Port);
        /// <paramref name="reusePort"/> aktiviert <c>SO_REUSEPORT</c>, damit sich mehrere
        /// Worker denselben Port teilen können; <paramref name="routeKeys"/> sind die
        /// Routen im Format "METHODE /pfad"; <paramref name="uploadRouteKeys"/> die
        /// Teilmenge, deren Bodys Bun auf die Platte streamt statt in den Speicher.
        /// </summary>
        public void Start(string ipcPath, int ipcConnections, int port, bool reusePort, IEnumerable<string> routeKeys, ICollection<string> uploadRouteKeys)
        {
            // Bun-Executable auflösen: expliziter Pfad → PATH → bekannte Installationsorte.
            string bunExecutable = BunLocator.Resolve(_options.BunExecutable);
            _log(BunLogLevel.Debug, "Bun-Executable: " + bunExecutable);

            Directory.CreateDirectory(_workDir);
            Directory.CreateDirectory(Path.Combine(_workDir, "uploads"));

            var scriptPath = Path.Combine(_workDir, "bridge.js");
            ExtractBridgeScript(scriptPath);

            var configPath = Path.Combine(_workDir, "config.json");
            File.WriteAllText(configPath, BuildConfigJson(ipcPath, ipcConnections, port, reusePort, routeKeys, uploadRouteKeys), new UTF8Encoding(false));

            var startInfo = new ProcessStartInfo
            {
                FileName = bunExecutable,
                Arguments = Quote(scriptPath) + " " + Quote(configPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) _log(BunLogLevel.Info, "bun: " + e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) _log(BunLogLevel.Warning, "bun: " + e.Data);
            };
            process.Exited += (_, __) =>
            {
                int code;
                try { code = process.ExitCode; }
                catch (InvalidOperationException) { code = -1; }
                _exited.TrySetResult(code);
            };

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                process.Dispose();
                throw new InvalidOperationException(
                    $"Bun konnte nicht gestartet werden ('{_options.BunExecutable}'). " +
                    "Ist Bun installiert und im PATH? (https://bun.sh)", ex);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            _log(BunLogLevel.Debug, $"Bun gestartet (PID {process.Id}).");
        }

        /// <summary>
        /// Wartet auf das freiwillige Ende des Prozesses (nach dem SHUTDOWN-Frame);
        /// erzwingt das Ende, falls die Frist überschritten wird.
        /// </summary>
        public async Task StopAsync(TimeSpan gracePeriod)
        {
            var process = _process;
            if (process == null) return;

            if (!process.HasExited)
            {
                var finished = await Task.WhenAny(_exited.Task, Task.Delay(gracePeriod)).ConfigureAwait(false);
                if (finished != _exited.Task && !process.HasExited)
                {
                    _log(BunLogLevel.Warning, "Bun hat nicht rechtzeitig reagiert, Prozess wird beendet.");
                    try
                    {
#if NET
                        process.Kill(entireProcessTree: true);
#else
                        process.Kill();
#endif
                    }
                    catch (InvalidOperationException) { /* bereits beendet */ }
                }
            }

            _exited.TrySetResult(-1);
        }

        public void Dispose()
        {
            var process = _process;
            _process = null;

            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                    {
#if NET
                        process.Kill(entireProcessTree: true);
#else
                        process.Kill();
#endif
                    }
                }
                catch (InvalidOperationException) { }
                process.Dispose();
            }

            try { Directory.Delete(_workDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void ExtractBridgeScript(string targetPath)
        {
            using var resource = typeof(BunProcessHost).Assembly
                .GetManifestResourceStream("BunNet.bridge.js")
                ?? throw new InvalidOperationException("Eingebettete Ressource 'BunNet.bridge.js' fehlt.");
            using var file = File.Create(targetPath);
            resource.CopyTo(file);
        }

        /// <summary>
        /// Erzeugt die Bun-Konfiguration als JSON. Bewusst von Hand serialisiert,
        /// damit die Bibliothek auch unter netstandard2.1 ohne externe Pakete auskommt.
        /// </summary>
        private string BuildConfigJson(string ipcPath, int ipcConnections, int port, bool reusePort, IEnumerable<string> routeKeys, ICollection<string> uploadRouteKeys)
        {
            var json = new StringBuilder(512);
            json.Append('{');
            json.Append("\"ipc\":").Append(JsonText.Escape(ipcPath));
            json.Append(",\"ipcConnections\":").Append(ipcConnections);
            json.Append(",\"port\":").Append(port);
            json.Append(",\"reusePort\":").Append(reusePort ? "true" : "false");
            json.Append(",\"hostname\":").Append(JsonText.Escape(_options.Hostname));
            json.Append(",\"maxRequestBodySize\":").Append(_options.MaxRequestBodySize);
            json.Append(",\"maxUploadSize\":").Append(_options.MaxUploadSize);
            json.Append(",\"uploadDir\":").Append(JsonText.Escape(Path.Combine(_workDir, "uploads")));
            json.Append(",\"requestTimeoutMs\":").Append((long)_options.RequestTimeout.TotalMilliseconds);
            json.Append(",\"staticRoot\":").Append(
                _options.StaticRoot == null ? "null" : JsonText.Escape(Path.GetFullPath(_options.StaticRoot)));

            if (_options.UseHttps)
            {
                json.Append(",\"tls\":{\"certPath\":").Append(JsonText.Escape(Path.GetFullPath(_options.CertificatePemPath!)));
                json.Append(",\"keyPath\":").Append(JsonText.Escape(Path.GetFullPath(_options.PrivateKeyPemPath!)));
                if (_options.PrivateKeyPassphrase != null)
                    json.Append(",\"passphrase\":").Append(JsonText.Escape(_options.PrivateKeyPassphrase));
                json.Append('}');
            }
            else
            {
                json.Append(",\"tls\":null");
            }

            // Routen-Schlüssel haben das Format "METHODE /pfad".
            json.Append(",\"routes\":[");
            var first = true;
            foreach (var key in routeKeys)
            {
                var space = key.IndexOf(' ');
                if (!first) json.Append(',');
                first = false;
                json.Append("{\"method\":").Append(JsonText.Escape(key.Substring(0, space)))
                    .Append(",\"path\":").Append(JsonText.Escape(key.Substring(space + 1)))
                    .Append(",\"upload\":").Append(uploadRouteKeys.Contains(key) ? "true" : "false").Append('}');
            }
            json.Append("]}");
            return json.ToString();
        }

        private static string Quote(string path)
        {
            return "\"" + path.Replace("\"", "\\\"") + "\"";
        }
    }
}

using System;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace BunNet
{
    /// <summary>
    /// Plattformabhängiger IPC-Endpunkt, auf dem der .NET-Masterprozess auf die
    /// Verbindungen der Bun-Instanz wartet:
    ///   * Windows: Named Pipe (<c>\\.\pipe\bunnet-…</c>)
    ///   * Linux/macOS: Unix Domain Socket im Temp-Verzeichnis
    /// Für hohen Durchsatz werden MEHRERE parallele Verbindungen akzeptiert
    /// (<see cref="BunServerOptions.IpcConnections"/>); Bun verteilt die Requests
    /// darauf. Beide Varianten liefern <see cref="Stream"/>-Objekte, sodass die
    /// restliche Bibliothek plattformneutral bleibt.
    /// </summary>
    internal sealed class IpcListener : IDisposable
    {
        private readonly Socket? _unixSocket;
        private readonly string? _unixSocketPath;
        private readonly NamedPipeServerStream[]? _pipes;

        /// <summary>Adresse, mit der sich Bun via <c>net.connect()</c> verbindet.</summary>
        public string BunConnectPath { get; }

        public IpcListener(int connectionCount)
        {
            var name = "bunnet-" + Guid.NewGuid().ToString("N").Substring(0, 12);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Alle Pipe-Instanzen VOR dem Bun-Start anlegen, damit jede der
                // parallelen Verbindungen sofort eine freie Instanz vorfindet.
                _pipes = new NamedPipeServerStream[connectionCount];
                for (int i = 0; i < connectionCount; i++)
                {
                    _pipes[i] = new NamedPipeServerStream(
                        name,
                        PipeDirection.InOut,
                        maxNumberOfServerInstances: connectionCount,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                }
                BunConnectPath = @"\\.\pipe\" + name;
            }
            else
            {
                _unixSocketPath = Path.Combine(Path.GetTempPath(), name + ".sock");
                if (File.Exists(_unixSocketPath)) File.Delete(_unixSocketPath);

                _unixSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                _unixSocket.Bind(new UnixDomainSocketEndPoint(_unixSocketPath));
                _unixSocket.Listen(connectionCount);
                BunConnectPath = _unixSocketPath;
            }
        }

        /// <summary>
        /// Wartet auf die eingehende Verbindung mit der angegebenen Nummer
        /// (0 … Verbindungsanzahl − 1); der Aufrufer akzeptiert sie nacheinander.
        /// </summary>
        public async Task<Stream> AcceptAsync(int index, CancellationToken ct)
        {
            if (_pipes != null)
            {
                await _pipes[index].WaitForConnectionAsync(ct).ConfigureAwait(false);
                return _pipes[index];
            }

            using (ct.Register(() => _unixSocket!.Dispose()))
            {
                try
                {
                    var connection = await _unixSocket!.AcceptAsync().ConfigureAwait(false);
                    return new NetworkStream(connection, ownsSocket: true);
                }
                catch (ObjectDisposedException) when (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }
                catch (SocketException) when (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }
            }
        }

        public void Dispose()
        {
            if (_pipes != null)
            {
                foreach (var pipe in _pipes) pipe.Dispose();
            }
            _unixSocket?.Dispose();

            if (_unixSocketPath != null)
            {
                try { File.Delete(_unixSocketPath); }
                catch (IOException) { /* bereits entfernt oder gesperrt — unkritisch */ }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}

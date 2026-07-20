using System;

namespace BunNet
{
    /// <summary>Log-Stufen der Bibliothek.</summary>
    public enum BunLogLevel
    {
        /// <summary>Interne Details (IPC-Pfade, Prozess-IDs).</summary>
        Debug,
        /// <summary>Normale Betriebsmeldungen.</summary>
        Info,
        /// <summary>Unerwartete, aber verkraftbare Zustände.</summary>
        Warning,
        /// <summary>Fehler, die eine Anfrage oder den Server betreffen.</summary>
        Error,
    }

    /// <summary>Konfiguration für einen <see cref="BunServer"/>.</summary>
    public sealed class BunServerOptions
    {
        /// <summary>
        /// TCP-Port, auf dem Bun lauscht. <c>0</c> lässt das Betriebssystem einen
        /// freien Port wählen; der tatsächliche Port steht nach dem Start in
        /// <see cref="BunServer.Port"/>.
        /// </summary>
        public int Port { get; set; } = 8080;

        /// <summary>Hostname/Interface, an das Bun bindet (Standard: nur lokal).</summary>
        public string Hostname { get; set; } = "127.0.0.1";

        /// <summary>
        /// Optionales Verzeichnis mit statischen Dateien (HTML/CSS/JS). GET/HEAD-Requests,
        /// die keinem registrierten Endpoint entsprechen, werden direkt von Bun aus diesem
        /// Verzeichnis bedient — ohne Umweg über .NET. <c>null</c> deaktiviert die Funktion.
        /// </summary>
        public string? StaticRoot { get; set; }

        /// <summary>Pfad zum Server-Zertifikat im PEM-Format. Aktiviert zusammen mit <see cref="PrivateKeyPemPath"/> HTTPS.</summary>
        public string? CertificatePemPath { get; set; }

        /// <summary>Pfad zum privaten Schlüssel im PEM-Format.</summary>
        public string? PrivateKeyPemPath { get; set; }

        /// <summary>Optionale Passphrase des privaten Schlüssels.</summary>
        public string? PrivateKeyPassphrase { get; set; }

        /// <summary>
        /// Pfad zur Bun-Executable. Standard: <c>bun</c> — wird automatisch gesucht
        /// (PATH, danach bekannte Installationsorte wie <c>~/.bun/bin</c>, Homebrew,
        /// winget, scoop). Findet die Suche Bun nicht, den vollständigen Pfad VOR dem
        /// Start setzen: <c>options.BunExecutable = "/pfad/zu/bun";</c>
        /// </summary>
        public string BunExecutable { get; set; } = "bun";

        /// <summary>
        /// Anzahl paralleler IPC-Verbindungen zwischen Bun und .NET (pro Worker).
        /// Requests werden von Bun auf die Verbindungen verteilt und von .NET parallel
        /// gelesen, verarbeitet und beantwortet — das erhöht den Durchsatz spürbar.
        /// <c>0</c> (Standard) = automatisch anhand der CPU-Kerne wählen.
        /// </summary>
        public int IpcConnections { get; set; } = 0;

        /// <summary>
        /// Anzahl der Bun-Prozesse (Worker), die sich per <c>SO_REUSEPORT</c> denselben
        /// Port teilen. Jeder Worker bringt eine eigene Bun-Instanz samt eigener
        /// IPC-Verbindungen mit und hebt so die Single-Thread-Obergrenze von Bun auf —
        /// der Durchsatz skaliert nahezu linear mit den Workern.
        /// WICHTIG: Das Betriebssystem verteilt die Verbindungen nur unter <b>Linux</b>
        /// auf die Worker; unter macOS/Windows erhält meist nur eine Instanz die Last.
        /// <c>1</c> (Standard) = ein Bun-Prozess; <c>0</c> = automatisch
        /// (unter Linux anhand der CPU-Kerne, sonst 1).
        /// </summary>
        public int BunWorkers { get; set; } = 1;

        /// <summary>Maximale Request-Body-Größe in Bytes (Standard: 16 MiB).</summary>
        public long MaxRequestBodySize { get; set; } = 16 * 1024 * 1024;

        /// <summary>
        /// Maximale Dateigröße in Bytes für Upload-Endpoints (<see cref="BunServer.MapUpload(string, BunEndpointHandler)"/>).
        /// Upload-Bodys werden von Bun auf die Platte gestreamt statt in den Speicher —
        /// auch viele GiB belasten das RAM nicht. <c>0</c> (Standard) = kein Limit,
        /// nur der freie Plattenplatz begrenzt.
        /// </summary>
        public long MaxUploadSize { get; set; } = 0;

        /// <summary>
        /// Zeit, die ein C#-Handler maximal für eine Antwort hat, bevor Bun dem Client
        /// <c>504 Gateway Timeout</c> liefert (Standard: 30 Sekunden).
        /// </summary>
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Wartezeit auf die Bereit-Meldung von Bun beim Start (Standard: 15 Sekunden).</summary>
        public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Automatische Eingangsprüfung (Standard: aktiv). JSON-Bodys, die
        /// <c>null</c>-Werte, leere oder nur aus Leerraum bestehende Werte enthalten,
        /// werden abgefangen und mit <c>400 Bad Request</c> beantwortet, bevor der
        /// Handler überhaupt aufgerufen wird.
        /// </summary>
        public bool RejectNullOrEmptyValues { get; set; } = true;

        /// <summary>
        /// Log-Senke. Standard: Ausgabe auf der Konsole. Auf <c>null</c> setzen,
        /// um Logging zu deaktivieren.
        /// </summary>
        public Action<BunLogLevel, string>? Log { get; set; } = DefaultLog;

        /// <summary><c>true</c>, wenn HTTPS konfiguriert ist.</summary>
        public bool UseHttps => CertificatePemPath != null && PrivateKeyPemPath != null;

        /// <summary>
        /// Aktiviert HTTPS mit einem selbstsignierten Zertifikat aus <paramref name="directory"/>;
        /// fehlende PEM-Dateien werden automatisch erzeugt. Das Verzeichnis bewusst NEBEN
        /// <see cref="StaticRoot"/> wählen, damit der private Schlüssel nie als statische
        /// Datei ausgeliefert werden kann.
        /// </summary>
        public BunServerOptions UseSelfSignedCertificate(string directory, string hostName = "localhost")
        {
            string certPath;
            string keyPath;
            BunCertificate.EnsureSelfSigned(directory, hostName, out certPath, out keyPath);
            CertificatePemPath = certPath;
            PrivateKeyPemPath = keyPath;
            return this;
        }

        private static void DefaultLog(BunLogLevel level, string message)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{level,-7}] {message}");
        }
    }
}

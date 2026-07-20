// BunNet.Sample — Program.cs enthält nur die Initialisierung.
// Die Endpoints liegen im Ordner api/, Benutzer/Sitzungen in Auth.cs.
//
// Start: dotnet run

using BunNet;
using BunNet.Sample;

BunServerOptions options = new BunServerOptions();
options.Port = 8080;
options.Hostname = "127.0.0.1";
options.StaticRoot = Path.Combine(AppContext.BaseDirectory, "Web");

// Bun wird automatisch gesucht (PATH, danach ~/.bun/bin, Homebrew, winget, scoop).
// Wird Bun dabei NICHT gefunden, den Pfad hier VOR dem Start explizit angeben:
// options.BunExecutable = "/pfad/zu/bun";              // Linux/macOS
// options.BunExecutable = @"C:\pfad\zu\bun.exe";       // Windows

// Anzahl paralleler IPC-Verbindungen zwischen Bun und .NET (pro Worker).
// Standard 0 = automatisch anhand der CPU-Kerne; nur bei Bedarf anpassen:
// options.IpcConnections = 4;

// Mehrere Bun-Prozesse teilen sich per SO_REUSEPORT den Port und heben die
// Single-Thread-Obergrenze von Bun auf — die Lastverteilung übernimmt der
// Kernel, das funktioniert nur unter LINUX. 0 = automatisch (Linux: Kerne/2):
// options.BunWorkers = 4;

// HTTPS: selbstsignierte Zertifikate aus dem Ordner cert/ laden; fehlende
// PEM-Dateien werden automatisch erzeugt. Der Ordner liegt bewusst NEBEN Web/,
// damit der private Schlüssel nie als statische Datei ausgeliefert werden kann.
options.UseSelfSignedCertificate(Path.Combine(AppContext.BaseDirectory, "cert"));

BunServer server = new BunServer(options);
server.MapPost("/api/login", Api.Login);
server.MapPost("/api/logout", Api.Logout);
server.MapPost("/api/profile", Api.Profile);
server.MapUpload("/api/upload", Api.Upload); // Body wird auf die Platte gestreamt

await server.StartAsync();

Console.WriteLine();
Console.WriteLine("  Website:  " + server.Url);
Console.WriteLine("  Login:    admin / geheim123  (oder erik / passwort42)");
Console.WriteLine("  Hinweis:  selbstsigniertes Zertifikat — Browser-Warnung einmal bestätigen");
Console.WriteLine("  Beenden:  Strg+C");
Console.WriteLine();

await server.WaitForShutdownAsync();

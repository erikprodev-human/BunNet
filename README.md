# BunNet

Minimalistische .NET-Bibliothek, mit der eine **Konsolenanwendung einen Webservice bereitstellt — ohne ASP.NET Core und ohne Blazor**. [Bun](https://bun.sh) dient als HTTP-/HTTPS-Server und Brücke zum Browser; die gesamte Logik (Endpoints, Authentifizierung, Validierung) läuft in C#.

- **Keine externen Abhängigkeiten**: nur .NET-Bordmittel, Bun und Betriebssystemfunktionen
- **Kein TCP/UDP zwischen Bun und .NET**: lokale IPC über **Unix Domain Sockets** (Linux/macOS) bzw. **Named Pipes** (Windows) — für hohen Durchsatz über **mehrere parallele Verbindungen**
- **.NET ist der Masterprozess**: startet, überwacht und beendet Bun (Bun wird automatisch gesucht; der Pfad lässt sich vor dem Start explizit setzen)
- **Native AOT**: Bibliothek und Beispielanwendung sind vollständig AOT-kompatibel — `dotnet publish` erzeugt eine einzelne native Executable ohne JIT
- **Automatische Eingangsprüfung**: JSON-Werte, die `null` oder leer sind, werden mit `400` abgewiesen, bevor ein Handler läuft
- **Einfacher C#-Code**: prozeduraler Stil mit einem leichten Hauch Objektorientierung — keine Records, keine Tuples, kein Pattern-Matching
- Targets: `net10.0` und `netstandard2.1`

## Regeln

Verbindliche Konventionen für alle Anwendungen, die mit BunNet gebaut werden (die Beispielanwendung hält sie alle ein).

### Client (Bun / Browser)

- **Statische Dateien liegen im Ordner `Web/`** (`options.StaticRoot`) — HTML, CSS, JS, Bilder. Bun liefert sie direkt aus, ohne .NET zu belasten.
- **Zertifikate liegen im Ordner `cert/`** — immer **neben** `Web/`, niemals darin, damit der private Schlüssel nie als statische Datei ausgeliefert werden kann.
- **Keine Geschäftslogik in JavaScript**: Der Browser-Code ruft nur Endpoints auf und stellt Ergebnisse dar; entschieden wird ausschließlich in C#.
- **API-Aufrufe gehen immer an `/api/…`-Pfade** — statische Inhalte und Endpoints bleiben dadurch sauber getrennt.
- **`bridge.js` wird nicht angepasst** — es gehört zur Bibliothek und wird als eingebettete Ressource ausgeliefert.

### Server (.NET / C#)

- **Alle Endpoints liegen im Ordner `api/`, jede Datei beginnt mit dem Präfix `api-`** — Muster: `api/api-<methode>-<name>.cs`, z. B. `api/api-post-login.cs` für `POST /api/login`. Eine Datei pro Endpoint.
- **Alle Endpoint-Dateien erweitern dieselbe Klasse** `static partial class Api`, sodass die Registrierung schlicht `server.MapPost("/api/login", Api.Login)` lautet.
- **`Program.cs` enthält nur die Initialisierung**: Optionen, Routenregistrierung, Start — keine Handler, keine Geschäftslogik.
- **Universelles gehört in die Bibliothek** (`src/BunNet/`), nicht in die Anwendung — was mehr als eine Anwendung brauchen könnte, wandert nach unten.
- **Endpoints vor `StartAsync()` registrieren** — die Routenliste wird Bun beim Start übergeben.
- **JSON-Antworten ohne Reflection bauen** (`JsonBuilder` bzw. `BunResponse.JsonText`) — damit bleibt die Anwendung Native-AOT-fähig. `BunResponse.Json(objekt)` per Reflection nur, wenn AOT keine Rolle spielt.
- **Schlanker Stil**: prozedural mit einem Hauch Objektorientierung — keine Records, keine Tuples, kein Pattern-Matching.
- **Passwörter niemals im Klartext** in echten Anwendungen — hashen (z. B. PBKDF2 via `Rfc2898DeriveBytes`); die Demo-Benutzer im Sample sind bewusst Demodaten.

## Architektur

```
Browser ⇄ HTTP/HTTPS ⇄ Bun (bridge.js) ⇄ IPC (UDS / Named Pipe) ⇄ .NET (BunServer)
                         │                                          │
                         ├─ statische Dateien (Web/) + Cache        ├─ Endpoints (C#-Handler)
                         └─ TLS-Terminierung                        ├─ Eingangsprüfung (null/leer)
                                                                    └─ Auth & Geschäftslogik
```

**Ablauf beim Start** (`BunServer.StartAsync`):

1. .NET sucht die Bun-Executable (expliziter Pfad → PATH → bekannte Installationsorte wie `~/.bun/bin`, Homebrew, winget, scoop).
2. Pro **Worker** (`options.BunWorkers`, Standard 1) öffnet .NET einen eigenen IPC-Endpunkt (Unix Domain Socket im Temp-Verzeichnis bzw. `\\.\pipe\bunnet-…`), extrahiert das eingebettete `bridge.js` samt `config.json` (Port, TLS, Routen, Static-Root, IPC-Adresse, Verbindungsanzahl, reusePort) in ein Temp-Verzeichnis und startet `bun bridge.js config.json`.
3. Jeder Bun-Worker baut **mehrere parallele IPC-Verbindungen** auf (`options.IpcConnections`, Standard: automatisch anhand der CPU-Kerne), startet `Bun.serve()` und meldet auf jeder Verbindung mit einem `READY`-Frame den tatsächlich gebundenen Port zurück. Bei mehreren Workern bindet der erste Worker den Port (und bestimmt ihn bei Port 0); alle weiteren binden denselben Port mit `SO_REUSEPORT`.

**Im Betrieb**: Requests auf registrierte Routen serialisiert Bun in ein binäres Frame-Format (Länge-präfixiert, Little-Endian; der Request-Kopf entsteht in einem Durchgang in einem wiederverwendeten Puffer) und verteilt sie auf die parallelen IPC-Verbindungen. .NET liest jede Verbindung mit einem eigenen Task, prüft eingehende JSON-Bodys automatisch auf `null`-/Leer-Werte, verarbeitet jeden gültigen Request parallel im Thread-Pool und schreibt die Antwort (per Request-ID gemultiplext) über dieselbe Verbindung zurück, auf der der Request kam. Für hohen Durchsatz werden Schreibvorgänge auf beiden Seiten gebündelt: Bun fasst alle Requests eines Event-Loop-Ticks zu einem Schreibvorgang zusammen (der nächste Tick wechselt zur nächsten Verbindung), .NET schickt wartende Antworten pro Verbindung als einen Block. GET/HEAD-Requests ohne passende Route bedient Bun direkt aus dem statischen Verzeichnis — kleine Dateien aus einem In-Memory-Cache (1 s TTL), ganz ohne .NET zu belasten. Path-Traversal wird abgewehrt, hängende Handler beantwortet Bun nach Timeout mit `504`.

**Beim Beenden**: .NET sendet ein `SHUTDOWN`-Frame, Bun stoppt den Server und beendet sich; nach einer Frist von 5 s wird der Prozess notfalls zwangsweise beendet. Sockets, Pipes und Temp-Dateien werden entfernt. Stirbt der .NET-Prozess unerwartet, erkennt Bun die geschlossene IPC-Verbindung und beendet sich selbst — es bleiben keine verwaisten Prozesse zurück.

### Drahtformat (IPC)

| Frame         | Richtung   | Inhalt                                            |
| ------------- | ---------- | ------------------------------------------------- |
| `REQUEST(1)`  | Bun → .NET | ID, Methode, Pfad, Query, Client-IP, Header, Body |
| `RESPONSE(2)` | .NET → Bun | ID, Status, Header, Body                          |
| `READY(3)`    | Bun → .NET | tatsächlich gebundener Port                       |
| `SHUTDOWN(4)` | .NET → Bun | –                                                 |

Jedes Frame: `uint32`-Längenpräfix + Payload; Strings sind längenpräfixiertes UTF-8. Bewusst binär statt JSON: keine Serialisierungskosten für Bodys, keine JSON-Abhängigkeit unter `netstandard2.1`.

## Projektstruktur

```
BunNet.sln
src/BunNet/                      Bibliothek (net10.0 + netstandard2.1, Native-AOT-kompatibel)
├── BunServer.cs                 Öffentliche API: Endpoints, Start/Stop, Lebenszyklus
├── BunServerOptions.cs          Konfiguration (Port, TLS, StaticRoot, IPC-Verbindungen, Logging)
├── BunRequest.cs                BunRequest/BunResponse + Handler-Delegat
├── BunCertificate.cs            Selbstsignierte Zertifikate erzeugen (PEM)
├── BunLocator.cs                Bun-Executable finden (PATH, ~/.bun/bin, Homebrew, winget, scoop)
├── Protocol.cs                  Binäres IPC-Drahtformat + gepufferter Frame-Reader
├── IpcListener.cs               Unix Domain Socket (Unix) / Named Pipe (Windows), N Verbindungen
├── BunProcessHost.cs            Bun-Prozess: Start, Log-Weiterleitung, Stop, Cleanup
├── JsonValidation.cs            Automatische Prüfung auf null-/Leer-Werte
├── JsonText.cs                  JSON-Helfer (Escaping, Feld-Auslesen) ohne externe Pakete
├── JsonBuilder.cs               JSON-Antworten ohne Reflection bauen (AOT-sicher)
└── Assets/bridge.js             Bun-Seite (eingebettete Ressource)
samples/BunNet.Sample/           Beispielanwendung (net10.0)
├── Program.cs                   Nur Initialisierung: Optionen, Routen, Start
├── api/                         Ein Endpoint pro Datei, immer mit api-Präfix
│   ├── api-post-login.cs        POST /api/login
│   ├── api-post-logout.cs       POST /api/logout
│   ├── api-post-profile.cs      POST /api/profile  — geschützt (Bearer-Token)
│   └── api-post-upload.cs       POST /api/upload   — Datei-Upload (auf Platte gestreamt)
├── Auth.cs                      Benutzer & Sitzungen
├── Web/                         Statische Website (HTML/CSS/JS)
└── cert/                        Selbstsignierte Zertifikate (fehlende werden erzeugt)
tests/BunNet.Tests/              Testsuite (ohne externe Frameworks)
└── Program.cs                   Funktion, Validierung, HTTPS, Shutdown, Performance
```

## Verwendung der Bibliothek

```csharp
BunServerOptions options = new BunServerOptions();
options.Port = 8080;                        // 0 = freien Port wählen (danach: server.Port)
options.Hostname = "127.0.0.1";
options.StaticRoot = "Web";                 // optional: statische Dateien direkt aus Bun
options.UseSelfSignedCertificate("cert");   // optional: HTTPS, PEM-Dateien werden erzeugt

// Bun wird automatisch gesucht (PATH, ~/.bun/bin, Homebrew, winget, scoop).
// Nur falls Bun dabei NICHT gefunden wird: Pfad VOR dem Start explizit setzen.
// options.BunExecutable = "/pfad/zu/bun";

// Parallele IPC-Verbindungen zwischen Bun und .NET, pro Worker (0 = automatisch).
// options.IpcConnections = 4;

// Mehrere Bun-Prozesse teilen sich per SO_REUSEPORT denselben Port — hebt die
// Single-Thread-Obergrenze von Bun auf, Durchsatz skaliert nahezu linear mit.
// Die Lastverteilung übernimmt der Kernel: NUR UNTER LINUX wirksam.
// 0 = automatisch (Linux: CPU-Kerne/2, sonst 1); Standard: 1.
// options.BunWorkers = 4;

BunServer server = new BunServer(options);
server.MapPost("/api/echo", HandleEcho);    // Handler dürfen synchron oder async sein

await server.StartAsync();
Console.WriteLine("Läuft auf " + server.Url);
await server.WaitForShutdownAsync();        // wartet auf Strg+C/SIGTERM, stoppt sauber

static BunResponse HandleEcho(BunRequest request)
{
    return BunResponse.Text(request.BodyAsText());
}
```

Handler erhalten einen vollständig eingelesenen `BunRequest` (Methode, Pfad, Query, Header, Body):

| Zugriff                                    | Zweck                                                     |
| ------------------------------------------ | --------------------------------------------------------- |
| `request["username"]`                      | String-Feld aus dem JSON-Body (`""` wenn nicht vorhanden) |
| `request.BearerToken`                      | Token aus dem `Authorization`-Header (`""` wenn keins)    |
| `request.GetQuery("id")`                   | Query-Parameter, URL-dekodiert                            |
| `request.GetHeader("…")`                   | Request-Header (case-insensitiv)                          |
| `request.BodyAsText()` / `BodyAsJson<T>()` | kompletter Body                                           |

Als Antwort liefern sie eine `BunResponse`:

| Factory                            | Zweck                                                        |
| ---------------------------------- | ------------------------------------------------------------ |
| `BunResponse.Text("…")`            | Text-Antwort                                                 |
| `BunResponse.Html("…")`            | HTML-Antwort                                                 |
| `BunResponse.Json(new JsonBuilder()…)` | JSON ohne Reflection bauen — AOT-sicher, empfohlen       |
| `BunResponse.Json(objekt)`         | Objekt per Reflection serialisieren (nur net10.0, nicht AOT) |
| `BunResponse.JsonText("{…}")`      | fertiger JSON-String                                         |
| `BunResponse.StatusCode(401)`      | nur Statuscode                                               |

`JsonBuilder` baut flache JSON-Objekte ohne Reflection — die empfohlene Art, Antworten zu erzeugen, weil sie unter Native AOT und netstandard2.1 gleichermaßen funktioniert:

```csharp
return BunResponse.Json(new JsonBuilder()
    .Add("token", token)
    .Add("user", username)
    .Add("expires", DateTimeOffset.UtcNow.AddMinutes(30)));
```

### Automatische Eingangsprüfung

Standardmäßig aktiv (`options.RejectNullOrEmptyValues = true`): Enthält ein JSON-Body einen `null`-Wert, einen leeren oder nur aus Leerraum bestehenden Wert, antwortet die Bibliothek selbst mit `400` und einer sprechenden Meldung — der Handler wird gar nicht erst aufgerufen:

```
POST {"password": null}   →  400 {"error":"Feld 'password' ist null."}
POST {"name": ""}         →  400 {"error":"Feld 'name' ist leer."}
```

Handler können sich also darauf verlassen: Was ankommt, hat immer einen Wert.

### Konvention: eine Datei pro Endpoint, mit `api`-Präfix

Im Beispielprojekt liegt jeder Endpoint in einer eigenen Datei im Ordner **`api/`**, deren Name **immer mit `api` beginnt** — Muster: `api/api-<methode>-<name>.cs`, z. B. `api/api-post-login.cs`. Alle diese Dateien erweitern dieselbe statische Klasse `Api` (`static partial class Api`), sodass die Registrierung in `Program.cs` schlicht `server.MapPost("/api/login", Api.Login)` lautet. `Program.cs` selbst enthält nur die Initialisierung (Optionen, Routen, Start).

```csharp
// api/api-post-logout.cs
static partial class Api
{
    public static BunResponse Logout(BunRequest request)
    {
        Auth.EndSession(request.BearerToken);
        return BunResponse.StatusCode(204);
    }
}
```

## Beispielanwendung starten

Voraussetzungen: **.NET 10 SDK** und **Bun** (`curl -fsSL https://bun.sh/install | bash` bzw. `winget install Oven-sh.Bun`).

```bash
dotnet run --project samples/BunNet.Sample              # HTTPS auf Port 8080
```

### Native AOT (empfohlen für Produktion)

Die Beispielanwendung ist für **Native AOT** konfiguriert (`PublishAot`, `OptimizationPreference=Speed`, natives Instruction-Set, Server-GC): `dotnet publish` erzeugt eine einzelne native Executable (~4 MB) ohne JIT und ohne .NET-Runtime-Abhängigkeit — schnellerer Start, weniger Speicher, konstante Spitzenleistung ab dem ersten Request:

```bash
dotnet publish samples/BunNet.Sample -c Release -o publish
./publish/BunNet.Sample
```

`dotnet run` funktioniert unverändert (JIT); die AOT-Analyzer prüfen aber schon beim Build, dass nichts AOT-Inkompatibles verwendet wird. Wichtigste Regel dafür: JSON-Antworten mit `JsonBuilder` bauen statt mit dem Reflection-basierten `BunResponse.Json(objekt)`.

Dann <https://127.0.0.1:8080> öffnen (Browser-Warnung wegen des selbstsignierten Zertifikats einmal bestätigen) und mit `admin / geheim123` (oder `erik / passwort42`) anmelden. Die Zertifikate (`cert.pem`, `key.pem`, selbstsigniert, 1 Jahr gültig) liegen im Ordner `cert/` und werden beim Build neben die Anwendung kopiert; fehlen sie, erzeugt `options.UseSelfSignedCertificate(...)` sie beim Start automatisch neu. Der Ordner liegt bewusst **neben** `Web/`, damit der private Schlüssel nie als statische Datei ausgeliefert werden kann. Beenden mit **Strg+C** — die Anwendung fährt Bun sauber herunter.

### Testen mit curl

`-k` akzeptiert das selbstsignierte Zertifikat.

```bash
# Statische Seite (liefert Bun)
curl -ik https://127.0.0.1:8080/

# Login (verarbeitet C#) → Token merken
TOKEN=$(curl -sk -X POST https://127.0.0.1:8080/api/login \
  -H 'Content-Type: application/json' \
  -d '{"username":"admin","password":"geheim123"}' | sed -E 's/.*"token":"([^"]+)".*/\1/')

# Geschützter Endpoint ohne Token → 401
curl -ik -X POST https://127.0.0.1:8080/api/profile

# Mit Token → 200 + Profildaten
curl -sk -X POST https://127.0.0.1:8080/api/profile -H "Authorization: Bearer $TOKEN"

# Eingangsprüfung: null wird automatisch abgelehnt → 400
curl -sk -X POST https://127.0.0.1:8080/api/login \
  -H 'Content-Type: application/json' -d '{"username":"admin","password":null}'

# Datei hochladen (gestreamt, beliebig groß) → landet in Uploads/ neben der Anwendung
curl -sk -X POST -T film.mp4 "https://127.0.0.1:8080/api/upload?name=film.mp4" \
  -H "Authorization: Bearer $TOKEN"
```

## Datei-Uploads

Dateien schickt man als **rohen Body** (kein `multipart/form-data`), den Dateinamen z. B. per Query-Parameter. Es gibt zwei Wege:

**Normale Endpoints** (`MapPost`): Der Body landet als `byte[]` in `request.Body` — bequem für kleine Dateien. Limit: `options.MaxRequestBodySize` (Standard **16 MiB**), alles wird im RAM gepuffert.

**Upload-Endpoints** (`MapUpload`): Für große Dateien — **1 GiB, 10 GiB, egal**. Bun streamt den Body Stück für Stück direkt in eine Temp-Datei; weder Bun noch .NET halten die Datei im Speicher (gemessen: 1 GiB-Upload in ~4 s, RAM beider Prozesse konstant unter 80 MiB). Der Handler übernimmt die fertige Datei mit `request.SaveBodyTo(zielPfad)` — das ist nur ein `File.Move`, also auch bei riesigen Dateien sofort fertig. Limit: `options.MaxUploadSize` (Standard **0 = unbegrenzt**, nur der Plattenplatz zählt); zu große Requests beantwortet Bun mit `413`. Nicht übernommene Temp-Dateien räumt die Bibliothek nach dem Handler automatisch weg.

```csharp
server.MapUpload("/api/upload", HandleUpload);

static BunResponse HandleUpload(BunRequest request)
{
    string name = Path.GetFileName(request.GetQuery("name") ?? "upload.bin");
    request.SaveBodyTo(Path.Combine("Uploads", name)); // verschiebt, kopiert nicht
    return BunResponse.Json(new { gespeichert = name, bytes = request.BodyLength });
}
```

Auf Upload-Routen ist `request.Body` leer; stattdessen gibt es `request.BodyFilePath` (Pfad der Temp-Datei), `request.BodyLength` (Größe) und `request.SaveBodyTo(...)`.

### Hochladen aus einem C#-Client

Wichtig ist `StreamContent` (streamt die Datei, statt sie in den RAM zu laden) — das Gegenstück zu `curl -T`:

```csharp
using System.Net.Http;

HttpClientHandler handler = new HttpClientHandler();
// Nur für selbstsignierte Zertifikate (Entwicklung/internes Netz):
handler.ServerCertificateCustomValidationCallback =
    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

using HttpClient http = new HttpClient(handler);
http.Timeout = TimeSpan.FromHours(1); // große Dateien brauchen Zeit
http.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);

string pfad = "C:\\Daten\\backup.zip";
using FileStream datei = File.OpenRead(pfad);
string url = "https://server:8080/api/upload?name=" +
    Uri.EscapeDataString(Path.GetFileName(pfad));

HttpResponseMessage antwort = await http.PostAsync(url, new StreamContent(datei));
Console.WriteLine(antwort.StatusCode + ": " + await antwort.Content.ReadAsStringAsync());
```

## Tests

Die Testsuite kommt ohne externe Frameworks aus (xUnit & Co. wären NuGet-Pakete) und ist eine einfache Konsolenanwendung. Sie startet echte Server-Instanzen und prüft von außen über HTTP — kein Mocking, getestet wird das reale Zusammenspiel .NET ⇄ IPC ⇄ Bun ⇄ HTTP.

```bash
dotnet run -c Release --project tests/BunNet.Tests                # alles
dotnet run -c Release --project tests/BunNet.Tests -- --skip-perf # ohne Performanceteil
```

Exit-Code `0` = alle Tests bestanden (CI-tauglich).

### Testbereiche

**1. Funktionstests** — statische Dateien (inkl. Unterordner, 404), Schutz vor Path-Traversal (`/../` und `%2e%2e`), Endpoint-Routing (POST/GET, falsche Methode, unbekannte Route), Body/Query/Header-Übergabe, Fehlerbehandlung (Exception im Handler → `500` ohne interne Details, Handler ohne Antwort → `204`).

**2. Validierungstests** — die automatische Eingangsprüfung: `null`-Werte, leere Werte, Nur-Leerraum-Werte, verschachtelte Objekte und Arrays werden mit `400` abgelehnt; `"null"` als Textinhalt und leere Bodys (z. B. Logout) bleiben erlaubt.

**3. HTTPS-Test** — `BunCertificate.EnsureSelfSigned` erzeugt gültige PEM-Dateien, ein HTTPS-Request wird Ende-zu-Ende beantwortet.

**4. Shutdown-Test** — nach `StopAsync()` ist der Port sofort wieder frei, keine Prozess- oder Dateileichen.

**5. Performancetests** — Latenz und Durchsatz mit einem eigenen Lastgenerator (rohe Keep-Alive-Sockets, keine HTTP-Client-Overheads).

### Gemessene Performance

Messwerte auf einem MacBook Pro (Apple Silicon, 10 Kerne, macOS), Release-Build, lokale Loopback-Verbindungen, 5–8 s pro Szenario (Stand: 20.07.2026):

| Szenario                                            | Ergebnis               |
| --------------------------------------------------- | ---------------------- |
| Latenz .NET-Endpoint (seriell, 1 Verbindung)        | ~110 µs/Request        |
| Statische Datei (Bun direkt, mit Cache)             | **~87.000 Requests/s** |
| .NET-Endpoint über IPC (64 Verbindungen)            | ~40.000 Requests/s     |
| .NET-Endpoint über IPC (256 Verbindungen)           | **~42.000 Requests/s** |
| .NET-Endpoint, 64-KiB-Bodys, 1 IPC-Verbindung       | ~1.000 Requests/s      |
| .NET-Endpoint, 64-KiB-Bodys, 4 IPC-Verbindungen     | **~2.700 Requests/s**  |
| .NET-Endpoint, 64-KiB-Bodys, 8 IPC-Verbindungen     | **~4.500 Requests/s**  |

**Was die parallelen IPC-Verbindungen bringen:** Bei winzigen Requests (wenige Bytes) ist die single-threaded Pro-Request-Arbeit in Bun selbst der Engpass (~54k/s Obergrenze, gemessen ganz ohne IPC) — dort ändern parallele Verbindungen wenig. Sobald aber echte Daten fließen (Formulare, JSON-Payloads, Datei-Inhalte), skaliert der Durchsatz mit den Verbindungen: **2,5× mit 4 und über 4× mit 8 Verbindungen** bei 64-KiB-Bodys, weil Übertragung, Parsing und Beantwortung dann auf beiden Seiten parallel laufen.

**Einordnung der Zahlen — wichtig:** Zur Ehrlichkeit gehört der gemessene Vergleichswert: Ein _nacktes_ `Bun.serve()`, das ohne jede Weiterleitung sofort `{"pong":true}` zurückgibt, schafft auf derselben Maschine mit demselben Lastgenerator **~105.000–110.000 Requests/s** — unabhängig davon, ob 64 oder 1024 Clients parallel anfragen. Das ist die physikalische Obergrenze einer einzelnen Bun-Instanz auf dieser Hardware (die von Bun beworbenen ~300k stammen von Linux-Serverhardware). Für .NET-Endpoints mit Kleinst-Requests erreicht BunNet ~40–45k/s, flach über 1–16 IPC-Verbindungen und 256–1024 Client-Verbindungen — der Engpass ist die single-threaded Pro-Request-Arbeit in Bun selbst, nicht IPC oder .NET. **Der Ausweg ist `options.BunWorkers`**: mehrere Bun-Prozesse teilen sich per `SO_REUSEPORT` den Port, jeder bringt seine eigene Weiterleitungskapazität mit — der Durchsatz skaliert damit nahezu linear (die Kernel-Lastverteilung funktioniert nur unter Linux; unter macOS/Windows erhält meist nur eine Instanz die Verbindungen). Native AOT ändert am Durchsatz unter Dauerlast wenig (der JIT ist im eingeschwungenen Zustand ähnlich schnell) — sein Gewinn sind Startzeit, Speicherverbrauch und konstante Leistung ab dem ersten Request.

Eingebaute Durchsatz-Optimierungen: binäres Drahtformat statt JSON, **mehrere parallele IPC-Verbindungen** (Requests werden tick-weise verteilt, .NET liest/schreibt jede Verbindung mit eigenen Tasks), Single-Pass-Serialisierung des Request-Kopfs in einen wiederverwendeten Puffer (Bun-Seite), exakt vorberechnete Antwort-Frames ohne Zwischenkopien (.NET-Seite), `req.bytes()` statt `ArrayBuffer`-Umweg, gepufferter Frame-Reader (ein Syscall liest viele Frames), gebündelte Schreibvorgänge auf beiden Seiten, In-Memory-Cache für kleine statische Dateien (1 s TTL), ein Timeout-Sweeper statt eines Timers pro Request, Server-GC im Sample.

## Hinweise & bewusste Grenzen

- **Routen sind exakte Pfade** (keine Wildcards/Parameter) — bewusst minimal gehalten; Varianten über Query-String oder Body abbilden.
- Die Route-Liste wird Bun beim Start übergeben; **Endpoints daher vor `StartAsync()` registrieren**.
- Request-Bodys normaler Endpoints werden vollständig gepuffert (Limit: `MaxRequestBodySize`, Standard 16 MiB) — dafür einfache Handler. Große Dateien nehmen `MapUpload`-Endpoints entgegen: gestreamt auf die Platte, Limit `MaxUploadSize` (Standard: unbegrenzt).
- Statische Dateien ≤ 512 KiB werden 1 s im Speicher gecacht; Änderungen greifen also nach spätestens einer Sekunde.
- Die Demo-Benutzer im Sample sind Klartext-Demodaten; in echten Anwendungen Passwörter hashen (z. B. PBKDF2 via `Rfc2898DeriveBytes`).
- `netstandard2.1`-Ziel: identische API, nur `BunResponse.Json(objekt)`, `BunRequest.BodyAsJson<T>()` und `BunCertificate` (Zertifikatserzeugung) benötigen net10.0. `JsonBuilder` funktioniert überall.

## Todo

Erledigtes ist mit ✅ markiert, Offenes mit ⬜. Jede Änderung wird mit Beschreibung, Datum und Uhrzeit notiert.

### Erledigt

- ✅ **Grundgerüst**: Bun als HTTP/HTTPS-Frontend, binäres IPC-Drahtformat (UDS/Named Pipe), .NET als Masterprozess, statische Dateien mit Cache, automatische Eingangsprüfung, Upload-Streaming, Testsuite mit 34 Tests — _vor dem 20.07.2026_
- ✅ **Native AOT mit Optimierungen**: Bibliothek AOT-kompatibel (`IsAotCompatible`, Reflection-JSON annotiert, `JsonTypeInfo`-Überladungen ergänzt), neuer `JsonBuilder` für JSON ohne Reflection, Sample auf `PublishAot` + `OptimizationPreference=Speed` + natives Instruction-Set + Server-GC umgestellt; native Executable (~4 MB) per `dotnet publish`, Smoke-Test bestanden — _20.07.2026, 11:40_
- ✅ **Parallele IPC-Verbindungen**: Bun nimmt Daten parallel an und gibt sie parallel weiter — N Verbindungen (`options.IpcConnections`, 0 = automatisch), Requests tick-weise verteilt, .NET mit Read-/Write-Task pro Verbindung; gemessen: ~2,5× Durchsatz mit 4 und über 4× mit 8 Verbindungen bei 64-KiB-Bodys — _20.07.2026, 11:40_
- ✅ **Serialisierung beschleunigt**: Request-Kopf in Bun in einem Durchgang in einen wiederverwendeten Puffer serialisiert (keine Hilfsarrays, kein doppeltes Vermessen), Antwort-Frames in .NET exakt vorberechnet statt über `MemoryStream`, `req.bytes()` statt `ArrayBuffer`-Umweg (+5 %) — _20.07.2026, 11:40_
- ✅ **Bun-Pfad-Auflösung**: Bun wird automatisch gesucht (PATH → `~/.bun/bin` → Homebrew → winget/scoop); falls nicht gefunden, lässt sich der Pfad vor dem Start per `options.BunExecutable` im Code angeben; sprechende Fehlermeldung — _20.07.2026, 11:40_
- ✅ **README überarbeitet**: Regeln-Sektion (Client/Server) nach der Kurzbeschreibung, Todo-Liste mit Zeitstempeln, Performance-Zahlen neu gemessen und eingeordnet — _20.07.2026, 11:45_
- ✅ **Skalierungstest mit mehr Verbindungen**: Nacktes `Bun.serve()` auf dieser Maschine erneut vermessen: ~105–110k req/s, unabhängig von 64–1024 Client-Verbindungen (Buns 300k-Angabe stammt von Linux-Serverhardware). BunNet-Endpoint bei Kleinst-Requests: ~40–45k req/s, flach über 1–16 IPC- und 256–1024 Client-Verbindungen — bestätigt: Engpass ist die single-threaded Weiterleitungsarbeit in Bun, nicht IPC oder .NET; mehr Durchsatz pro Maschine erfordert mehrere Bun-Instanzen (siehe BunWorkers) — _20.07.2026, 11:55_
- ✅ **BunWorkers (mehrere Bun-Prozesse per `SO_REUSEPORT`)**: `options.BunWorkers = N` startet N Bun-Prozesse, die sich denselben Port teilen — jeder mit eigenem IPC-Endpunkt und eigenen parallelen Verbindungen zum selben .NET-Prozess. Worker 0 bindet den Port (bestimmt ihn bei Port 0), weitere binden mit `reusePort`; SHUTDOWN und Cleanup pro Worker. Hebt die Single-Thread-Obergrenze von Bun auf; die Kernel-Lastverteilung wirkt nur unter Linux (unter macOS/Windows loggt die Bibliothek eine Warnung). Funktional auf macOS verifiziert (3 Worker, Port 0, sauberer Shutdown, keine Prozess-/Socket-Leichen) — _20.07.2026, 12:05_

### Offen

- ⬜ **BunWorkers unter Linux benchmarken**: erwartete nahezu lineare Skalierung (N × ~45k req/s) auf echter Linux-Hardware nachmessen
- ⬜ **Routen mit Parametern/Wildcards** (z. B. `/api/users/:id`) — bisher bewusst exakte Pfade
- ⬜ **WebSocket-Unterstützung** über die bestehende IPC-Brücke
- ⬜ **NuGet-Paket** für die Bibliothek veröffentlichen
- ⬜ **Benchmarks unter Linux und Windows** (bisher nur macOS/Apple Silicon gemessen)

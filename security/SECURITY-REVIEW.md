# Sicherheits-Review BunNet

Statisches Code-Review **plus** aktiver Angriffs-Test gegen einen laufenden Server.
Der aktive Test (`security/BunNet.SecurityTest`) fährt reale Angriffe über rohe
TCP-Sockets; das Ergebnis `0 PROBLEM` bedeutet, dass alle abgewehrt wurden.

Reproduzieren:

```bash
dotnet run -c Release --project security/BunNet.SecurityTest
```

## Zusammenfassung

Die Bibliothek ist **robust gebaut**. Von den geprüften Angriffsklassen war keine
erfolgreich. Ein Befund betraf die **Verfügbarkeit** (ein Handler konnte den
Bun-Worker zum Absturz bringen) — er wurde in diesem Review **behoben**.

| # | Prüfung | Ergebnis |
| - | ------- | -------- |
| 1 | Path-Traversal (`../`, `%2f`, `%5c`, `%2e%2e`, `/Web/../..`) | **abgewehrt** |
| 2 | Null-Byte-Injection im Pfad (`%00`) | **abgewehrt** |
| 3 | Response-Splitting / Header-Injection (CR/LF im Header) | **abgewehrt** |
| 4 | Interner Datei-Header (`x-bunnet-body-file`) von außen | **abgewehrt** |
| 5 | Body über `MaxRequestBodySize` → 413 | **greift** |
| 6 | JSON-Eingangsprüfung (null/leer) → 400 | **greift** |
| 7 | Command-Injection beim Bun-Start | **nicht möglich** |
| 8 | DoS: Worker-Crash durch ungültigen Response-Header | **gefunden → behoben** |

## Behobener Befund: Worker-Crash durch ungültigen Response-Header (DoS)

**Schweregrad:** mittel (Verfügbarkeit) — kein Datenabfluss, keine Rechteausweitung.

**Ursache:** Setzt ein C#-Handler einen Header-Wert mit CR/LF (z. B. weil er
ungeprüfte Benutzereingaben in einen Header spiegelt) oder einen Statuscode
außerhalb 200–599, wirft Buns `new Response(...)` in `bridge.js` eine Exception.
Diese lief bis in den `socket`-`data`-Handler durch und beendete über den
`IPC-Fehler`-Pfad den **gesamten Bun-Worker** (`process.exit(1)`). Eine einzige
präparierte Anfrage konnte so den Server (bzw. einen Worker) abschießen.

**Wichtig:** Der eigentliche *Injection*-Angriff war nie erfolgreich — Bun lehnt
den ungültigen Header ab, er erreicht den Client nicht. Das Problem war
ausschließlich, dass die Abwehr den Worker mitriss (Denial of Service).

**Fix** (`src/BunNet/Assets/bridge.js`, Funktion `handleResponse`): Der
`new Response(...)`-Aufbau ist jetzt in `try/catch` gekapselt. Eine ungültige
Antwort wird protokolliert und dem Client als sauberes **500** ausgeliefert —
der Worker läuft weiter. Ein Regressionstest in `tests/BunNet.Tests`
(„Ungültiger Response-Header wird zu 500 statt Worker-Crash" +
„Server bleibt nach ungültigem Header erreichbar") sichert das ab.

## Was bereits gut gelöst ist

- **Path-Traversal** (`bridge.js`, `serveStatic`): `decodeURIComponent` in `try`,
  Null-Byte-Check, `normalize()`, führende Separatoren entfernt, danach jede
  verbleibende `..`-Komponente abgelehnt — sowohl `/` als auch `\` abgedeckt.
- **Trennung intern/extern:** `bridge.js` verwirft alle von außen kommenden
  `x-bunnet-*`-Header, bevor der Request an .NET geht. Nur so kann kein Client
  über `x-bunnet-body-file` einen beliebigen Dateipfad als „Upload" unterschieben.
- **Kein Shell-Aufruf:** Bun wird mit `UseShellExecute = false` und getrennten
  Argumenten gestartet (`BunProcessHost`) — Command-Injection über Pfade ist
  ausgeschlossen. `BunLocator` übernimmt einen explizit gesetzten Pfad nur, wenn
  die Datei existiert.
- **Interne Fehler bleiben intern:** Handler-Exceptions werden serverseitig
  geloggt, der Client erhält ein nacktes `500` ohne Details (`DispatchAsync`).
- **Frame-Schutz:** `FrameReader` begrenzt Frames auf 512 MiB
  (`Protocol.MaxFrameSize`); Body-/String-Längen werden `checked` gelesen.
- **Beispiel-Auth** (`samples/BunNet.Sample/Auth.cs`): Session-Tokens aus
  `RandomNumberGenerator` (CSPRNG, 32 Byte), Passwortvergleich mit
  `CryptographicOperations.FixedTimeEquals` (konstante Zeit).
- **Upload-Ziel** (`api-post-upload.cs`): `Path.GetFileName` entfernt Pfadanteile
  aus dem Ziel-Dateinamen — kein Traversal beim Speichern.

## Empfehlungen (Härtung, offen)

Nicht als akute Lücken, sondern als Defense-in-depth:

1. **IPC-Socket-Rechte:** Der Unix Domain Socket liegt in `Path.GetTempPath()`
   (oft `/tmp`). Unter der üblichen `umask 022` kann sich kein anderer lokaler
   Nutzer verbinden (connect braucht Schreibrecht). Ist die `umask` jedoch
   permissiv (z. B. `000`), könnte ein lokaler Nutzer direkt Frames einspeisen und
   die `x-bunnet-*`-Filterung von `bridge.js` umgehen. Empfehlung: den Socket
   explizit auf `0700` beschränken bzw. in ein nutzer-privates Verzeichnis legen
   (`IpcListener`). Betrifft nur Mehrbenutzer-Maschinen.
2. **`x-bunnet-body-file` auch in .NET absichern:** Als zweite Verteidigungslinie
   könnte `Protocol.ParseRequest` prüfen, dass ein `x-bunnet-body-file` nur
   innerhalb des von der Bibliothek erzeugten Upload-Verzeichnisses liegt — falls
   die IPC-Grenze doch einmal überwunden würde.
3. **Anwendungsseitig:** Benutzereingaben nie ungefiltert in Response-Header
   schreiben. Dank des Fixes ist das kein DoS mehr, bleibt aber unsauber.

## Threat Model (Einordnung)

BunNet terminiert HTTP/HTTPS in Bun und spricht mit .NET über **lokale** IPC
(UDS / Named Pipe). Die IPC-Grenze wird als vertrauenswürdig angenommen
(gleiche Maschine, gleicher Nutzer). Gegen **remote** Angreifer über das Netz
sind die relevanten Klassen (Traversal, Injection, Overflow, DoS) abgedeckt.

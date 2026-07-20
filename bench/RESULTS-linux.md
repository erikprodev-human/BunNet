# Performance-Messung (Linux)

Ergebnisse des Durchsatz-Benchmarks (`bench/BunNet.Bench`) auf einer Linux-Maschine.
Er beantwortet zwei Fragen: **Wie hoch ist der Durchsatz** und **skalieren die
parallelen Bun-Prozesse** (`BunWorkers` via `SO_REUSEPORT`)?

## Testumgebung

| | |
| --- | --- |
| Plattform | Ubuntu 24.04.4 LTS |
| Kernel | Linux 6.18.5 x86_64 |
| CPU-Kerne | 4 |
| RAM | 15 GiB |
| .NET | 10.0.302 (Release, Server-GC) |
| Bun | 1.3.11 |
| Endpoint | `POST /api/ping` → `{"pong":true}` (fester Antwort-Puffer, keine Reflection) |
| Last | 256 parallele Keep-Alive-TCP-Verbindungen, seriell pro Verbindung |
| Messung | 2 s Aufwärmen + 6 s Messung pro Kombination |

> Hinweis: Lastgenerator, .NET-Server und alle Bun-Worker laufen auf **derselben**
> 4-Kern-Maschine und teilen sich die CPU. Auf getrennter Client-Hardware liegt der
> reine Server-Durchsatz entsprechend höher.

## Ergebnisse

| BunWorkers | IpcConnections | req/s | Faktor vs. 1 Worker |
| ---------: | -------------: | ----------: | :-----------------: |
| 1 | 1 | ~25.900 | 1,00× |
| 1 | 2 | ~27.100 | 1,05× |
| 1 | 4 | ~26.600 | 1,03× |
| 2 | 2 | ~52.400 | **2,02×** |
| 3 | 2 | ~57.100 | 2,20× |
| 4 | 2 | ~62.600 | **2,42×** |
| 6 | 2 | ~65.500 | 2,53× |
| 8 | 2 | ~64.100 | 2,47× |
| auto (2) | auto (2) | ~51.500 | 1,99× |

Statische Dateien (Bun liefert direkt aus, ohne IPC): **~60.000 req/s** bereits mit
einem einzigen Worker. Serielle Latenz eines .NET-Endpoints: **~160–200 µs/Request**.

## Auswertung

- **Parallele Bun-Prozesse funktionieren unter Linux** und skalieren nahezu linear:
  Der Sprung von 1 auf 2 Worker **verdoppelt** den Durchsatz exakt (26k → 52k).
  `SO_REUSEPORT` verteilt die Verbindungen sauber über den Kernel auf die Worker.
- **Ein einzelner Bun-Worker deckelt bei ~26k req/s** — das ist die Single-Thread-
  Obergrenze von Bun, die die Worker gezielt aufheben.
- **Das Maximum liegt bei ~63–66k req/s** (4–6 Worker). Ab hier ist die 4-Kern-Maschine
  gesättigt: Der .NET-Server und der Lastgenerator brauchen ebenfalls CPU, und
  jenseits von 6 Workern kostet Context-Switching mehr, als er bringt (8 Worker < 6).
- **`IpcConnections` über 2 bringt nichts** — pro Worker genügen wenige IPC-Kanäle;
  der Hebel für mehr Durchsatz ist die **Worker-Zahl**, nicht die Kanal-Zahl.
- Der **Automatik-Wert** (`BunWorkers = 0` → Kerne/2 = 2 Worker) liefert ~52k req/s.
  Er ist bewusst konservativ und lässt Kerne für die .NET-Seite frei. Wer die Maschine
  ausschließlich für den Server nutzt, holt mit `BunWorkers = Kernanzahl` das Maximum.

## Empfehlung für maximalen Durchsatz

```csharp
BunServerOptions options = new BunServerOptions();
options.BunWorkers = Environment.ProcessorCount; // alle Kerne für Bun-Worker
options.IpcConnections = 2;                       // 2 IPC-Kanäle pro Worker genügen
```

## Reproduzieren

```bash
dotnet run -c Release --project bench/BunNet.Bench
```

Die Kombinationsmatrix steht oben in `bench/BunNet.Bench/Program.cs` und lässt sich
dort anpassen.

// ---------------------------------------------------------------------------
// BunNet bridge.js
//
// Läuft in Bun und ist die Brücke zwischen Browser und .NET-Masterprozess:
//   * Bun.serve() nimmt HTTP/HTTPS-Requests entgegen.
//   * In C# registrierte Endpoints werden über einen lokalen IPC-Kanal
//     (Unix Domain Socket bzw. Named Pipe) an .NET weitergereicht.
//   * Alle übrigen GET/HEAD-Requests werden optional als statische Dateien
//     aus config.staticRoot beantwortet (mit kurzem In-Memory-Cache).
//
// Aufruf (durch die .NET-Bibliothek):  bun bridge.js <pfad-zur-config.json>
//
// Drahtformat (alle Zahlen Little-Endian, Strings = uint32-Länge + UTF-8):
//   Frame     : uint32 payloadLength | payload
//   Payload   : uint8 type | ...
//   REQUEST(1): uint32 id | str method | str path | str query | str remoteAddr
//               | uint16 headerCount | (str name, str value)* | uint32 bodyLen | body
//   RESPONSE(2): uint32 id | uint16 status | uint16 headerCount
//               | (str name, str value)* | uint32 bodyLen | body
//   READY(3)  : uint32 port          (Bun -> .NET, nach erfolgreichem Start)
//   SHUTDOWN(4): leer                (.NET -> Bun, sauberes Herunterfahren)
// ---------------------------------------------------------------------------

import net from "node:net";
import { unlink } from "node:fs/promises";
import { join, normalize } from "node:path";

const TYPE_REQUEST = 1;
const TYPE_RESPONSE = 2;
const TYPE_READY = 3;
const TYPE_SHUTDOWN = 4;

const config = await Bun.file(process.argv[2]).json();
const routes = new Set(config.routes.map((r) => r.method + " " + r.path));
// Upload-Routen: Body wird auf die Platte gestreamt statt in den Speicher.
const uploadRoutes = new Set(config.routes.filter((r) => r.upload).map((r) => r.method + " " + r.path));

// ---------------------------------------------------------------------------
// IPC-Verbindungen zum .NET-Masterprozess
//
// Es werden MEHRERE parallele Verbindungen aufgebaut (config.ipcConnections).
// Requests werden round-robin darauf verteilt; .NET liest, verarbeitet und
// beantwortet jede Verbindung mit eigenen Tasks — Annahme und Weitergabe
// laufen dadurch auf beiden Seiten parallel.
// ---------------------------------------------------------------------------

// Offene Requests: id -> { resolve, expires } (global über alle Verbindungen)
const pending = new Map();
let nextId = 1;
let shuttingDown = false;

const ipcConnectionCount = Math.max(1, config.ipcConnections | 0);
const channels = [];

function createChannel() {
  const sock = net.connect(config.ipc);
  sock.setNoDelay?.(true);
  const channel = { sock: sock, rxBuf: Buffer.alloc(0), txQueue: [], txScheduled: false };

  sock.on("error", (err) => {
    console.error(`[bunnet] IPC-Fehler: ${err.message}`);
    process.exit(1);
  });

  // Wenn der Masterprozess eine Verbindung schließt (oder stirbt), beendet
  // sich Bun selbst — es bleiben nie verwaiste Bun-Prozesse zurück.
  sock.on("close", () => {
    if (!shuttingDown) console.error("[bunnet] IPC-Verbindung geschlossen, beende Bun.");
    process.exit(shuttingDown ? 0 : 1);
  });

  // Eingehende Bytes zu Frames zusammensetzen.
  sock.on("data", (chunk) => {
    channel.rxBuf = channel.rxBuf.length === 0 ? chunk : Buffer.concat([channel.rxBuf, chunk]);
    while (channel.rxBuf.length >= 4) {
      const len = channel.rxBuf.readUInt32LE(0);
      if (channel.rxBuf.length < 4 + len) break;
      const payload = channel.rxBuf.subarray(4, 4 + len);
      channel.rxBuf = channel.rxBuf.subarray(4 + len);
      handleFrame(payload);
    }
  });

  return channel;
}

for (let i = 0; i < ipcConnectionCount; i++) channels.push(createChannel());

// Warten, bis alle Verbindungen stehen, bevor der HTTP-Server startet.
await Promise.all(channels.map(
  (channel) => new Promise((resolve) => channel.sock.on("connect", resolve))
));

// Statt eines Timers pro Request räumt EIN Intervall abgelaufene Requests ab —
// das spart bei hoher Last hunderttausende setTimeout-Aufrufe.
setInterval(() => {
  if (pending.size === 0) return;
  const now = Date.now();
  for (const [id, entry] of pending) {
    if (entry.expires <= now) {
      pending.delete(id);
      entry.resolve(new Response("Gateway Timeout", { status: 504 }));
    }
  }
}, 1000);

function handleFrame(p) {
  const type = p[0];
  if (type === TYPE_RESPONSE) {
    handleResponse(p);
  } else if (type === TYPE_SHUTDOWN) {
    shuttingDown = true;
    server.stop(true);
    for (const channel of channels) channel.sock.end();
  }
}

function handleResponse(p) {
  let off = 1;
  const id = p.readUInt32LE(off); off += 4;
  const status = p.readUInt16LE(off); off += 2;
  const headerCount = p.readUInt16LE(off); off += 2;

  // Einfaches Objekt statt Headers-Instanz — schneller, Response akzeptiert beides.
  const headers = {};
  for (let i = 0; i < headerCount; i++) {
    const nameLen = p.readUInt32LE(off); off += 4;
    const name = p.toString("utf8", off, off + nameLen); off += nameLen;
    const valueLen = p.readUInt32LE(off); off += 4;
    const value = p.toString("utf8", off, off + valueLen); off += valueLen;
    headers[name] = name in headers ? headers[name] + ", " + value : value;
  }

  const bodyLen = p.readUInt32LE(off); off += 4;
  // Kopie erstellen: p ist eine Sicht auf den (wiederverwendeten) Empfangspuffer.
  const body = Uint8Array.prototype.slice.call(p, off, off + bodyLen);

  const entry = pending.get(id);
  if (entry) {
    pending.delete(id);
    const canHaveBody = status !== 204 && status !== 304;
    entry.resolve(new Response(canHaveBody ? body : null, { status, headers }));
  }
}

// ---------------------------------------------------------------------------
// Ausgehende Frames mikro-bündeln und auf die Verbindungen verteilen: Alle
// Frames desselben Event-Loop-Ticks gehen gebündelt mit EINEM sock.write()
// über DIESELBE Verbindung (weniger Syscalls); erst der nächste Tick wechselt
// zur nächsten Verbindung. So bleibt die Bündelung erhalten, und .NET liest,
// verarbeitet und beantwortet die Ticks trotzdem parallel.
// ---------------------------------------------------------------------------

let rrIndex = 0;

function sendFrame(frame) {
  const channel = channels[rrIndex];
  channel.txQueue.push(frame);
  if (!channel.txScheduled) {
    channel.txScheduled = true;
    queueMicrotask(() => flushTx(channel));
  }
}

function flushTx(channel) {
  channel.txScheduled = false;
  if (channel.txQueue.length === 1) {
    channel.sock.write(channel.txQueue[0]);
  } else {
    channel.sock.write(Buffer.concat(channel.txQueue));
  }
  channel.txQueue = [];
  rrIndex = rrIndex + 1 >= channels.length ? 0 : rrIndex + 1;
}

// ---------------------------------------------------------------------------
// Request an .NET weiterleiten
// ---------------------------------------------------------------------------

const EMPTY_BODY = new Uint8Array(0);

// Wiederverwendeter Serialisierungspuffer: Der Request-Kopf (Methode, Pfad,
// Header, …) wird in EINEM Durchgang hineingeschrieben — ohne Hilfsarrays und
// ohne die Strings doppelt zu vermessen (write() liefert die Byte-Länge gleich
// mit). Der Puffer wächst bei Bedarf und bleibt dann auf seiner Hochwassermarke.
let scratch = Buffer.allocUnsafe(64 * 1024);
let scratchOff = 0;

function scratchEnsure(extra) {
  if (scratchOff + extra <= scratch.length) return;
  const bigger = Buffer.allocUnsafe(Math.max(scratch.length * 2, scratchOff + extra));
  scratch.copy(bigger, 0, 0, scratchOff);
  scratch = bigger;
}

// Schreibt einen längenpräfixierten UTF-8-String in den Scratch-Puffer.
function scratchWriteString(text) {
  scratchEnsure(4 + text.length * 3); // 3 Bytes/UTF-16-Einheit = sichere Obergrenze
  const byteLength = scratch.write(text, scratchOff + 4, "utf8");
  scratch.writeUInt32LE(byteLength, scratchOff);
  scratchOff += 4 + byteLength;
}

// Liest den Body in den Speicher und erzwingt maxRequestBodySize von Hand
// (das Bun.serve-Limit steht wegen der Upload-Routen ggf. höher).
// Liefert null, wenn der Body zu groß ist.
async function readBody(req) {
  if (req.body === null) return EMPTY_BODY;
  const limit = config.maxRequestBodySize;
  const declared = Number(req.headers.get("content-length") ?? -1);
  if (declared > limit) return null;
  if (declared >= 0) return await req.bytes(); // schnellster Weg (Bun-nativ, ohne Umwege)

  // Ohne Content-Length (chunked): stückweise lesen und dabei das Limit prüfen.
  const chunks = [];
  let total = 0;
  for await (const chunk of req.body) {
    total += chunk.byteLength;
    if (total > limit) return null;
    chunks.push(chunk);
  }
  return chunks.length === 1 ? chunks[0] : Buffer.concat(chunks, total);
}

// Streamt den Body einer Upload-Route in eine Temp-Datei — konstanter
// Speicherverbrauch, egal wie groß die Datei ist. Liefert { path, size }
// oder null, wenn maxUploadSize (0 = unbegrenzt) überschritten wird.
let uploadSeq = 0;
async function readBodyToFile(req) {
  const limit = config.maxUploadSize;
  const declared = Number(req.headers.get("content-length") ?? -1);
  if (limit > 0 && declared > limit) return null;

  const path = join(config.uploadDir, "upload-" + Date.now() + "-" + ++uploadSeq + ".tmp");
  const writer = Bun.file(path).writer();
  let total = 0;
  if (req.body !== null) {
    for await (const chunk of req.body) {
      total += chunk.byteLength;
      if (limit > 0 && total > limit) {
        await writer.end();
        await unlink(path).catch(() => {});
        return null;
      }
      writer.write(chunk);
      await writer.flush(); // direkt auf die Platte — nichts sammelt sich im RAM
    }
  }
  await writer.end();
  return { path: path, size: total };
}

async function forward(req, pathname, query, srv, isUpload) {
  const id = nextId;
  nextId = nextId >= 0xffffffff ? 1 : nextId + 1;

  let body = EMPTY_BODY;
  let bodyFile = null;
  if (isUpload) {
    bodyFile = await readBodyToFile(req);
    if (bodyFile === null) return new Response("Payload Too Large", { status: 413 });
  } else {
    body = await readBody(req);
    if (body === null) return new Response("Payload Too Large", { status: 413 });
  }

  const ip = srv.requestIP(req);
  const remote = ip === null ? "" : ip.address;

  // Request-Kopf in EINEM Durchgang in den Scratch-Puffer serialisieren
  // (ab hier synchron — kein await, der Puffer gehört exklusiv diesem Request).
  scratchOff = 0;
  scratchEnsure(9);
  scratch[0] = TYPE_REQUEST;
  scratch.writeUInt32LE(id, 1);
  scratchOff = 5;
  scratchWriteString(req.method);
  scratchWriteString(pathname);
  scratchWriteString(query);
  scratchWriteString(remote);

  // Header-Anzahl ist erst nach der Schleife bekannt — Platzhalter merken.
  scratchEnsure(2);
  const headerCountOff = scratchOff;
  scratchOff += 2;
  let headerCount = 0;
  for (const [name, value] of req.headers) {
    // x-bunnet-* ist für die interne Übergabe reserviert — von außen kommende
    // Header dieses Namens werden verworfen (sonst könnte ein Client .NET
    // einen beliebigen Dateipfad als "Upload" unterschieben).
    if (name.startsWith("x-bunnet-")) continue;
    scratchWriteString(name);
    scratchWriteString(value);
    headerCount++;
  }
  if (bodyFile !== null) {
    scratchWriteString("x-bunnet-body-file");
    scratchWriteString(bodyFile.path);
    scratchWriteString("x-bunnet-body-size");
    scratchWriteString(String(bodyFile.size));
    headerCount += 2;
  }
  scratch.writeUInt16LE(headerCount, headerCountOff);

  // Frame exakt passend anlegen: Längenpräfix + Kopf (aus dem Scratch) + Body.
  const payloadSize = scratchOff + 4 + body.byteLength;
  const frame = Buffer.allocUnsafe(4 + payloadSize);
  frame.writeUInt32LE(payloadSize, 0);
  scratch.copy(frame, 4, 0, scratchOff);
  let off = 4 + scratchOff;
  frame.writeUInt32LE(body.byteLength, off); off += 4;
  frame.set(body, off);

  return new Promise((resolve) => {
    pending.set(id, { resolve: resolve, expires: Date.now() + config.requestTimeoutMs });
    sendFrame(frame);
  });
}

// ---------------------------------------------------------------------------
// Statische Dateien (nur GET/HEAD, nur unterhalb von staticRoot)
// ---------------------------------------------------------------------------

// Kleine Dateien werden kurz im Speicher gehalten: innerhalb des TTL entfallen
// sämtliche Dateisystemzugriffe. Nach Ablauf wird neu von der Platte gelesen,
// Änderungen an Dateien greifen also nach spätestens einer Sekunde.
const STATIC_CACHE_TTL_MS = 1000;
const STATIC_CACHE_MAX_FILE = 512 * 1024;
const STATIC_CACHE_MAX_ENTRIES = 1000;
const staticCache = new Map(); // pathname -> { data|null, type, expires }

async function serveStatic(pathname) {
  const now = Date.now();
  const cached = staticCache.get(pathname);
  if (cached !== undefined && cached.expires > now) {
    if (cached.data === null) return null;
    return new Response(cached.data, { headers: { "Content-Type": cached.type } });
  }

  let rel;
  try {
    rel = decodeURIComponent(pathname);
  } catch {
    return null;
  }
  if (rel.includes("\0")) return null;

  // Pfad normalisieren und führende Separatoren entfernen; danach darf keine
  // ".."-Komponente mehr übrig sein — sonst Zugriff außerhalb der Root.
  rel = normalize(rel).replace(/^[/\\]+/, "");
  if (rel === "" || rel === ".") rel = "index.html";
  if (rel.split(/[/\\]/).includes("..")) return null;

  let file = Bun.file(join(config.staticRoot, rel));
  if (!(await file.exists())) {
    file = Bun.file(join(config.staticRoot, rel, "index.html"));
    if (!(await file.exists())) {
      cachePut(pathname, null, "", now);
      return null;
    }
  }

  // Große Dateien streamen statt cachen.
  if (file.size > STATIC_CACHE_MAX_FILE) return new Response(file);

  const data = new Uint8Array(await file.arrayBuffer());
  cachePut(pathname, data, file.type, now);
  return new Response(data, { headers: { "Content-Type": file.type } });
}

function cachePut(pathname, data, type, now) {
  if (staticCache.size >= STATIC_CACHE_MAX_ENTRIES) staticCache.clear();
  staticCache.set(pathname, { data: data, type: type, expires: now + STATIC_CACHE_TTL_MS });
}

// ---------------------------------------------------------------------------
// HTTP/HTTPS-Server
// ---------------------------------------------------------------------------

// Bun.serve kennt nur EIN Body-Limit für den ganzen Server. Gibt es Upload-
// Routen, wird es entsprechend angehoben; maxRequestBodySize für normale
// Routen erzwingt readBody() dann von Hand.
const serveBodyLimit = uploadRoutes.size === 0
  ? config.maxRequestBodySize
  : (config.maxUploadSize > 0
      ? Math.max(config.maxRequestBodySize, config.maxUploadSize)
      : Number.MAX_SAFE_INTEGER);

const server = Bun.serve({
  port: config.port,
  hostname: config.hostname,
  // Mehrere Worker-Prozesse teilen sich per SO_REUSEPORT denselben Port
  // (Lastverteilung durch den Kernel — nur unter Linux wirksam).
  reusePort: config.reusePort === true,
  maxRequestBodySize: serveBodyLimit,
  development: false,
  tls: config.tls
    ? {
        cert: Bun.file(config.tls.certPath),
        key: Bun.file(config.tls.keyPath),
        passphrase: config.tls.passphrase ?? undefined,
      }
    : undefined,

  async fetch(req, srv) {
    // Pfad und Query von Hand trennen — spart den teuren URL-Parser.
    const url = req.url;
    const pathStart = url.indexOf("/", url.indexOf("//") + 2);
    const queryStart = pathStart < 0 ? -1 : url.indexOf("?", pathStart);
    const pathname = pathStart < 0 ? "/" : (queryStart < 0 ? url.substring(pathStart) : url.substring(pathStart, queryStart));
    const query = queryStart < 0 ? "" : url.substring(queryStart + 1);

    const routeKey = req.method + " " + pathname;
    if (routes.has(routeKey)) {
      return forward(req, pathname, query, srv, uploadRoutes.has(routeKey));
    }

    if (config.staticRoot && (req.method === "GET" || req.method === "HEAD")) {
      const res = await serveStatic(pathname);
      if (res) return res;
    }

    return new Response("Not Found", { status: 404 });
  },

  error(err) {
    console.error(`[bunnet] Serverfehler: ${err.message}`);
    return new Response("Internal Server Error", { status: 500 });
  },
});

// .NET auf JEDER Verbindung mitteilen, dass der Server läuft (inkl.
// tatsächlichem Port, falls 0 konfiguriert war) — .NET wartet beim Start
// auf das READY jeder einzelnen Verbindung.
const ready = Buffer.allocUnsafe(9);
ready.writeUInt32LE(5, 0);
ready[4] = TYPE_READY;
ready.writeUInt32LE(server.port, 5);
for (const channel of channels) channel.sock.write(ready);

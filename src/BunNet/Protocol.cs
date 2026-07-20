using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BunNet
{
    /// <summary>
    /// Binäres Drahtformat zwischen Bun und .NET (Gegenstück zu bridge.js).
    /// Frame: uint32 Payload-Länge (Little-Endian) + Payload. Strings sind
    /// uint32-längenpräfixiertes UTF-8.
    /// </summary>
    internal static class Protocol
    {
        public const byte TypeRequest = 1;
        public const byte TypeResponse = 2;
        public const byte TypeReady = 3;
        public const byte TypeShutdown = 4;

        /// <summary>Maximal akzeptierte Frame-Größe als Schutz vor defekten Längenangaben.</summary>
        public const int MaxFrameSize = 512 * 1024 * 1024;

        /// <summary>Parst einen REQUEST-Payload. Die Request-ID kommt über den out-Parameter.</summary>
        public static BunRequest ParseRequest(byte[] payload, out uint requestId)
        {
            int offset = 1; // Typ-Byte überspringen
            requestId = ReadUInt32(payload, ref offset);

            string method = ReadString(payload, ref offset);
            string path = ReadString(payload, ref offset);
            string query = ReadString(payload, ref offset);
            string remoteAddress = ReadString(payload, ref offset);

            int headerCount = ReadUInt16(payload, ref offset);
            Dictionary<string, string> headers = new Dictionary<string, string>(headerCount, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headerCount; i++)
            {
                string name = ReadString(payload, ref offset);
                string value = ReadString(payload, ref offset);
                // Mehrfach-Header werden HTTP-konform mit ", " zusammengeführt.
                string? existing;
                if (headers.TryGetValue(name, out existing)) headers[name] = existing + ", " + value;
                else headers[name] = value;
            }

            int bodyLength = checked((int)ReadUInt32(payload, ref offset));
            byte[] body = new byte[bodyLength];
            Buffer.BlockCopy(payload, offset, body, 0, bodyLength);

            // Interne Übergabe-Header von Upload-Routen: Bun hat den Body auf die
            // Platte gestreamt und schickt statt der Daten nur Pfad und Größe.
            string bodyFilePath = "";
            long bodyFileSize = 0;
            string? headerValue;
            if (headers.TryGetValue("x-bunnet-body-file", out headerValue))
            {
                bodyFilePath = headerValue;
                headers.Remove("x-bunnet-body-file");
            }
            if (headers.TryGetValue("x-bunnet-body-size", out headerValue))
            {
                long.TryParse(headerValue, out bodyFileSize);
                headers.Remove("x-bunnet-body-size");
            }

            return new BunRequest(method, path, query, remoteAddress, headers, body, bodyFilePath, bodyFileSize);
        }

        /// <summary>Parst einen READY-Payload und liefert den tatsächlich gebundenen Port.</summary>
        public static int ParseReady(byte[] payload)
        {
            int offset = 1;
            return checked((int)ReadUInt32(payload, ref offset));
        }

        /// <summary>
        /// Serialisiert einen kompletten RESPONSE-Frame (inkl. Längenpräfix).
        /// Die Größe wird vorab exakt berechnet, sodass genau EIN Puffer ohne
        /// Zwischenkopien entsteht (schneller als ein MemoryStream).
        /// </summary>
        public static byte[] BuildResponseFrame(uint id, BunResponse response)
        {
            int headerBytes = 0;
            foreach (KeyValuePair<string, string> header in response.Headers)
                headerBytes += 8 + Encoding.UTF8.GetByteCount(header.Key) + Encoding.UTF8.GetByteCount(header.Value);

            int payloadSize =
                1 + 4 +                       // Typ + Request-ID
                2 + 2 + headerBytes +         // Status + Header-Anzahl + Header
                4 + response.Body.Length;     // Body

            byte[] frame = new byte[4 + payloadSize];
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0, 4), (uint)payloadSize);
            frame[4] = TypeResponse;
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(5, 4), id);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(9, 2), checked((ushort)response.Status));
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(11, 2), checked((ushort)response.Headers.Count));

            int offset = 13;
            foreach (KeyValuePair<string, string> header in response.Headers)
            {
                offset = WriteStringInto(frame, offset, header.Key);
                offset = WriteStringInto(frame, offset, header.Value);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset, 4), (uint)response.Body.Length);
            offset += 4;
            Buffer.BlockCopy(response.Body, 0, frame, offset, response.Body.Length);
            return frame;
        }

        /// <summary>Schreibt einen längenpräfixierten UTF-8-String und liefert den neuen Offset.</summary>
        private static int WriteStringInto(byte[] buffer, int offset, string value)
        {
            int byteLength = Encoding.UTF8.GetBytes(value, 0, value.Length, buffer, offset + 4);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), (uint)byteLength);
            return offset + 4 + byteLength;
        }

        /// <summary>Serialisiert einen SHUTDOWN-Frame.</summary>
        public static byte[] BuildShutdownFrame()
        {
            return new byte[] { 1, 0, 0, 0, TypeShutdown };
        }

        // --- Lese-/Schreibhelfer -------------------------------------------

        private static uint ReadUInt32(byte[] buffer, ref int offset)
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4));
            offset += 4;
            return value;
        }

        private static ushort ReadUInt16(byte[] buffer, ref int offset)
        {
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset, 2));
            offset += 2;
            return value;
        }

        private static string ReadString(byte[] buffer, ref int offset)
        {
            int length = checked((int)ReadUInt32(buffer, ref offset));
            string value = Encoding.UTF8.GetString(buffer, offset, length);
            offset += length;
            return value;
        }

    }

    /// <summary>
    /// Liest Frames aus dem IPC-Stream. Ein interner Puffer bündelt viele kleine
    /// Frames pro Systemaufruf — wichtig für hohen Durchsatz. Es darf immer nur
    /// ein Leser gleichzeitig aktiv sein (der Read-Loop des Servers).
    /// </summary>
    internal sealed class FrameReader
    {
        private readonly Stream _stream;
        private readonly byte[] _buffer = new byte[256 * 1024];
        private int _start;   // erstes ungelesenes Byte im Puffer
        private int _end;     // Ende der gültigen Daten im Puffer

        public FrameReader(Stream stream)
        {
            _stream = stream;
        }

        /// <summary>
        /// Liefert den Payload des nächsten Frames oder <c>null</c> bei sauberem
        /// Verbindungsende (EOF an einer Frame-Grenze).
        /// </summary>
        public async Task<byte[]?> ReadFrameAsync(CancellationToken ct)
        {
            // Längenpräfix (4 Bytes) lesen.
            if (!await EnsureAsync(4, ct).ConfigureAwait(false)) return null;
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(_start, 4)));
            if (length > Protocol.MaxFrameSize)
                throw new InvalidDataException("IPC-Frame zu groß: " + length + " Bytes.");
            _start += 4;

            byte[] payload = new byte[length];

            // Was schon im Puffer liegt, direkt übernehmen …
            int buffered = Math.Min(length, _end - _start);
            Buffer.BlockCopy(_buffer, _start, payload, 0, buffered);
            _start += buffered;

            // … und den Rest (große Frames) direkt aus dem Stream lesen.
            int done = buffered;
            while (done < length)
            {
                int read = await _stream.ReadAsync(payload.AsMemory(done, length - done), ct).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("IPC-Verbindung wurde unerwartet beendet.");
                done += read;
            }

            return payload;
        }

        /// <summary>Sorgt dafür, dass mindestens <paramref name="count"/> Bytes im Puffer liegen.</summary>
        private async Task<bool> EnsureAsync(int count, CancellationToken ct)
        {
            while (_end - _start < count)
            {
                // Gelesenes nach vorn schieben, damit Platz zum Nachladen ist.
                if (_start > 0)
                {
                    Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                    _end -= _start;
                    _start = 0;
                }

                int read = await _stream.ReadAsync(_buffer.AsMemory(_end), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    if (_end == 0) return false; // sauberes Ende an einer Frame-Grenze
                    throw new EndOfStreamException("IPC-Verbindung wurde unerwartet beendet.");
                }
                _end += read;
            }
            return true;
        }
    }
}

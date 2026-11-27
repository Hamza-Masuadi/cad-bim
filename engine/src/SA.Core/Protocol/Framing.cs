using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SA.Core.Json;

namespace SA.Core.Protocol
{
    public sealed class FramingException : IOException
    {
        public FramingException(string message) : base(message) { }
    }

    /// <summary>
    /// Message framing: a 4-byte little-endian payload length, then that many bytes of UTF-8 JSON.
    /// Payloads must be 1 byte to <see cref="ProtocolInfo.MaxMessageBytes"/>. The length is checked before any
    /// payload buffer is allocated, so a hostile or corrupt header cannot make the reader allocate gigabytes.
    /// </summary>
    public static class Framing
    {
        public static void WriteMessage(Stream stream, byte[] payload)
        {
            CheckOutgoing(payload);
            var header = EncodeHeader(payload.Length);
            stream.Write(header, 0, 4);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        public static void WriteJson(Stream stream, JsonValue message) => WriteMessage(stream, message.ToUtf8Bytes());

        public static async Task WriteMessageAsync(Stream stream, byte[] payload, CancellationToken ct = default)
        {
            CheckOutgoing(payload);
            var header = EncodeHeader(payload.Length);
            await stream.WriteAsync(header, 0, 4, ct).ConfigureAwait(false);
            await stream.WriteAsync(payload, 0, payload.Length, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Reads one message. Returns null if the stream ends cleanly before the first header byte.
        /// Throws <see cref="FramingException"/> for a truncated frame, a zero length or a length over the cap.
        /// </summary>
        public static byte[]? ReadMessage(Stream stream)
        {
            var header = new byte[4];
            int got = ReadFully(stream, header, 0, 4);
            if (got == 0) return null;
            if (got < 4) throw new FramingException("Stream ended inside a frame header (" + got + " of 4 bytes).");
            int length = DecodeHeader(header);
            var payload = new byte[length];
            int read = ReadFully(stream, payload, 0, length);
            if (read < length) throw new FramingException("Stream ended inside a frame: expected " + length + " bytes, got " + read + ".");
            return payload;
        }

        public static JsonValue? ReadJson(Stream stream)
        {
            var bytes = ReadMessage(stream);
            return bytes is null ? null : JsonValue.Parse(bytes);
        }

        public static async Task<byte[]?> ReadMessageAsync(Stream stream, CancellationToken ct = default)
        {
            var header = new byte[4];
            int got = await ReadFullyAsync(stream, header, 0, 4, ct).ConfigureAwait(false);
            if (got == 0) return null;
            if (got < 4) throw new FramingException("Stream ended inside a frame header (" + got + " of 4 bytes).");
            int length = DecodeHeader(header);
            var payload = new byte[length];
            int read = await ReadFullyAsync(stream, payload, 0, length, ct).ConfigureAwait(false);
            if (read < length) throw new FramingException("Stream ended inside a frame: expected " + length + " bytes, got " + read + ".");
            return payload;
        }

        public static byte[] EncodeHeader(int length)
        {
            return new[] { (byte)length, (byte)(length >> 8), (byte)(length >> 16), (byte)(length >> 24) };
        }

        /// <summary>Decodes and checks a header. Lengths are unsigned on the wire; anything over the cap is rejected.</summary>
        public static int DecodeHeader(byte[] header)
        {
            uint length = header[0] | ((uint)header[1] << 8) | ((uint)header[2] << 16) | ((uint)header[3] << 24);
            if (length == 0) throw new FramingException("Zero-length frame.");
            if (length > ProtocolInfo.MaxMessageBytes)
                throw new FramingException("Frame of " + length + " bytes exceeds the " + ProtocolInfo.MaxMessageBytes + "-byte limit.");
            return (int)length;
        }

        private static void CheckOutgoing(byte[] payload)
        {
            if (payload is null) throw new ArgumentNullException(nameof(payload));
            if (payload.Length == 0) throw new FramingException("Refusing to send an empty frame.");
            if (payload.Length > ProtocolInfo.MaxMessageBytes)
                throw new FramingException("Message of " + payload.Length + " bytes exceeds the " + ProtocolInfo.MaxMessageBytes + "-byte limit.");
        }

        private static int ReadFully(Stream s, byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = s.Read(buffer, offset + total, count - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        private static async Task<int> ReadFullyAsync(Stream s, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int total = 0;
            while (total < count)
            {
                int n = await s.ReadAsync(buffer, offset + total, count - total, ct).ConfigureAwait(false);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }
    }
}

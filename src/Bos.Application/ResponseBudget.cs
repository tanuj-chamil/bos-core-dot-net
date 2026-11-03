using System.Text.Json;
using System.Text.Json.Nodes;
using Bos.Contracts;

namespace Bos.Application;

internal static class ResponseBudget
{
    public static void Check(JsonNode? result)
    {
        using var sink = new CountingStream();
        using var writer = new Utf8JsonWriter(sink);
        if (result is null) writer.WriteNullValue(); else result.WriteTo(writer);
        writer.Flush();
    }
    private sealed class CountingStream : Stream
    {
        private long length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => length;
        public override long Position { get => length; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if ((length += buffer.Length) > 2 * 1024 * 1024)
                throw new BosException("EXECUTION_LIMIT", "The bounded API response exceeds its transport allocation budget.");
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

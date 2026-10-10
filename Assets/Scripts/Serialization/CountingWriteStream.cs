using System;
using System.IO;

namespace Serialization
{
    /// <summary>
    /// A write-only pass-through stream that counts the bytes written to it — the uncompressed size of a payload
    /// when placed above a compression stream. Every write is forwarded as the same call, so the inner stream
    /// sees exactly the writes it would see without the counter. Reusable: <see cref="Attach"/> rebinds it.
    /// </summary>
    internal sealed class CountingWriteStream : Stream
    {
        private Stream _inner;

        /// <summary>Bytes written since the last <see cref="Attach"/>.</summary>
        public long BytesWritten { get; private set; }

        /// <summary>Starts forwarding to <paramref name="inner"/> and zeroes the count.</summary>
        /// <param name="inner">The stream that receives every write.</param>
        public void Attach(Stream inner)
        {
            _inner = inner;
            BytesWritten = 0;
        }

        /// <summary>Drops the inner stream so a pooled instance does not keep it alive.</summary>
        public void Detach() => _inner = null;

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            BytesWritten += count;
        }

        /// <inheritdoc />
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _inner.Write(buffer);
            BytesWritten += buffer.Length;
        }

        /// <inheritdoc />
        public override void WriteByte(byte value)
        {
            _inner.WriteByte(value);
            BytesWritten++;
        }

        /// <inheritdoc />
        public override void Flush() => _inner.Flush();

        /// <inheritdoc />
        public override bool CanRead => false;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => true;

        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

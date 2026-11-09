namespace AgMemory.Storage.LanceDb;

/// <summary>Stops JSON serialization before a document can exceed its disk budget; the caller owns the inner stream.</summary>
internal sealed class KnowledgeBoundedWriteStream(Stream inner, long maximumBytes) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

    /// <summary>Rejects an oversized chunk before changing the unpublished file.</summary>
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    /// <summary>Checks the remaining byte budget before passing each JSON chunk to the file.</summary>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length > maximumBytes - inner.Position) throw new InvalidDataException("Knowledge document exceeds its size limit.");
        inner.Write(buffer);
    }

    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

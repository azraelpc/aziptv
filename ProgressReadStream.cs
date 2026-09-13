using System;
using System.IO;

namespace AzIPTV;

/// <summary>
/// Read-only stream wrapper that tracks bytes read to estimate parse progress.
/// </summary>
internal sealed class ProgressReadStream : Stream
{
    private readonly Stream _inner;
    private readonly IProgress<double>? _progress;
    private long _lastReportedBytes;

    public ProgressReadStream(Stream inner, long totalBytes, IProgress<double>? progress = null)
    {
        _inner = inner;
        TotalBytes = Math.Max(0, totalBytes);
        _progress = progress;
    }

    public long BytesRead { get; private set; }
    public long TotalBytes { get; }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = _inner.Read(buffer, offset, count);
        OnRead(read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        int read = _inner.Read(buffer);
        OnRead(read);
        return read;
    }

    public override int ReadByte()
    {
        int b = _inner.ReadByte();
        if (b >= 0) OnRead(1);
        return b;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }

    private void OnRead(int count)
    {
        if (count <= 0) return;
        BytesRead += count;
        if (_progress is null || TotalBytes <= 0) return;

        // Report only on meaningful byte deltas to avoid flooding the UI.
        if (BytesRead - _lastReportedBytes < 64 * 1024 && BytesRead < TotalBytes)
            return;

        _lastReportedBytes = BytesRead;
        double pct = Math.Clamp((double)BytesRead / TotalBytes, 0d, 1d);
        _progress.Report(pct);
    }
}

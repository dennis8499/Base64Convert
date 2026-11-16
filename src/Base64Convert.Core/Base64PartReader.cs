using System.Buffers;
using System.Buffers.Text;

namespace Base64Convert.Core;

/// <summary>Joins and decodes parts without buffering the package or requiring a whole header in one part.</summary>
internal sealed class Base64PartReader(IReadOnlyList<PartFile> parts, IProgress<ConversionProgress>? progress = null,
    long encodedLength = 0) : Stream
{
    private readonly byte[] _encoded = new byte[ConversionService.EncodedBufferSize];
    private readonly byte[] _decoded = new byte[ConversionService.InputBufferSize];
    private readonly ProgressReporter _reporter = new(progress, "正在解碼與還原", encodedLength);
    private FileStream? _input;
    private int _part, _offset, _available;
    private long _remaining, _processed;
    private bool _disposed;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.Length == 0) return 0;
        while (_offset == _available)
        {
            if (_input is not null && _remaining == 0)
            {
                await _input.DisposeAsync();
                _input = null;
                _part++;
            }
            if (_input is null)
            {
                if (_part == parts.Count) return 0;
                PartFile part = parts[_part];
                _input = new FileStream(part.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    _encoded.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (_input.Length != part.Length) throw new IOException("分片大小已變更，請重新選取分片。");
                _remaining = part.Length;
            }
            int count = (int)Math.Min(_remaining, _encoded.Length);
            await _input.ReadExactlyAsync(_encoded.AsMemory(0, count), cancellationToken);
            bool final = _part == parts.Count - 1 && _remaining == count;
            for (int i = 0; i < count; i++)
            {
                byte b = _encoded[i];
                bool alphabet = b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'
                    or >= (byte)'0' and <= (byte)'9' or (byte)'+' or (byte)'/';
                if (!alphabet && !(b == '=' && final && i >= count - 2))
                    throw new InvalidDataException("分片包含無效的 Base64 字元或填補符號。");
            }
            OperationStatus status = Base64.DecodeFromUtf8(_encoded.AsSpan(0, count), _decoded,
                out int consumed, out _available, isFinalBlock: final);
            if (status != OperationStatus.Done || consumed != count) throw new InvalidDataException("分片包含無效的 Base64 內容。");
            _offset = 0;
            _remaining -= count;
            _processed += count;
            _reporter.Report(_processed);
        }
        int copied = Math.Min(buffer.Length, _available - _offset);
        _decoded.AsMemory(_offset, copied).CopyTo(buffer);
        _offset += copied;
        return copied;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) _input?.Dispose();
        _disposed = true;
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        if (_input is not null) await _input.DisposeAsync();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

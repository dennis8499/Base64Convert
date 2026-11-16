using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Base64Convert.Core;

namespace Base64Convert.Tests;

internal static class TestSupport
{
    internal const string SetId = "012345abcdef";
    internal static byte[] Bytes(int count)
    {
        var bytes = new byte[count];
        new Random(123456).NextBytes(bytes);
        return bytes;
    }
    internal static byte[] Compress(byte[] input)
    {
        using var output = new MemoryStream();
        using (var compressor = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) compressor.Write(input);
        return output.ToArray();
    }
    internal static JsonObject Metadata(byte[] original, byte[] payload, string method = "none", string name = "original.bin") => new()
    {
        ["FormatVersion"] = 2, ["SetId"] = SetId, ["OriginalFileName"] = name,
        ["OriginalLength"] = original.LongLength, ["PayloadLength"] = payload.LongLength,
        ["Compression"] = method, ["Sha256"] = Convert.ToHexString(SHA256.HashData(original))
    };
    // Deliberately independent of the production formatter and JSON options.
    internal static byte[] Frame(JsonObject metadata, byte[] payload)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(metadata);
        byte[] frame = new byte[12 + json.Length + payload.Length];
        Encoding.ASCII.GetBytes("B64CVT02").CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8, 4), (uint)json.Length);
        json.CopyTo(frame, 12);
        payload.CopyTo(frame, 12 + json.Length);
        return frame;
    }
    internal static async Task<string[]> WriteParts(string directory, byte[] frame, int limit = 1_000_000,
        string prefix = "fixture", string setId = SetId)
    {
        string encoded = Convert.ToBase64String(frame);
        int count = (encoded.Length + limit - 1) / limit;
        string[] paths = Enumerable.Range(1, count).Select(i => Path.Combine(directory,
            $"{prefix}.{setId}.v2.part{i:D6}-of-{count:D6}.txt")).ToArray();
        for (int i = 0; i < count; i++)
            await File.WriteAllTextAsync(paths[i], encoded.Substring(i * limit, Math.Min(limit, encoded.Length - i * limit)), Encoding.ASCII);
        return paths;
    }
    internal static async Task<byte[]> ReadFrame(IEnumerable<string> paths) =>
        Convert.FromBase64String(string.Concat(await Task.WhenAll(paths.Select(path => File.ReadAllTextAsync(path)))));
    internal static (JsonObject Metadata, byte[] Payload) ParseFrame(byte[] frame)
    {
        Check(Encoding.ASCII.GetString(frame, 0, 8) == "B64CVT02", "Incorrect v2 magic.");
        int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8, 4)));
        return (JsonNode.Parse(frame.AsSpan(12, size))!.AsObject(), frame[(12 + size)..]);
    }
    internal static async Task VerifyRestored(string directory, IEnumerable<string> paths, byte[] original, string file = "restored.bin")
    {
        RestorePlan plan = await ConversionService.CreateRestorePlanAsync(paths);
        string output = Path.Combine(directory, file);
        await ConversionService.RestoreAsync(plan, output, false);
        Check((await File.ReadAllBytesAsync(output)).SequenceEqual(original), "Restored original bytes differ.");
    }
    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    internal static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T ex) { return ex; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    internal static void NoTemporaryFiles(string directory) => Check(
        !Directory.EnumerateFileSystemEntries(directory, ".base64convert-*.tmp").Any(), "Temporary output was left behind.");
}

internal sealed class SynchronousProgress(Action<ConversionProgress> report) : IProgress<ConversionProgress>
{
    public void Report(ConversionProgress value) => report(value);
}

internal sealed class ControlledIo : ConversionFileOperations
{
    internal Func<string, bool>? FailWrite { get; set; }
    internal Action<string>? OnWrite { get; set; }
    internal Func<string, string, bool>? FailMove { get; set; }
    internal bool FailCleanup { get; set; }
    internal long? FreeSpace { get; set; } = long.MaxValue;
    internal override long? AvailableSpace(string directory) => FreeSpace;
    internal override Stream CreateOutput(string path, int bufferSize) => new ControlledStream(
        base.CreateOutput(path, bufferSize), () => OnWrite?.Invoke(path), () => FailWrite?.Invoke(path) == true);
    internal override void MoveFile(string source, string destination, bool overwrite = false)
    {
        if (FailMove?.Invoke(source, destination) == true) throw new IOException("simulated move failure");
        base.MoveFile(source, destination, overwrite);
    }
    internal override void DeleteDirectory(string path)
    {
        if (FailCleanup) throw new IOException("simulated cleanup failure");
        base.DeleteDirectory(path);
    }
}

internal sealed class ControlledStream(Stream inner, Action onWrite, Func<bool> fail) : Stream
{
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        onWrite();
        if (fail())
        {
            await inner.WriteAsync(buffer[..Math.Min(16, buffer.Length)], cancellationToken);
            throw new IOException("simulated disk full");
        }
        await inner.WriteAsync(buffer, cancellationToken);
    }
    public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); GC.SuppressFinalize(this); }
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

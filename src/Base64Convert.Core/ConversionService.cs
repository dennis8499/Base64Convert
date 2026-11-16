using System.Buffers;
using System.Buffers.Text;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Base64Convert.Core;

public sealed record ConversionProgress(string Message, long ProcessedBytes, long TotalBytes);
public enum CompressionMethod { None, Brotli }

public sealed record PackageMetadata
{
    [JsonRequired] public int FormatVersion { get; init; } = 2;
    [JsonRequired] public string SetId { get; init; } = "";
    [JsonRequired] public string OriginalFileName { get; init; } = "";
    [JsonRequired] public long OriginalLength { get; init; }
    [JsonRequired] public long PayloadLength { get; init; }
    [JsonRequired] public CompressionMethod Compression { get; init; }
    [JsonRequired] public string Sha256 { get; init; } = "";
}

public sealed record ExportResult(CompressionMethod Compression, long OriginalBytes, long PayloadBytes,
    long EncodedBytes, long UncompressedEncodedBytes, int PartCount)
{
    public long SavedBytes => UncompressedEncodedBytes - EncodedBytes;
}

/// <summary>A single-use prepared export. Dispose even when declining to export.</summary>
public sealed class ExportPlan : IAsyncDisposable
{
    private readonly FileStream _source;
    private readonly Stream _payload;
    private int _state; // 0 prepared, 1 exporting, 2 finished, 3 disposed
    internal ExportPlan(string sourcePath, string directory, long limit, string[] paths,
        PackageMetadata metadata, byte[] header, long baseline, string stage,
        FileStream source, Stream payload, ConversionFileOperations files)
    {
        SourcePath = sourcePath;
        DestinationDirectory = directory;
        PartByteLimit = limit;
        PartPaths = Array.AsReadOnly(paths);
        Metadata = metadata;
        Header = header;
        EncodedLength = PackageFormat.EncodedLength(checked(header.LongLength + metadata.PayloadLength));
        UncompressedEncodedLength = baseline;
        Stage = stage;
        _source = source;
        _payload = payload;
        Files = files;
    }
    public string SourcePath { get; }
    public string DestinationDirectory { get; }
    public long SourceLength => Metadata.OriginalLength;
    public long PartByteLimit { get; }
    public IReadOnlyList<string> PartPaths { get; }
    public IEnumerable<string> OutputPaths => PartPaths;
    public PackageMetadata Metadata { get; }
    public long EncodedLength { get; }
    public long UncompressedEncodedLength { get; }
    internal byte[] Header { get; }
    internal string Stage { get; }
    internal Stream Payload => _payload;
    internal ConversionFileOperations Files { get; }
    internal bool KeepBackups { get; set; }
    internal void BeginExport()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            throw new InvalidOperationException("匯出計畫只能執行一次，且不可在釋放後使用。");
    }
    internal void FinishExport() => Interlocked.CompareExchange(ref _state, 2, 1);
    public async ValueTask DisposeAsync()
    {
        int previous;
        do
        {
            previous = Volatile.Read(ref _state);
            if (previous == 3) return;
            if (previous == 1) throw new InvalidOperationException("匯出進行中，不可釋放計畫。");
        } while (Interlocked.CompareExchange(ref _state, 3, previous) != previous);
        var errors = new List<Exception>();
        try { if (!ReferenceEquals(_payload, _source)) await _payload.DisposeAsync(); }
        catch (Exception ex) { errors.Add(ex); }
        try { await _source.DisposeAsync(); }
        catch (Exception ex) { errors.Add(ex); }
        if (!KeepBackups)
        {
            try { Files.DeleteDirectory(Stage); }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (errors.Count > 0)
            throw new IOException($"無法完整清理暫存資料，請檢查：{Stage}", new AggregateException(errors));
    }
}

public sealed record PartFile(string Path, long Length);
public sealed class RestorePlan
{
    internal RestorePlan(PartFile[] parts, PackageMetadata metadata, byte[] header, long encodedLength)
    {
        Parts = Array.AsReadOnly(parts);
        Metadata = metadata;
        Header = header;
        EncodedLength = encodedLength;
    }
    public IReadOnlyList<PartFile> Parts { get; }
    public PackageMetadata Metadata { get; }
    public string SuggestedFileName => Metadata.OriginalFileName;
    public long EncodedLength { get; }
    internal byte[] Header { get; }
}

/// <summary>Streams v2 packages with bounded buffers and mandatory integrity verification.</summary>
public static partial class ConversionService
{
    internal const int InputBufferSize = 192 * 1024;
    internal const int EncodedBufferSize = InputBufferSize / 3 * 4;
    private const int MaximumPartCount = 999_999;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    [GeneratedRegex(@"^(?<name>.+)\.(?<id>[a-fA-F0-9]{12})\.v2\.part(?<index>\d{6})-of-(?<count>\d{6})\.txt$", RegexOptions.CultureInvariant)]
    private static partial Regex PartNamePattern();

    public static Task<ExportPlan> CreateExportPlanAsync(string sourcePath, string destinationDirectory,
        long maximumPartBytes, IProgress<ConversionProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        PrepareAsync(sourcePath, destinationDirectory, maximumPartBytes, ConversionFileOperations.Real, progress, cancellationToken);

    internal static async Task<ExportPlan> PrepareAsync(string sourcePath, string destinationDirectory,
        long maximumPartBytes, ConversionFileOperations files, IProgress<ConversionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("找不到來源檔案。", sourcePath);
        if (string.IsNullOrWhiteSpace(destinationDirectory) || !Directory.Exists(destinationDirectory))
            throw new DirectoryNotFoundException("找不到輸出資料夾。");
        if (maximumPartBytes < 4)
            throw new ArgumentOutOfRangeException(nameof(maximumPartBytes), "分片大小至少需要 4 位元組。");
        sourcePath = Path.GetFullPath(sourcePath);
        destinationDirectory = Path.GetFullPath(destinationDirectory);
        long limit = maximumPartBytes - maximumPartBytes % 4;
        string stage = Path.Combine(destinationDirectory, $".base64convert-{Guid.NewGuid():N}.tmp");
        var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            InputBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        Stream? payload = null;
        try
        {
            Directory.CreateDirectory(stage);
            string compressedPath = Path.Combine(stage, "payload.br");
            string sha;
            long sourceLength = source.Length;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using (Stream output = files.CreateOutput(compressedPath, InputBufferSize))
                    await CompressAsync(source, output, hash, progress, cancellationToken);
                sha = Convert.ToHexString(hash.GetHashAndReset());
            }
            var raw = new PackageMetadata
            {
                SetId = Guid.NewGuid().ToString("N")[..12], OriginalFileName = Path.GetFileName(sourcePath),
                OriginalLength = sourceLength, PayloadLength = sourceLength,
                Compression = CompressionMethod.None, Sha256 = sha
            };
            byte[] rawHeader = PackageFormat.CreateHeader(raw);
            long baseline = PackageFormat.EncodedLength(checked(rawHeader.LongLength + sourceLength));
            var packed = raw with { Compression = CompressionMethod.Brotli, PayloadLength = new FileInfo(compressedPath).Length };
            byte[] packedHeader = PackageFormat.CreateHeader(packed);
            long packedLength = PackageFormat.EncodedLength(checked(packedHeader.LongLength + packed.PayloadLength));
            bool useCompression = packedLength < baseline;
            PackageMetadata metadata = useCompression ? packed : raw;
            byte[] header = useCompression ? packedHeader : rawHeader;
            long encodedLength = useCompression ? packedLength : baseline;
            long count = encodedLength / limit + (encodedLength % limit == 0 ? 0 : 1);
            if (count > MaximumPartCount)
                throw new InvalidOperationException($"分片數量超過 {MaximumPartCount:N0} 個，請調大每個分片的大小。");
            string prefix = raw.OriginalFileName.Length <= 200 ? raw.OriginalFileName : raw.OriginalFileName[..200];
            if (char.IsHighSurrogate(prefix[^1])) prefix = prefix[..^1];
            string[] paths = Enumerable.Range(1, (int)count).Select(i => Path.Combine(destinationDirectory,
                $"{prefix}.{raw.SetId}.v2.part{i:D6}-of-{count:D6}.txt")).ToArray();
            if (paths.Any(p => PathComparer.Equals(p, sourcePath))) throw new IOException("輸出檔案不可與來源檔案相同。");
            if (useCompression)
                payload = new FileStream(compressedPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    InputBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            else
            {
                files.DeleteFile(compressedPath);
                source.Position = 0;
                payload = source;
            }
            files.EnsureSpace(destinationDirectory, encodedLength);
            cancellationToken.ThrowIfCancellationRequested();
            return new ExportPlan(sourcePath, destinationDirectory, limit, paths, metadata, header,
                baseline, stage, source, payload, files);
        }
        catch (Exception error)
        {
            var cleanupErrors = new List<Exception>();
            try { if (payload is not null && !ReferenceEquals(payload, source)) await payload.DisposeAsync(); }
            catch (Exception cleanup) { cleanupErrors.Add(cleanup); }
            try { await source.DisposeAsync(); }
            catch (Exception cleanup) { cleanupErrors.Add(cleanup); }
            try { files.DeleteDirectory(stage); }
            catch (Exception cleanup) { cleanupErrors.Add(cleanup); }
            if (cleanupErrors.Count > 0) throw CleanupError(error, new AggregateException(cleanupErrors), stage);
            throw;
        }
    }

    private static async Task CompressAsync(Stream source, Stream destination, IncrementalHash hash,
        IProgress<ConversionProgress>? progress, CancellationToken token)
    {
        var input = new byte[InputBufferSize];
        var output = new byte[InputBufferSize];
        var encoder = new BrotliEncoder(11, 22);
        var reporter = new ProgressReporter(progress, "正在分析與壓縮", source.Length);
        reporter.Report(0, true);
        long processed = 0;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int read = await source.ReadAsync(input, token);
                bool final = read == 0;
                if (!final) hash.AppendData(input, 0, read);
                int offset = 0;
                OperationStatus status;
                do
                {
                    token.ThrowIfCancellationRequested();
                    status = encoder.Compress(input.AsSpan(offset, read - offset), output,
                        out int consumed, out int written, final);
                    if (status == OperationStatus.InvalidData) throw new InvalidDataException("Brotli 壓縮失敗。");
                    offset += consumed;
                    await destination.WriteAsync(output.AsMemory(0, written), token);
                } while (offset < read || status == OperationStatus.DestinationTooSmall);
                processed += read;
                reporter.Report(processed);
                if (final)
                {
                    if (status != OperationStatus.Done) throw new InvalidDataException("Brotli 壓縮未完整結束。");
                    break;
                }
            }
            if (processed != source.Length) throw new IOException("來源檔案大小已變更，請重新開始轉換。");
        }
        finally { encoder.Dispose(); } // Cancellation abandons the encoder without finalizing it.
    }

    public static async Task<ExportResult> ExportAsync(ExportPlan plan, IEnumerable<string> approvedOverwrites,
        IProgress<ConversionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(approvedOverwrites);
        plan.BeginExport();
        try
        {
            var approved = new HashSet<string>(approvedOverwrites.Select(Path.GetFullPath), PathComparer);
            cancellationToken.ThrowIfCancellationRequested();
            plan.Files.EnsureSpace(plan.DestinationDirectory, plan.EncodedLength);
            var input = new byte[InputBufferSize];
            var encoded = new byte[EncodedBufferSize];
            var stagedFiles = new List<(string Staged, string Destination)>();
            long containerLength = checked(plan.Header.LongLength + plan.Metadata.PayloadLength);
            long position = 0;
            var reporter = new ProgressReporter(progress, "正在編碼與拆分", containerLength);
            reporter.Report(0, true);
            for (int i = 0; i < plan.PartPaths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string staged = Path.Combine(plan.Stage, $"part-{i:D6}");
                stagedFiles.Add((staged, plan.PartPaths[i]));
                await using Stream output = plan.Files.CreateOutput(staged, EncodedBufferSize);
                long remaining = Math.Min(plan.PartByteLimit / 4 * 3, containerLength - position);
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(input.Length, remaining);
                    int fromHeader = (int)Math.Min(count, Math.Max(0, plan.Header.LongLength - position));
                    if (fromHeader > 0) plan.Header.AsMemory((int)position, fromHeader).CopyTo(input);
                    if (fromHeader < count)
                        await plan.Payload.ReadExactlyAsync(input.AsMemory(fromHeader, count - fromHeader), cancellationToken);
                    var status = Base64.EncodeToUtf8(input.AsSpan(0, count), encoded, out int consumed, out int written);
                    if (status != OperationStatus.Done || consumed != count) throw new InvalidDataException("Base64 編碼失敗。");
                    await output.WriteAsync(encoded.AsMemory(0, written), cancellationToken);
                    position += count;
                    remaining -= count;
                    reporter.Report(position);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            CommitFiles(stagedFiles, plan, approved, progress, cancellationToken);
            var result = new ExportResult(plan.Metadata.Compression, plan.SourceLength, plan.Metadata.PayloadLength,
                plan.EncodedLength, plan.UncompressedEncodedLength, plan.PartPaths.Count);
            plan.FinishExport();
            await plan.DisposeAsync();
            return result;
        }
        catch (Exception error)
        {
            plan.FinishExport();
            try { await plan.DisposeAsync(); }
            catch (Exception cleanup) { throw CleanupError(error, cleanup, plan.Stage); }
            throw;
        }
    }

    public static async Task<RestorePlan> CreateRestorePlanAsync(IEnumerable<string> partPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partPaths);
        cancellationToken.ThrowIfCancellationRequested();
        var indexed = new SortedDictionary<int, PartFile>();
        string? setId = null, prefix = null;
        int expectedCount = 0;
        long encodedLength = 0;
        foreach (string path in partPaths.Select(Path.GetFullPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Match match = PartNamePattern().Match(Path.GetFileName(path));
            if (!match.Success) throw new InvalidDataException("僅支援 v2 文字分片，請使用新版程式重新轉換。");
            string currentId = match.Groups["id"].Value;
            string currentPrefix = match.Groups["name"].Value;
            int index = int.Parse(match.Groups["index"].Value);
            int count = int.Parse(match.Groups["count"].Value);
            if (count < 1 || index < 1 || index > count) throw new InvalidDataException("分片編號或總數無效。");
            if (setId is null) { setId = currentId; prefix = currentPrefix; expectedCount = count; }
            else if (!StringComparer.OrdinalIgnoreCase.Equals(setId, currentId) ||
                !PathComparer.Equals(prefix, currentPrefix) || count != expectedCount)
                throw new InvalidDataException("選取的分片不屬於同一組檔案。");
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("找不到分片檔案。", path);
            if (info.Length == 0 || info.Length % 4 != 0) throw new InvalidDataException("分片長度無效。");
            if (!indexed.TryAdd(index, new PartFile(path, info.Length))) throw new InvalidDataException($"分片編號重複：{index}");
            encodedLength = checked(encodedLength + info.Length);
        }
        if (indexed.Count == 0) throw new InvalidDataException("請選取要還原的文字分片。");
        if (indexed.Count != expectedCount || indexed.Keys.First() != 1 || indexed.Keys.Last() != expectedCount)
            throw new InvalidDataException($"分片不完整：需要 {expectedCount} 個，目前選取 {indexed.Count} 個。");
        PartFile[] parts = indexed.Values.ToArray();
        await using var decoded = new Base64PartReader(parts);
        var (metadata, header) = await PackageFormat.ReadHeaderAsync(decoded, cancellationToken);
        if (!StringComparer.OrdinalIgnoreCase.Equals(metadata.SetId, setId)) throw new InvalidDataException("容器與分片組別不符。");
        try
        {
            if (PackageFormat.EncodedLength(checked(header.LongLength + metadata.PayloadLength)) != encodedLength)
                throw new InvalidDataException("分片總長度與容器資訊不符。");
        }
        catch (OverflowException ex) { throw new InvalidDataException("容器長度超出支援範圍。", ex); }
        return new RestorePlan(parts, metadata, header, encodedLength);
    }

    public static Task RestoreAsync(RestorePlan plan, string destinationPath, bool overwriteApproved,
        IProgress<ConversionProgress>? progress = null, CancellationToken cancellationToken = default) =>
        RestoreWithFilesAsync(plan, destinationPath, overwriteApproved, ConversionFileOperations.Real, progress, cancellationToken);

    internal static async Task RestoreWithFilesAsync(RestorePlan plan, string destinationPath, bool overwriteApproved,
        ConversionFileOperations files, IProgress<ConversionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        destinationPath = Path.GetFullPath(destinationPath);
        if (plan.Parts.Any(p => PathComparer.Equals(p.Path, destinationPath))) throw new IOException("還原位置不可與分片相同。");
        string directory = Path.GetDirectoryName(destinationPath)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("找不到還原目的地資料夾。");
        cancellationToken.ThrowIfCancellationRequested();
        files.EnsureSpace(directory, plan.Metadata.OriginalLength);
        string staged = Path.Combine(directory, $".base64convert-{Guid.NewGuid():N}.tmp");
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var decoded = new Base64PartReader(plan.Parts, progress, plan.EncodedLength))
            {
                var (_, header) = await PackageFormat.ReadHeaderAsync(decoded, cancellationToken);
                if (!header.AsSpan().SequenceEqual(plan.Header)) throw new InvalidDataException("分片標頭已變更，請重新選取分片。");
                await using Stream output = files.CreateOutput(staged, InputBufferSize);
                long written = await RestorePayloadAsync(decoded, output, plan.Metadata, hash, cancellationToken);
                var extra = new byte[1];
                if (await decoded.ReadAsync(extra, cancellationToken) != 0) throw new InvalidDataException("容器包含多餘資料。");
                progress?.Report(new ConversionProgress("正在驗證完整性", plan.EncodedLength, plan.EncodedLength));
                if (written != plan.Metadata.OriginalLength ||
                    !StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(hash.GetHashAndReset()), plan.Metadata.Sha256))
                    throw new InvalidDataException("還原內容的大小或 SHA-256 不符，檔案可能已損毀。");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(destinationPath) && !overwriteApproved) throw new IOException("目的檔案已存在，但尚未同意覆寫。");
            progress?.Report(new ConversionProgress("正在儲存還原檔案", 0, plan.Metadata.OriginalLength));
            cancellationToken.ThrowIfCancellationRequested();
            files.MoveFile(staged, destinationPath, overwriteApproved);
        }
        catch (Exception error)
        {
            try { files.DeleteFile(staged); }
            catch (Exception cleanup) { throw CleanupError(error, cleanup, staged); }
            throw;
        }
    }

    private static async Task<long> RestorePayloadAsync(Stream input, Stream output, PackageMetadata metadata,
        IncrementalHash hash, CancellationToken token)
    {
        var buffer = new byte[InputBufferSize];
        var decoded = new byte[InputBufferSize];
        long remaining = metadata.PayloadLength, writtenTotal = 0;
        var decoder = new BrotliDecoder();
        try
        {
            if (metadata.Compression == CompressionMethod.None)
            {
                while (remaining > 0)
                {
                    token.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(buffer.Length, remaining);
                    await ReadPayloadExactlyAsync(input, buffer.AsMemory(0, count), token);
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    hash.AppendData(buffer, 0, count);
                    writtenTotal += count;
                    remaining -= count;
                }
                return writtenTotal;
            }
            int available = 0, offset = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (offset == available && remaining > 0)
                {
                    available = (int)Math.Min(buffer.Length, remaining);
                    await ReadPayloadExactlyAsync(input, buffer.AsMemory(0, available), token);
                    remaining -= available;
                    offset = 0;
                }
                OperationStatus status = decoder.Decompress(buffer.AsSpan(offset, available - offset), decoded,
                    out int consumed, out int written);
                offset += consumed;
                if (written > metadata.OriginalLength - writtenTotal) throw new InvalidDataException("解壓縮結果超過宣告大小。");
                await output.WriteAsync(decoded.AsMemory(0, written), token);
                hash.AppendData(decoded, 0, written);
                writtenTotal += written;
                if (status == OperationStatus.Done)
                {
                    if (remaining != 0 || offset != available) throw new InvalidDataException("Brotli 串流包含尾隨資料。");
                    return writtenTotal;
                }
                if (status == OperationStatus.InvalidData ||
                    (status == OperationStatus.NeedMoreData && (remaining == 0 || offset != available)) ||
                    (status == OperationStatus.DestinationTooSmall && consumed == 0 && written == 0))
                    throw new InvalidDataException("Brotli 串流無效或遭截斷。");
            }
        }
        finally { decoder.Dispose(); }
    }

    private static async Task ReadPayloadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        try { await stream.ReadExactlyAsync(buffer, token); }
        catch (EndOfStreamException ex) { throw new InvalidDataException("容器資料遭截斷。", ex); }
    }

    private static void CommitFiles(List<(string Staged, string Destination)> files, ExportPlan plan,
        HashSet<string> approved, IProgress<ConversionProgress>? progress, CancellationToken token)
    {
        var moved = new List<(string Destination, string? Backup, bool Installed)>();
        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                progress?.Report(new ConversionProgress("正在提交分片", i * plan.PartByteLimit, plan.EncodedLength));
                token.ThrowIfCancellationRequested();
                var (source, destination) = files[i];
                bool exists = File.Exists(destination);
                if (exists && !approved.Contains(destination)) throw new IOException($"輸出檔案已存在，但尚未同意覆寫：{destination}");
                string? backup = exists ? Path.Combine(plan.Stage, $"backup-{i:D6}") : null;
                if (backup is not null) plan.Files.MoveFile(destination, backup);
                moved.Add((destination, backup, false));
                plan.Files.MoveFile(source, destination);
                moved[^1] = (destination, backup, true);
            }
            token.ThrowIfCancellationRequested();
        }
        catch (Exception error)
        {
            var rollbackErrors = new List<Exception>();
            for (int i = moved.Count - 1; i >= 0; i--)
            {
                try
                {
                    var record = moved[i];
                    if (record.Installed) plan.Files.DeleteFile(record.Destination);
                    if (record.Backup is not null) plan.Files.MoveFile(record.Backup, record.Destination);
                }
                catch (Exception rollback) { rollbackErrors.Add(rollback); }
            }
            if (rollbackErrors.Count > 0)
            {
                plan.KeepBackups = true;
                throw new IOException($"轉換失敗，且部分原檔無法自動復原。備份保留於：{plan.Stage}",
                    new AggregateException(new[] { error }.Concat(rollbackErrors)));
            }
            throw;
        }
    }

    private static IOException CleanupError(Exception error, Exception cleanup, string path) =>
        new($"{error.Message}\n另外無法清理暫存資料，請檢查：{path}", new AggregateException(error, cleanup));
}

internal sealed class ProgressReporter(IProgress<ConversionProgress>? progress, string message, long total)
{
    private long _lastReport = Environment.TickCount64 - 1000;
    internal void Report(long processed, bool force = false)
    {
        long now = Environment.TickCount64;
        if (!force && now - _lastReport < 100) return;
        _lastReport = now;
        progress?.Report(new ConversionProgress(message, processed, total));
    }
}

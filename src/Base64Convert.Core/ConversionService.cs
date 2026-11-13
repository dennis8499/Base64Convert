using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Base64Convert.Core;

public sealed record ConversionProgress(string Message, long ProcessedBytes, long TotalBytes);

public sealed record VerificationInfo
{
    [JsonRequired]
    public int FormatVersion { get; init; } = 1;
    [JsonRequired]
    public string SetId { get; init; } = "";
    [JsonRequired]
    public string OriginalFileName { get; init; } = "";
    [JsonRequired]
    public long OriginalLength { get; init; }
    [JsonRequired]
    public int PartCount { get; init; }
    [JsonRequired]
    public string Sha256 { get; init; } = "";
}

public sealed class ExportPlan
{
    internal ExportPlan(string sourcePath, string destinationDirectory, long sourceLength,
        long partByteLimit, long inputBytesPerPart, string setId, string[] partPaths, string? manifestPath)
    {
        SourcePath = sourcePath;
        DestinationDirectory = destinationDirectory;
        SourceLength = sourceLength;
        PartByteLimit = partByteLimit;
        InputBytesPerPart = inputBytesPerPart;
        SetId = setId;
        PartPaths = Array.AsReadOnly(partPaths);
        ManifestPath = manifestPath;
    }

    public string SourcePath { get; }
    public string DestinationDirectory { get; }
    public long SourceLength { get; }
    public long PartByteLimit { get; }
    public long InputBytesPerPart { get; }
    public string SetId { get; }
    public IReadOnlyList<string> PartPaths { get; }
    public string? ManifestPath { get; }

    public IEnumerable<string> OutputPaths => ManifestPath is null
        ? PartPaths
        : PartPaths.Append(ManifestPath);
}

public sealed record PartFile(string Path, long Length);

public sealed class RestorePlan
{
    internal RestorePlan(PartFile[] parts, string suggestedFileName, string? manifestPath,
        VerificationInfo? verification, long encodedLength)
    {
        Parts = Array.AsReadOnly(parts);
        SuggestedFileName = suggestedFileName;
        ManifestPath = manifestPath;
        Verification = verification;
        EncodedLength = encodedLength;
    }

    public IReadOnlyList<PartFile> Parts { get; }
    public string SuggestedFileName { get; }
    public string? ManifestPath { get; }
    public VerificationInfo? Verification { get; }
    public long EncodedLength { get; }
}

/// <summary>Converts streams without loading a whole file into memory.</summary>
public static partial class ConversionService
{
    private const int InputBufferSize = 192 * 1024; // A multiple of three.
    private const int EncodedBufferSize = InputBufferSize / 3 * 4;
    private const int MaximumPartCount = 999_999;
    private const int MaximumNamePrefixLength = 200;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    [GeneratedRegex(@"^(?<name>.+)\.(?<id>[a-fA-F0-9]{12})\.part(?<index>\d{6})-of-(?<count>\d{6})\.txt$", RegexOptions.CultureInvariant)]
    private static partial Regex PartNamePattern();

    public static ExportPlan CreateExportPlan(string sourcePath, string destinationDirectory,
        long maximumPartBytes, bool createVerificationInfo)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("找不到來源檔案。", sourcePath);
        if (string.IsNullOrWhiteSpace(destinationDirectory) || !Directory.Exists(destinationDirectory))
            throw new DirectoryNotFoundException("找不到輸出資料夾。");
        if (maximumPartBytes < 4)
            throw new ArgumentOutOfRangeException(nameof(maximumPartBytes), "分片大小至少需要 4 位元組。");

        sourcePath = Path.GetFullPath(sourcePath);
        destinationDirectory = Path.GetFullPath(destinationDirectory);
        var fileName = Path.GetFileName(sourcePath);
        long sourceLength = new FileInfo(sourcePath).Length;
        long alignedLimit = maximumPartBytes - maximumPartBytes % 4;
        long inputPerPart = alignedLimit / 4 * 3;
        long partCount = sourceLength == 0 ? 1 : sourceLength / inputPerPart + (sourceLength % inputPerPart == 0 ? 0 : 1);
        if (partCount > MaximumPartCount)
            throw new InvalidOperationException($"分片數量超過 {MaximumPartCount:N0} 個，請調大每個分片的大小。");

        // Keep component names below Windows' usual 255-character limit. The full name is in the optional manifest.
        string prefix = fileName.Length <= MaximumNamePrefixLength ? fileName : fileName[..MaximumNamePrefixLength];
        if (char.IsHighSurrogate(prefix[^1])) prefix = prefix[..^1];
        string setId = Guid.NewGuid().ToString("N")[..12];
        string stem = $"{prefix}.{setId}";
        var parts = Enumerable.Range(1, (int)partCount)
            .Select(i => Path.Combine(destinationDirectory, $"{stem}.part{i:D6}-of-{partCount:D6}.txt"))
            .ToArray();
        string? manifest = createVerificationInfo ? Path.Combine(destinationDirectory, $"{stem}.base64.json") : null;
        if (parts.Any(p => PathComparer.Equals(p, sourcePath)) || (manifest is not null && PathComparer.Equals(manifest, sourcePath)))
            throw new IOException("輸出檔案不可與來源檔案相同。");
        return new ExportPlan(sourcePath, destinationDirectory, sourceLength, alignedLimit, inputPerPart, setId, parts, manifest);
    }

    public static async Task ExportAsync(ExportPlan plan, IEnumerable<string> approvedOverwrites,
        IProgress<ConversionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var approved = new HashSet<string>(approvedOverwrites.Select(Path.GetFullPath), PathComparer);
        cancellationToken.ThrowIfCancellationRequested();
        await using var input = new FileStream(plan.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            InputBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length != plan.SourceLength)
            throw new IOException("來源檔案大小已變更，請重新開始轉換。");

        string stage = Path.Combine(plan.DestinationDirectory, $".base64convert-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(stage);
        bool keepStage = false;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var inputBuffer = new byte[InputBufferSize];
            var encodedBuffer = new byte[EncodedBufferSize];
            var stagedFiles = new List<(string Staged, string Destination)>();
            long processed = 0;
            var reporter = new ProgressReporter(progress, "正在轉換檔案", plan.SourceLength);
            reporter.Report(0, true);

            for (int i = 0; i < plan.PartPaths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string staged = Path.Combine(stage, $"part-{i:D6}");
                stagedFiles.Add((staged, plan.PartPaths[i]));
                await using var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    EncodedBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                long remaining = Math.Min(plan.InputBytesPerPart, plan.SourceLength - processed);
                while (remaining > 0)
                {
                    int read = (int)Math.Min(inputBuffer.Length, remaining);
                    await input.ReadExactlyAsync(inputBuffer.AsMemory(0, read), cancellationToken);
                    hash.AppendData(inputBuffer, 0, read);
                    OperationStatus status = Base64.EncodeToUtf8(inputBuffer.AsSpan(0, read), encodedBuffer, out int consumed, out int written);
                    if (status != OperationStatus.Done || consumed != read)
                        throw new InvalidDataException("Base64 編碼失敗。");
                    await output.WriteAsync(encodedBuffer.AsMemory(0, written), cancellationToken);
                    remaining -= read;
                    processed += read;
                    reporter.Report(processed);
                }
            }

            if (plan.ManifestPath is not null)
            {
                string staged = Path.Combine(stage, "manifest");
                var info = new VerificationInfo
                {
                    SetId = plan.SetId,
                    OriginalFileName = Path.GetFileName(plan.SourcePath),
                    OriginalLength = plan.SourceLength,
                    PartCount = plan.PartPaths.Count,
                    Sha256 = Convert.ToHexString(hash.GetHashAndReset())
                };
                await using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.Asynchronous))
                    await JsonSerializer.SerializeAsync(stream, info, JsonOptions, cancellationToken);
                stagedFiles.Add((staged, plan.ManifestPath));
            }

            cancellationToken.ThrowIfCancellationRequested();
            reporter.Report(plan.SourceLength, true);
            try
            {
                CommitFiles(stagedFiles, stage, approved, cancellationToken);
            }
            catch (RollbackFailedException)
            {
                keepStage = true;
                throw;
            }
        }
        finally
        {
            if (!keepStage) DeleteTemporaryDirectory(stage);
        }
    }

    public static string? FindVerificationInfo(string partPath)
    {
        string fullPath = Path.GetFullPath(partPath);
        Match match = PartNamePattern().Match(Path.GetFileName(fullPath));
        if (!match.Success) return null;
        string path = Path.Combine(Path.GetDirectoryName(fullPath)!,
            $"{match.Groups["name"].Value}.{match.Groups["id"].Value}.base64.json");
        return File.Exists(path) ? path : null;
    }

    public static async Task<RestorePlan> CreateRestorePlanAsync(IEnumerable<string> partPaths,
        string? verificationPath = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partPaths);
        string[] paths = partPaths.Select(Path.GetFullPath).ToArray();
        if (paths.Length == 0) throw new InvalidDataException("請選取要還原的文字分片。");
        var indexed = new SortedDictionary<int, PartFile>();
        string? setId = null, prefix = null;
        int expectedCount = 0;
        long encodedLength = 0;
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Match match = PartNamePattern().Match(Path.GetFileName(path));
            if (!match.Success) throw new InvalidDataException($"不是本程式產生的分片檔名：{Path.GetFileName(path)}");
            string currentId = match.Groups["id"].Value;
            string currentPrefix = match.Groups["name"].Value;
            int index = int.Parse(match.Groups["index"].Value);
            int count = int.Parse(match.Groups["count"].Value);
            if (count < 1 || index < 1 || index > count)
                throw new InvalidDataException("分片編號或總數無效。");
            if (setId is null)
            {
                setId = currentId;
                prefix = currentPrefix;
                expectedCount = count;
            }
            else if (!StringComparer.OrdinalIgnoreCase.Equals(setId, currentId) ||
                     !PathComparer.Equals(prefix, currentPrefix) || count != expectedCount)
                throw new InvalidDataException("選取的分片不屬於同一組檔案。");
            var file = new FileInfo(path);
            if (!file.Exists) throw new FileNotFoundException("找不到分片檔案。", path);
            if (file.Length % 4 != 0 || (file.Length == 0 && count != 1))
                throw new InvalidDataException($"分片長度無效：{file.Name}");
            if (!indexed.TryAdd(index, new PartFile(path, file.Length)))
                throw new InvalidDataException($"分片編號重複：{index}");
            encodedLength = checked(encodedLength + file.Length);
        }
        if (indexed.Count != expectedCount || indexed.Keys.First() != 1 || indexed.Keys.Last() != expectedCount)
            throw new InvalidDataException($"分片不完整：需要 {expectedCount} 個，目前選取 {indexed.Count} 個。");

        VerificationInfo? verification = null;
        if (!string.IsNullOrWhiteSpace(verificationPath))
        {
            verificationPath = Path.GetFullPath(verificationPath);
            var infoFile = new FileInfo(verificationPath);
            if (!infoFile.Exists) throw new FileNotFoundException("找不到驗證資訊檔。", verificationPath);
            if (infoFile.Length > 1024 * 1024) throw new InvalidDataException("驗證資訊檔過大。");
            try
            {
                await using var stream = new FileStream(verificationPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                verification = await JsonSerializer.DeserializeAsync<VerificationInfo>(stream, cancellationToken: cancellationToken);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("驗證資訊檔格式無效。", ex);
            }
            if (verification is null || verification.FormatVersion != 1 ||
                !StringComparer.OrdinalIgnoreCase.Equals(verification.SetId, setId) ||
                verification.PartCount != expectedCount || verification.OriginalLength < 0 ||
                string.IsNullOrWhiteSpace(verification.OriginalFileName) ||
                verification.OriginalFileName != Path.GetFileName(verification.OriginalFileName) ||
                verification.OriginalFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                verification.OriginalFileName is "." or ".." ||
                verification.Sha256 is null || verification.Sha256.Length != 64 ||
                !verification.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("驗證資訊檔與分片不符，或其中的資料無效。");
        }
        else
        {
            verificationPath = null;
        }
        return new RestorePlan(indexed.Values.ToArray(), verification?.OriginalFileName ?? prefix!,
            verificationPath, verification, encodedLength);
    }

    public static async Task RestoreAsync(RestorePlan plan, string destinationPath, bool overwriteApproved,
        IProgress<ConversionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        destinationPath = Path.GetFullPath(destinationPath);
        if (plan.Parts.Any(p => PathComparer.Equals(p.Path, destinationPath)) ||
            (plan.ManifestPath is not null && PathComparer.Equals(plan.ManifestPath, destinationPath)))
            throw new IOException("還原位置不可與分片或驗證資訊檔相同。");
        string directory = Path.GetDirectoryName(destinationPath)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("找不到還原目的地資料夾。");
        string staged = Path.Combine(directory, $".base64convert-{Guid.NewGuid():N}.tmp");
        bool installed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var encodedBuffer = new byte[EncodedBufferSize];
            var outputBuffer = new byte[InputBufferSize];
            long processed = 0, decodedLength = 0;
            var reporter = new ProgressReporter(progress, "正在還原檔案", plan.EncodedLength);
            reporter.Report(0, true);
            await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                InputBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                for (int i = 0; i < plan.Parts.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PartFile part = plan.Parts[i];
                    await using var input = new FileStream(part.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        EncodedBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (input.Length != part.Length) throw new IOException("分片大小已變更，請重新選取分片。");
                    long remaining = part.Length;
                    while (remaining > 0)
                    {
                        int read = (int)Math.Min(encodedBuffer.Length, remaining);
                        await input.ReadExactlyAsync(encodedBuffer.AsMemory(0, read), cancellationToken);
                        bool finalBlock = i == plan.Parts.Count - 1 && remaining == read;
                        ValidateBase64Characters(encodedBuffer, read, finalBlock);
                        OperationStatus status = Base64.DecodeFromUtf8(encodedBuffer.AsSpan(0, read), outputBuffer,
                            out int consumed, out int written, isFinalBlock: finalBlock);
                        if (status != OperationStatus.Done || consumed != read)
                            throw new InvalidDataException($"分片包含無效的 Base64 內容：{Path.GetFileName(part.Path)}");
                        await output.WriteAsync(outputBuffer.AsMemory(0, written), cancellationToken);
                        hash.AppendData(outputBuffer, 0, written);
                        decodedLength = checked(decodedLength + written);
                        processed += read;
                        remaining -= read;
                        reporter.Report(processed);
                    }
                }
            }
            string actualHash = Convert.ToHexString(hash.GetHashAndReset());
            if (plan.Verification is not null &&
                (decodedLength != plan.Verification.OriginalLength ||
                 !StringComparer.OrdinalIgnoreCase.Equals(actualHash, plan.Verification.Sha256)))
                throw new InvalidDataException("還原內容與驗證資訊不符，檔案可能不完整或已損毀。");
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(destinationPath) && !overwriteApproved)
                throw new IOException("目的檔案已存在，但尚未同意覆寫；請重新選擇儲存位置。");
            File.Move(staged, destinationPath, overwriteApproved);
            installed = true;
            reporter.Report(plan.EncodedLength, true);
        }
        finally
        {
            if (!installed) DeleteTemporaryFile(staged);
        }
    }

    private static void ValidateBase64Characters(byte[] bytes, int count, bool finalBlock)
    {
        for (int i = 0; i < count; i++)
        {
            byte b = bytes[i];
            bool standard = b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'
                or >= (byte)'0' and <= (byte)'9' or (byte)'+' or (byte)'/';
            if (!standard && !(b == (byte)'=' && finalBlock && i >= count - 2))
                throw new InvalidDataException("分片包含無效的 Base64 字元或填補符號。");
        }
    }

    private static void CommitFiles(List<(string Staged, string Destination)> files, string stage,
        HashSet<string> approved, CancellationToken cancellationToken)
    {
        var moved = new List<MoveRecord>();
        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (source, destination) = files[i];
                bool exists = File.Exists(destination);
                if (exists && !approved.Contains(destination))
                    throw new IOException($"輸出檔案已存在，但尚未同意覆寫：{destination}");
                string? backup = exists ? Path.Combine(stage, $"backup-{i:D6}") : null;
                if (backup is not null) File.Move(destination, backup);
                var record = new MoveRecord(destination, backup);
                moved.Add(record);
                File.Move(source, destination);
                record.Installed = true;
            }
        }
        catch (Exception error)
        {
            var rollbackErrors = new List<Exception>();
            for (int i = moved.Count - 1; i >= 0; i--)
            {
                try
                {
                    MoveRecord record = moved[i];
                    if (record.Installed) File.Delete(record.Destination);
                    if (record.Backup is not null) File.Move(record.Backup, record.Destination);
                }
                catch (Exception rollbackError) { rollbackErrors.Add(rollbackError); }
            }
            if (rollbackErrors.Count > 0)
                throw new RollbackFailedException($"轉換未完成，且部分原檔無法自動復原。備份保留於：{stage}",
                    new AggregateException(new[] { error }.Concat(rollbackErrors)));
            throw;
        }
    }

    private sealed class MoveRecord(string destination, string? backup)
    {
        public string Destination { get; } = destination;
        public string? Backup { get; } = backup;
        public bool Installed { get; set; }
    }

    private sealed class RollbackFailedException(string message, Exception inner) : IOException(message, inner);

    private static void DeleteTemporaryDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"無法清理暫存資料夾，請確認輸出狀態並手動刪除：{path}", ex);
        }
    }

    private static void DeleteTemporaryFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"無法清理暫存檔案，請確認輸出狀態並手動刪除：{path}", ex);
        }
    }

    private sealed class ProgressReporter(IProgress<ConversionProgress>? progress, string message, long total)
    {
        private long _lastReport = Environment.TickCount64 - 1000;
        public void Report(long processed, bool force = false)
        {
            long now = Environment.TickCount64;
            if (!force && now - _lastReport < 100) return;
            _lastReport = now;
            progress?.Report(new ConversionProgress(message, processed, total));
        }
    }
}

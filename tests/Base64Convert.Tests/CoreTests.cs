using System.IO.Compression;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Base64Convert.Core;
using static Base64Convert.Tests.TestSupport;

namespace Base64Convert.Tests;

internal static partial class CoreTests
{
    internal static async Task RoundTrips(string directory)
    {
        foreach (int size in new[] { 0, 1, 2, 3, 4, 29, 30, 31, 196_607, 196_608, 196_609 })
        {
            byte[] original = Bytes(size);
            string source = Path.Combine(directory, $"中文 🙂 # {size}.bin");
            await File.WriteAllBytesAsync(source, original);
            long limit = size < 32 ? 43 : 512_000;
            await using ExportPlan plan = await ConversionService.CreateExportPlanAsync(source, directory, limit);
            Check(plan.PartByteLimit == limit - limit % 4, "Part limit was not aligned down.");
            Check(plan.Metadata.Sha256 == Convert.ToHexString(SHA256.HashData(original)), "Embedded hash differs.");
            ExportResult result = await ConversionService.ExportAsync(plan, []);
            Check(result.OriginalBytes == size && result.PartCount == plan.PartPaths.Count, "Result statistics differ.");
            Check(plan.PartPaths.Sum(p => new FileInfo(p).Length) == result.EncodedBytes, "Encoded result size differs.");
            Check(result.EncodedBytes <= result.UncompressedEncodedBytes, "Automatic selection grew the v2 output.");
            foreach (string part in plan.PartPaths)
            {
                byte[] text = await File.ReadAllBytesAsync(part);
                Check(text.Length <= limit && text.Length % 4 == 0, "Part violates limit or alignment.");
                Check(text.All(b => b is >= 33 and <= 126), "Unexpected whitespace, BOM or non-ASCII output.");
            }
            RestorePlan restore = await ConversionService.CreateRestorePlanAsync(plan.PartPaths.Reverse());
            Check(restore.SuggestedFileName == Path.GetFileName(source), "Embedded Unicode name was lost.");
            await VerifyRestored(directory, plan.PartPaths.Reverse(), original, $"restored-{size}.bin");
            NoTemporaryFiles(directory);
        }
        byte[] small = [1];
        string smallSource = Path.Combine(directory, "four-byte.bin");
        await File.WriteAllBytesAsync(smallSource, small);
        await using var smallPlan = await ConversionService.CreateExportPlanAsync(smallSource, directory, 4);
        await ConversionService.ExportAsync(smallPlan, []);
        Check(smallPlan.PartPaths.Count > 12 && smallPlan.PartPaths.All(p => new FileInfo(p).Length == 4), "Header did not cross minimum-size parts.");
        await VerifyRestored(directory, smallPlan.PartPaths, small, "four-byte-restored.bin");
    }

    internal static async Task CompressionSelection(string directory)
    {
        var samples = new[]
        {
            (Name: "text", Data: Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("中文,42,repeatable JSON and CSV data\n", 20_000))), Expected: CompressionMethod.Brotli),
            (Name: "random", Data: Bytes(262_147), Expected: CompressionMethod.None)
        };
        foreach (var sample in samples)
        {
            string path = Path.Combine(directory, sample.Name + ".bin");
            await File.WriteAllBytesAsync(path, sample.Data);
            await using var plan = await ConversionService.CreateExportPlanAsync(path, directory, 1_000_000);
            Check(plan.Metadata.Compression == sample.Expected, "Incorrect compression selection.");
            ExportResult result = await ConversionService.ExportAsync(plan, []);
            var (metadata, payload) = ParseFrame(await ReadFrame(plan.PartPaths));
            metadata["Compression"] = "none";
            metadata["PayloadLength"] = sample.Data.LongLength;
            long independentlyMeasuredBaseline = Convert.ToBase64String(Frame(metadata, sample.Data)).Length;
            Check(result.UncompressedEncodedBytes == independentlyMeasuredBaseline, "Baseline excluded header or Base64 overhead.");
            Check(result.PayloadBytes == payload.Length && result.EncodedBytes <= independentlyMeasuredBaseline, "Selection statistics are incorrect.");
            if (sample.Expected == CompressionMethod.Brotli) Check(result.SavedBytes > 0, "Compressible input did not shrink.");
            else Check(result.SavedBytes == 0, "Fallback baseline is inconsistent.");
        }
        // Find an independently encoded tie; equality must select none even if payload bytes shrink.
        bool foundTie = false;
        for (int size = 1; size <= 80 && !foundTie; size++)
        {
            byte[] original = new byte[size];
            byte[] packed = Compress(original);
            long rawSize = Convert.ToBase64String(Frame(Metadata(original, original, name: "tie.bin"), original)).Length;
            long packedSize = Convert.ToBase64String(Frame(Metadata(original, packed, "brotli", "tie.bin"), packed)).Length;
            if (rawSize != packedSize || packed.Length >= original.Length) continue;
            string path = Path.Combine(directory, "tie.bin");
            await File.WriteAllBytesAsync(path, original);
            await using var plan = await ConversionService.CreateExportPlanAsync(path, directory, 1_000_000);
            Check(plan.Metadata.Compression == CompressionMethod.None, "Encoded-size equality must select none.");
            foundTie = true;
        }
        Check(foundTie, "No equality fixture was exercised.");
    }

    internal static async Task IndependentDecoding(string directory)
    {
        byte[] original = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("independent 解碼驗證\n", 10_000)));
        string source = Path.Combine(directory, "independent.txt");
        await File.WriteAllBytesAsync(source, original);
        await using var plan = await ConversionService.CreateExportPlanAsync(source, directory, 40);
        await ConversionService.ExportAsync(plan, []);
        var (metadata, payload) = ParseFrame(await ReadFrame(plan.PartPaths));
        Check(metadata["FormatVersion"]!.GetValue<int>() == 2 && metadata["Compression"]!.GetValue<string>() == "brotli", "Wire discriminators differ.");
        using var input = new MemoryStream(payload);
        using var decoder = new BrotliStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        await decoder.CopyToAsync(output);
        Check(output.ToArray().SequenceEqual(original), "Independent decoder could not restore the export.");
        Check(metadata["PayloadLength"]!.GetValue<long>() == payload.LongLength, "Wire payload length differs.");
        Check(metadata["Sha256"]!.GetValue<string>() == Convert.ToHexString(SHA256.HashData(original)), "Wire hash differs.");
        Check(Directory.GetFiles(directory, "*.json").Length == 0, "External JSON was emitted.");
        foreach (string method in new[] { "none", "brotli" })
        {
            byte[] data = method == "none" ? original : Compress(original);
            string[] paths = await WriteParts(directory, Frame(Metadata(original, data, method), data), 400, prefix: method);
            await VerifyRestored(directory, paths, original, method + "-external.bin");
        }
    }

    internal static async Task SplitBoundaries(string directory)
    {
        foreach (int encodedSize in new[] { 14_999_996, 15_000_000, 15_000_004 })
        {
            int payloadSize = encodedSize / 4 * 3 - 300;
            byte[] frame = [];
            for (int attempt = 0; attempt < 8; attempt++)
            {
                byte[] data = new byte[payloadSize];
                frame = Frame(Metadata(data, data), data);
                int difference = encodedSize / 4 * 3 - frame.Length;
                if (difference == 0) break;
                payloadSize += difference;
            }
            Check(Convert.ToBase64String(frame).Length == encodedSize, "Boundary fixture is not exact.");
            string[] paths = await WriteParts(directory, frame, 15_000_000, prefix: "boundary" + encodedSize);
            Check(paths.Length == (encodedSize <= 15_000_000 ? 1 : 2), "Wrong default-limit part count.");
            Check(new FileInfo(paths[0]).Length == Math.Min(encodedSize, 15_000_000), "First default-limit part is incorrect.");
            if (paths.Length == 2) Check(new FileInfo(paths[1]).Length == 4, "Boundary remainder is incorrect.");
            await VerifyRestored(directory, paths, new byte[payloadSize], "boundary-restored-" + encodedSize);
        }
    }

    internal static async Task LargeBufferedStreams(string directory)
    {
        byte[] original = Bytes(850_019);
        string source = Path.Combine(directory, "large.dat");
        await File.WriteAllBytesAsync(source, original);
        foreach (long limit in new[] { 100_004L, 2_000_000L })
        {
            await using var plan = await ConversionService.CreateExportPlanAsync(source, directory, limit);
            await ConversionService.ExportAsync(plan, []);
            Check(plan.PartPaths.All(p => new FileInfo(p).Length <= limit), "Buffered output exceeded its limit.");
            Check((await ParseAndDecode(plan.PartPaths)).SequenceEqual(original), "Multi-buffer export differs.");
            await VerifyRestored(directory, plan.PartPaths.Reverse(), original, $"large-{limit}.out");
        }
    }

    internal static async Task HeaderBounds(string directory)
    {
        byte[] original = [7, 8, 9];
        byte[] compact = Frame(Metadata(original, original), original);
        int compactLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(compact.AsSpan(8, 4));
        foreach (int size in new[] { 65_535, 65_536, 65_537 })
        {
            byte[] frame = new byte[12 + size + original.Length];
            compact.AsSpan(0, 12 + compactLength).CopyTo(frame);
            frame.AsSpan(12 + compactLength, size - compactLength).Fill((byte)' ');
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8, 4), (uint)size);
            original.CopyTo(frame, 12 + size);
            string[] paths = await WriteParts(directory, frame, 20_000, "header" + size);
            if (size <= 65_536) await VerifyRestored(directory, paths, original, "header-restored-" + size);
            else await Throws<InvalidDataException>(() => ConversionService.CreateRestorePlanAsync(paths));
        }
        NoTemporaryFiles(directory);
    }

    internal static async Task DefaultExportBoundary(string directory)
    {
        byte[] original = Bytes(11_250_001);
        string source = Path.Combine(directory, "default-limit.bin");
        await File.WriteAllBytesAsync(source, original);
        await using var plan = await ConversionService.CreateExportPlanAsync(source, directory, 15_000_000);
        var commitBytes = new List<long>();
        var progress = new SynchronousProgress(p =>
        {
            if (p.Message != "正在提交分片") return;
            Check(p.TotalBytes == plan.EncodedLength, "Commit progress used part counts as byte counts.");
            commitBytes.Add(p.ProcessedBytes);
        });
        ExportResult result = await ConversionService.ExportAsync(plan, [], progress);
        Check(result.PartCount == 2 && new FileInfo(plan.PartPaths[0]).Length == 15_000_000,
            "Default export failed to fill the first 15 MB part.");
        Check(new FileInfo(plan.PartPaths[1]).Length == result.EncodedBytes - 15_000_000,
            "Default export remainder excluded the embedded header.");
        Check(commitBytes.SequenceEqual(new long[] { 0, 15_000_000 }), "Commit progress did not track staged text bytes.");
        await VerifyRestored(directory, plan.PartPaths, original);
        NoTemporaryFiles(directory);
    }

    private static async Task<byte[]> ParseAndDecode(IEnumerable<string> paths)
    {
        var (metadata, payload) = ParseFrame(await ReadFrame(paths));
        if (metadata["Compression"]!.GetValue<string>() == "none") return payload;
        using var stream = new BrotliStream(new MemoryStream(payload), System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
    }

    internal static async Task Lifecycle(string directory)
    {
        string source = Path.Combine(directory, "source.bin");
        await File.WriteAllBytesAsync(source, Bytes(100));
        var plan = await ConversionService.CreateExportPlanAsync(source, directory, 1000);
        await Throws<IOException>(() => Task.Run(() => { using var writer = File.OpenWrite(source); }));
        await plan.DisposeAsync();
        await plan.DisposeAsync();
        NoTemporaryFiles(directory);
        using (var writer = File.OpenWrite(source)) Check(writer.CanWrite, "Source was not released after declining export.");
        await Throws<InvalidOperationException>(() => ConversionService.ExportAsync(plan, []));
        await using var once = await ConversionService.CreateExportPlanAsync(source, directory, 1000);
        await ConversionService.ExportAsync(once, []);
        await Throws<InvalidOperationException>(() => ConversionService.ExportAsync(once, []));
        NoTemporaryFiles(directory);
        // Full original name survives the shortened filename prefix.
        string longName = new string('a', 205) + ".bin";
        string longPath = Path.Combine(directory, longName);
        await File.WriteAllBytesAsync(longPath, [5]);
        await using var longPlan = await ConversionService.CreateExportPlanAsync(longPath, directory, 1000);
        await ConversionService.ExportAsync(longPlan, []);
        Check(Path.GetFileName(longPlan.PartPaths[0]).Length < 255, "Part component is too long.");
        Check((await ConversionService.CreateRestorePlanAsync(longPlan.PartPaths)).SuggestedFileName == longName, "Full original name was not retained.");
    }

    internal static async Task InvalidArguments(string directory)
    {
        string source = Path.Combine(directory, "source.bin");
        await File.WriteAllBytesAsync(source, [1, 2]);
        await Throws<FileNotFoundException>(() => ConversionService.CreateExportPlanAsync("", directory, 100));
        await Throws<DirectoryNotFoundException>(() => ConversionService.CreateExportPlanAsync(source, Path.Combine(directory, "missing"), 100));
        foreach (long limit in new long[] { -1, 0, 3 })
            await Throws<ArgumentOutOfRangeException>(() => ConversionService.CreateExportPlanAsync(source, directory, limit));
        await Throws<ArgumentNullException>(() => ConversionService.ExportAsync(null!, []));
        await Throws<ArgumentNullException>(() => ConversionService.CreateRestorePlanAsync(null!));
        await Throws<InvalidDataException>(() => ConversionService.CreateRestorePlanAsync([]));
        await Throws<ArgumentNullException>(() => ConversionService.RestoreAsync(null!, source, false));
        NoTemporaryFiles(directory);
    }
}

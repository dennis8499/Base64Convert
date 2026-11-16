using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Base64Convert.Core;
using static Base64Convert.Tests.TestSupport;

namespace Base64Convert.Tests;

internal static partial class CoreTests
{
    internal static async Task InvalidPartSets(string directory)
    {
        byte[] data = Bytes(300);
        string[] paths = await WriteParts(directory, Frame(Metadata(data, data), data), 40);
        await Throws<InvalidDataException>(() => ConversionService.CreateRestorePlanAsync(paths.Skip(1)));
        await Throws<InvalidDataException>(() => ConversionService.CreateRestorePlanAsync(paths.Append(paths[0])));
        var other = Metadata(data, data);
        other["SetId"] = "abcdef012345";
        string[] mixed = await WriteParts(directory, Frame(other, data), 40, "other", "abcdef012345");
        await Throws<InvalidDataException>(() => ConversionService.CreateRestorePlanAsync(paths.Skip(1).Prepend(mixed[0])));
        string old = Path.Combine(directory, $"old.{SetId}.part000001-of-000001.txt");
        await File.WriteAllTextAsync(old, Convert.ToBase64String(data));
        await Throws<InvalidDataException>(() => ConversionService.CreateRestorePlanAsync([old]));
        string badIndex = Path.Combine(directory, $"bad.{SetId}.v2.part000000-of-000001.txt");
        await File.WriteAllTextAsync(badIndex, "AAAA");
        await Throws<InvalidDataException>(() => ConversionService.CreateRestorePlanAsync([badIndex]));
        string empty = Path.Combine(directory, $"empty.{SetId}.v2.part000001-of-000001.txt");
        await File.WriteAllBytesAsync(empty, []);
        await Throws<InvalidDataException>(() => ConversionService.CreateRestorePlanAsync([empty]));
        await File.AppendAllTextAsync(paths[0], "A");
        await Throws<InvalidDataException>(() => ConversionService.CreateRestorePlanAsync(paths));
        File.Delete(paths[0]);
        await Throws<FileNotFoundException>(() => ConversionService.CreateRestorePlanAsync(paths));
    }

    internal static async Task InvalidPackages(string directory)
    {
        byte[] original = [1, 2, 3];
        var mutations = new (string Name, Action<JsonObject> Mutate)[]
        {
            ("version", m => m["FormatVersion"] = 3),
            ("setid", m => m["SetId"] = "ffffffffffff"),
            ("setid_shape", m => m["SetId"] = "invalid"),
            ("unknown_method", m => m["Compression"] = "zstd"),
            ("numeric_method", m => m["Compression"] = 1),
            ("negative_original", m => m["OriginalLength"] = -1),
            ("negative_payload", m => m["PayloadLength"] = -1),
            ("overflow", m => { m["OriginalLength"] = long.MaxValue; m["PayloadLength"] = long.MaxValue; }),
            ("bad_sha", m => m["Sha256"] = new string('Z', 64)),
            ("null_sha", m => m["Sha256"] = null),
            ("short_sha", m => m["Sha256"] = "ABCD"),
            ("null_name", m => m["OriginalFileName"] = null),
            ("parent_path", m => m["OriginalFileName"] = "../escape.bin"),
            ("absolute_path", m => m["OriginalFileName"] = "C:\\escape.bin"),
            ("reserved_name", m => m["OriginalFileName"] = "NUL.txt"),
            ("trailing_dot", m => m["OriginalFileName"] = "name."),
            ("trailing_space", m => m["OriginalFileName"] = "name "),
            ("control_name", m => m["OriginalFileName"] = "bad\0name"),
            ("long_name", m => m["OriginalFileName"] = new string('x', 256)),
            ("none_mismatch", m => m["OriginalLength"] = 4),
            ("total_mismatch", m => { m["OriginalLength"] = 6; m["PayloadLength"] = 6; }),
            ("empty_brotli", m => { m["Compression"] = "brotli"; m["PayloadLength"] = 0; }),
            ("unknown_field", m => m["Unexpected"] = true)
        };
        foreach (var mutation in mutations)
        {
            JsonObject metadata = Metadata(original, original);
            mutation.Mutate(metadata);
            await RejectPackage(directory, mutation.Name, Frame(metadata, original));
        }
        foreach (string key in Metadata(original, original).Select(p => p.Key).ToArray())
        {
            var metadata = Metadata(original, original);
            metadata.Remove(key);
            await RejectPackage(directory, "missing_" + key, Frame(metadata, original));
        }
        byte[] valid = Frame(Metadata(original, original), original);
        byte[] badMagic = (byte[])valid.Clone();
        badMagic[0] ^= 1;
        await RejectPackage(directory, "bad_magic", badMagic);
        await RejectPackage(directory, "short_prefix", valid[..7]);
        await RejectPackage(directory, "short_header", valid[..20]);
        foreach (uint length in new uint[] { 0, 65_537, uint.MaxValue })
        {
            byte[] bad = (byte[])valid.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(8, 4), length);
            await RejectPackage(directory, "header_length_" + length, bad);
        }
        byte[] invalidJson = (byte[])valid.Clone();
        invalidJson[12] = 0xFF;
        await RejectPackage(directory, "invalid_json", invalidJson);
    }

    internal static async Task InvalidBase64(string directory)
    {
        byte[] data = Bytes(30);
        string encoded = Convert.ToBase64String(Frame(Metadata(data, data), data));
        var cases = new[]
        {
            "!" + encoded[1..], "=" + encoded[1..], "\n" + encoded[1..],
            "\uFEFF" + encoded[1..], encoded[..^4] + "===="
        };
        for (int i = 0; i < cases.Length; i++)
        {
            string path = Path.Combine(directory, $"invalid{i}.{SetId}.v2.part000001-of-000001.txt");
            await File.WriteAllTextAsync(path, cases[i], new UTF8Encoding(false));
            await Throws<InvalidDataException>(() => ConversionService.CreateRestorePlanAsync([path]));
        }
        // Padding in an earlier part is forbidden even when the individual quartet decodes.
        string[] parts = await WriteParts(directory, Frame(Metadata(data, data), data), 40, "padding");
        string first = await File.ReadAllTextAsync(parts[0]);
        await File.WriteAllTextAsync(parts[0], first[..^4] + "AA==", Encoding.ASCII);
        await Throws<InvalidDataException>(() => ConversionService.CreateRestorePlanAsync(parts));
    }

    internal static async Task CorruptPayloads(string directory)
    {
        byte[] original = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("integrity-complete-stream\n", 20_000)));
        byte[] packed = Compress(original);
        for (int trim = 1; trim <= 3; trim++)
        {
            byte[] truncated = packed[..^trim];
            await RejectPackage(directory, "truncated_" + trim, Frame(Metadata(original, truncated, "brotli"), truncated));
        }
        foreach (byte[] suffix in new byte[][] { [0], [1, 2, 3], Compress([9, 8, 7]) })
        {
            byte[] trailing = [.. packed, .. suffix];
            await RejectPackage(directory, "trailing_" + suffix.Length, Frame(Metadata(original, trailing, "brotli"), trailing));
        }
        var shortOutput = Metadata(original, packed, "brotli");
        shortOutput["OriginalLength"] = original.LongLength - 1;
        await RejectPackage(directory, "output_overflow", Frame(shortOutput, packed));
        var longOutput = Metadata(original, packed, "brotli");
        longOutput["OriginalLength"] = original.LongLength + 1;
        await RejectPackage(directory, "output_short", Frame(longOutput, packed));
        byte[] raw = Bytes(100);
        byte[] changed = (byte[])raw.Clone();
        changed[0] ^= 1;
        await RejectPackage(directory, "raw_checksum", Frame(Metadata(raw, changed), changed));
        var wrongHash = Metadata(original, packed, "brotli");
        wrongHash["Sha256"] = new string('0', 64);
        await RejectPackage(directory, "brotli_checksum", Frame(wrongHash, packed));
    }

    internal static async Task ChangedHeader(string directory)
    {
        byte[] data = Bytes(500);
        JsonObject metadata = Metadata(data, data);
        string[] paths = await WriteParts(directory, Frame(metadata, data));
        RestorePlan plan = await ConversionService.CreateRestorePlanAsync(paths);
        long length = new FileInfo(paths[0]).Length;
        metadata["OriginalFileName"] = "modified.bin"; // Same UTF-8 length as original.bin.
        await File.WriteAllTextAsync(paths[0], Convert.ToBase64String(Frame(metadata, data)), Encoding.ASCII);
        Check(new FileInfo(paths[0]).Length == length, "Mutation must preserve the recorded part length.");
        string output = Path.Combine(directory, "existing.bin");
        await File.WriteAllTextAsync(output, "keep");
        await Throws<InvalidDataException>(() => ConversionService.RestoreAsync(plan, output, true));
        Check(await File.ReadAllTextAsync(output) == "keep", "Changed header replaced an existing file.");
        await File.AppendAllTextAsync(paths[0], "AAAA");
        await Throws<IOException>(() => ConversionService.RestoreAsync(plan, output, true));
        NoTemporaryFiles(directory);
    }

    internal static async Task OverwriteProtection(string directory)
    {
        byte[] original = Bytes(200);
        string[] paths = await WriteParts(directory, Frame(Metadata(original, original), original));
        RestorePlan plan = await ConversionService.CreateRestorePlanAsync(paths);
        byte[] partBefore = await File.ReadAllBytesAsync(paths[0]);
        await Throws<IOException>(() => ConversionService.RestoreAsync(plan, paths[0], true));
        Check((await File.ReadAllBytesAsync(paths[0])).SequenceEqual(partBefore), "Input part was overwritten.");
        string output = Path.Combine(directory, "existing.bin");
        await File.WriteAllTextAsync(output, "keep");
        await Throws<IOException>(() => ConversionService.RestoreAsync(plan, output, false));
        Check(await File.ReadAllTextAsync(output) == "keep", "Unapproved overwrite changed the output.");
        await ConversionService.RestoreAsync(plan, output, true);
        Check((await File.ReadAllBytesAsync(output)).SequenceEqual(original), "Approved overwrite did not restore the data.");
        NoTemporaryFiles(directory);
    }

    internal static async Task ExportRollback(string directory)
    {
        string source = Path.Combine(directory, "source.bin");
        byte[] original = Bytes(300);
        await File.WriteAllBytesAsync(source, original);
        var io = new ControlledIo { FailMove = (from, _) => Path.GetFileName(from) == "part-000001" };
        await using var plan = await ConversionService.PrepareAsync(source, directory, 40, io);
        await File.WriteAllTextAsync(plan.PartPaths[0], "old first");
        await File.WriteAllTextAsync(plan.PartPaths[1], "old second");
        await Throws<IOException>(() => ConversionService.ExportAsync(plan, plan.PartPaths.Take(2)));
        Check(await File.ReadAllTextAsync(plan.PartPaths[0]) == "old first", "First overwritten file was not rolled back.");
        Check(await File.ReadAllTextAsync(plan.PartPaths[1]) == "old second", "Second backup was not restored.");
        Check(plan.PartPaths.Skip(2).All(p => !File.Exists(p)), "A partial new output survived rollback.");
        NoTemporaryFiles(directory);
        // Unapproved existing outputs are also protected after earlier files were committed.
        await using var unapproved = await ConversionService.CreateExportPlanAsync(source, directory, 40);
        await File.WriteAllTextAsync(unapproved.PartPaths[1], "unapproved");
        await Throws<IOException>(() => ConversionService.ExportAsync(unapproved, []));
        Check(!File.Exists(unapproved.PartPaths[0]) && await File.ReadAllTextAsync(unapproved.PartPaths[1]) == "unapproved", "Unapproved overwrite rollback failed.");
        NoTemporaryFiles(directory);
    }

    internal static async Task RollbackFailure(string directory)
    {
        string source = Path.Combine(directory, "source.bin");
        await File.WriteAllBytesAsync(source, Bytes(100));
        var io = new ControlledIo { FailMove = (from, _) => Path.GetFileName(from) is "part-000001" or "backup-000000" };
        var plan = await ConversionService.PrepareAsync(source, directory, 40, io);
        await File.WriteAllTextAsync(plan.PartPaths[0], "recover this original");
        IOException error = await Throws<IOException>(() => ConversionService.ExportAsync(plan, [plan.PartPaths[0]]));
        await plan.DisposeAsync();
        Check(error.Message.Contains(plan.Stage, StringComparison.Ordinal), "Recovery location was not reported.");
        Check(error.InnerException is AggregateException aggregate && aggregate.InnerExceptions.Count >= 2, "Commit and recovery errors were not preserved.");
        Check(Directory.Exists(plan.Stage), "Disposal deleted the recovery directory.");
        Check(await File.ReadAllTextAsync(Path.Combine(plan.Stage, "backup-000000")) == "recover this original", "Original backup was lost.");
        using var writer = File.OpenWrite(source);
        Check(writer.CanWrite, "Keeping backups also kept the source locked.");
    }

    internal static async Task CleanupFailures(string directory)
    {
        string source = Path.Combine(directory, "source.bin");
        await File.WriteAllBytesAsync(source, Bytes(100));
        var io = new ControlledIo { FailWrite = p => Path.GetFileName(p).StartsWith("part-", StringComparison.Ordinal) };
        var plan = await ConversionService.PrepareAsync(source, directory, 1000, io);
        io.FailCleanup = true;
        IOException error = await Throws<IOException>(() => ConversionService.ExportAsync(plan, []));
        Check(error.Message.Contains("simulated disk full", StringComparison.Ordinal) && error.Message.Contains(plan.Stage, StringComparison.Ordinal), "Primary error or leftover path was hidden.");
        Check(error.InnerException is AggregateException, "Cleanup replaced the primary exception.");
        Check(Directory.Exists(plan.Stage), "Cleanup failure was not exercised.");
        using (var writer = File.OpenWrite(source)) Check(writer.CanWrite, "Failed cleanup kept the source open.");
        Check(plan.PartPaths.All(p => !File.Exists(p)), "Failed encoding published final parts.");
        io.FailCleanup = false;
        io.DeleteDirectory(plan.Stage);
        NoTemporaryFiles(directory);
        // Failure while preparing must clean up before a plan can be returned.
        io.FailWrite = _ => true;
        await Throws<IOException>(() => ConversionService.PrepareAsync(source, directory, 1000, io));
        NoTemporaryFiles(directory);
        byte[] data = Bytes(50);
        string[] paths = await WriteParts(directory, Frame(Metadata(data, data), data));
        RestorePlan restore = await ConversionService.CreateRestorePlanAsync(paths);
        string output = Path.Combine(directory, "existing.bin");
        await File.WriteAllTextAsync(output, "keep");
        await Throws<IOException>(() => ConversionService.RestoreWithFilesAsync(restore, output, true, io));
        Check(await File.ReadAllTextAsync(output) == "keep", "Failed restore write changed existing output.");
        NoTemporaryFiles(directory);
    }

    internal static async Task DiskSpace(string directory)
    {
        string source = Path.Combine(directory, "source.bin");
        await File.WriteAllBytesAsync(source, Bytes(50));
        var io = new ControlledIo { FreeSpace = 0 };
        await Throws<IOException>(() => ConversionService.PrepareAsync(source, directory, 1000, io));
        NoTemporaryFiles(directory);
        io.FreeSpace = long.MaxValue;
        var plan = await ConversionService.PrepareAsync(source, directory, 1000, io);
        io.FreeSpace = 0;
        await Throws<IOException>(() => ConversionService.ExportAsync(plan, []));
        Check(plan.PartPaths.All(p => !File.Exists(p)), "Space failure published output.");
        NoTemporaryFiles(directory);
        byte[] data = Bytes(50);
        string[] parts = await WriteParts(directory, Frame(Metadata(data, data), data));
        RestorePlan restore = await ConversionService.CreateRestorePlanAsync(parts);
        string output = Path.Combine(directory, "existing.bin");
        await File.WriteAllTextAsync(output, "keep");
        await Throws<IOException>(() => ConversionService.RestoreWithFilesAsync(restore, output, true, io));
        Check(await File.ReadAllTextAsync(output) == "keep", "Space precheck replaced output.");
        NoTemporaryFiles(directory);
        io.FreeSpace = null; // Network paths may not expose free-space information.
        await using var unknownSpace = await ConversionService.PrepareAsync(source, directory, 1000, io);
        await ConversionService.ExportAsync(unknownSpace, []);
        await VerifyRestored(directory, unknownSpace.PartPaths, data);
    }

    internal static async Task Cancellation(string directory)
    {
        string source = Path.Combine(directory, "source.bin");
        byte[] data = Bytes(300_001);
        await File.WriteAllBytesAsync(source, data);
        using (var cts = new CancellationTokenSource())
        {
            var progress = new SynchronousProgress(_ => cts.Cancel());
            await Throws<OperationCanceledException>(() => ConversionService.CreateExportPlanAsync(source, directory, 1000, progress, cts.Token));
            NoTemporaryFiles(directory);
        }
        using (var cts = new CancellationTokenSource())
        {
            var io = new ControlledIo { OnWrite = _ => cts.Cancel() };
            await Throws<OperationCanceledException>(() => ConversionService.PrepareAsync(source, directory, 1000, io, cancellationToken: cts.Token));
            NoTemporaryFiles(directory);
        }
        using (var cts = new CancellationTokenSource())
        {
            var io = new ControlledIo();
            var plan = await ConversionService.PrepareAsync(source, directory, 100_000, io);
            io.OnWrite = _ => cts.Cancel();
            await Throws<OperationCanceledException>(() => ConversionService.ExportAsync(plan, [], cancellationToken: cts.Token));
            Check(plan.PartPaths.All(p => !File.Exists(p)), "Cancelled encoding published parts.");
            NoTemporaryFiles(directory);
        }
        using (var cts = new CancellationTokenSource())
        {
            var plan = await ConversionService.CreateExportPlanAsync(source, directory, 100_000);
            await File.WriteAllTextAsync(plan.PartPaths[0], "old");
            var progress = new SynchronousProgress(p => { if (p.Message == "正在提交分片" && p.ProcessedBytes == plan.PartByteLimit) cts.Cancel(); });
            await Throws<OperationCanceledException>(() => ConversionService.ExportAsync(plan, [plan.PartPaths[0]], progress, cts.Token));
            Check(await File.ReadAllTextAsync(plan.PartPaths[0]) == "old" && plan.PartPaths.Skip(1).All(p => !File.Exists(p)), "Cancelled commit did not restore existing output.");
            NoTemporaryFiles(directory);
        }
        string[] paths = await WriteParts(directory, Frame(Metadata(data, data), data));
        RestorePlan restore = await ConversionService.CreateRestorePlanAsync(paths);
        foreach (string stage in new[] { "正在解碼與還原", "正在驗證完整性", "正在儲存還原檔案" })
        {
            using var cts = new CancellationTokenSource();
            string output = Path.Combine(directory, "existing.bin");
            await File.WriteAllTextAsync(output, "keep");
            var progress = new SynchronousProgress(p => { if (p.Message == stage) cts.Cancel(); });
            await Throws<OperationCanceledException>(() => ConversionService.RestoreAsync(restore, output, true, progress, cts.Token));
            Check(await File.ReadAllTextAsync(output) == "keep", "Cancelled restore replaced existing output.");
            NoTemporaryFiles(directory);
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Throws<OperationCanceledException>(() => ConversionService.CreateRestorePlanAsync(paths, cancelled.Token));
    }

    private static async Task RejectPackage(string directory, string name, byte[] frame)
    {
        string[] paths = await WriteParts(directory, frame, prefix: name);
        string output = Path.Combine(directory, "preserve.bin");
        await File.WriteAllTextAsync(output, "do not replace");
        await Throws<InvalidDataException>(async () =>
        {
            RestorePlan restore = await ConversionService.CreateRestorePlanAsync(paths);
            await ConversionService.RestoreAsync(restore, output, true);
        });
        Check(await File.ReadAllTextAsync(output) == "do not replace", "Invalid package replaced an existing output.");
        NoTemporaryFiles(directory);
    }
}

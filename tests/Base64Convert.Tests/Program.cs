using System.Text;
using System.Text.Json;
using Base64Convert.Core;

var tests = new (string Name, Func<string, Task> Run)[]
{
    ("round trips and aligned split sizes", RoundTrips),
    ("large buffered streams", LargeStreams),
    ("default 15 MB boundary", DefaultSizeBoundary),
    ("missing, duplicate and mixed parts", InvalidPartSets),
    ("invalid Base64 and verification failure preserve output", InvalidContents),
    ("manifest validation", InvalidManifest),
    ("explicit overwrite and input protection", OverwriteAndInputProtection),
    ("export rollback protects existing files", ExportRollback),
    ("cancellation removes temporary output", Cancellation)
};

string root = Path.Combine(Path.GetTempPath(), "Base64Convert-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int failed = 0;
try
{
    for (int i = 0; i < tests.Length; i++)
    {
        string directory = Path.Combine(root, $"test-{i}");
        Directory.CreateDirectory(directory);
        try
        {
            await tests[i].Run(directory);
            Console.WriteLine($"PASS {tests[i].Name}");
        }
        catch (Exception ex)
        {
            failed++;
            Console.Error.WriteLine($"FAIL {tests[i].Name}: {ex}");
        }
    }
}
finally
{
    try { Directory.Delete(root, true); }
    catch (IOException) { Console.Error.WriteLine($"Unable to remove test files: {root}"); }
    catch (UnauthorizedAccessException) { Console.Error.WriteLine($"Unable to remove test files: {root}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed.");
return failed == 0 ? 0 : 1;

static async Task RoundTrips(string dir)
{
    foreach (int size in new[] { 0, 1, 2, 3, 4, 29, 30, 31, 59, 60, 61, 1001 })
    {
        foreach (bool withInfo in new[] { false, true })
        {
            string input = Path.Combine(dir, $"中文 # 測試 {size} {withInfo}.bin");
            byte[] data = Bytes(size);
            await File.WriteAllBytesAsync(input, data);
            // 43 bytes aligns down to a 40-byte output boundary (30 input bytes).
            ExportPlan export = ConversionService.CreateExportPlan(input, dir, 43, withInfo);
            await ConversionService.ExportAsync(export, []);
            Check(export.PartByteLimit == 40, "Output limit must be aligned down.");
            Check(export.PartPaths.Count == Math.Max(1, (size + 29) / 30), "Unexpected part count.");
            foreach (string part in export.PartPaths)
            {
                var info = new FileInfo(part);
                Check(info.Length <= 43 && info.Length % 4 == 0, "Part violates the size or alignment limit.");
            }
            string encoded = string.Concat(await Task.WhenAll(export.PartPaths.Select(path => File.ReadAllTextAsync(path))));
            Check(encoded == Convert.ToBase64String(data), "Parts do not contain the expected Base64 text.");
            Check((export.ManifestPath is not null) == withInfo, "Unexpected verification file state.");
            if (withInfo)
                Check(ConversionService.FindVerificationInfo(export.PartPaths[0]) == export.ManifestPath, "Manifest not found automatically.");
            else
                Check(ConversionService.FindVerificationInfo(export.PartPaths[0]) is null, "Unexpected manifest discovery.");

            RestorePlan restore = await ConversionService.CreateRestorePlanAsync(export.PartPaths.Reverse(), export.ManifestPath);
            Check(restore.SuggestedFileName == Path.GetFileName(input), "Original file name was not preserved.");
            string output = Path.Combine(dir, $"restored-{size}-{withInfo}.bin");
            await ConversionService.RestoreAsync(restore, output, false);
            Check((await File.ReadAllBytesAsync(output)).SequenceEqual(data), "Restored bytes differ.");
        }
    }
}

static async Task LargeStreams(string dir)
{
    byte[] data = Bytes(850_019);
    string input = Path.Combine(dir, "large.dat");
    await File.WriteAllBytesAsync(input, data);
    foreach (long limit in new long[] { 100_004, 2_000_000 })
    {
        ExportPlan export = ConversionService.CreateExportPlan(input, dir, limit, true);
        await ConversionService.ExportAsync(export, []);
        Check(export.PartPaths.All(p => new FileInfo(p).Length <= limit), "Large part exceeds limit.");
        RestorePlan restore = await ConversionService.CreateRestorePlanAsync(export.PartPaths.Reverse(), export.ManifestPath);
        string output = Path.Combine(dir, $"large-{limit}.out");
        await ConversionService.RestoreAsync(restore, output, false);
        Check((await File.ReadAllBytesAsync(output)).SequenceEqual(data), "Large-file round trip failed.");
    }
}

static async Task DefaultSizeBoundary(string dir)
{
    // One more input byte than fits in a 15,000,000-byte Base64 part.
    byte[] data = Bytes(11_250_001);
    string input = Path.Combine(dir, "default-limit.bin");
    await File.WriteAllBytesAsync(input, data);
    ExportPlan export = ConversionService.CreateExportPlan(input, dir, 15_000_000, true);
    await ConversionService.ExportAsync(export, []);
    Check(export.PartPaths.Count == 2, "Expected two parts at the 15 MB boundary.");
    Check(new FileInfo(export.PartPaths[0]).Length == 15_000_000, "The first part should fill the 15 MB limit.");
    Check(new FileInfo(export.PartPaths[1]).Length == 4, "The remaining byte should use four Base64 bytes.");
    RestorePlan restore = await ConversionService.CreateRestorePlanAsync(export.PartPaths, export.ManifestPath);
    string output = Path.Combine(dir, "default-limit-restored.bin");
    await ConversionService.RestoreAsync(restore, output, false);
    Check((await File.ReadAllBytesAsync(output)).SequenceEqual(data), "The 15 MB boundary round trip failed.");
}

static async Task InvalidPartSets(string dir)
{
    string input = Path.Combine(dir, "source.bin");
    await File.WriteAllBytesAsync(input, Bytes(100));
    ExportPlan first = ConversionService.CreateExportPlan(input, dir, 40, false);
    ExportPlan second = ConversionService.CreateExportPlan(input, dir, 40, false);
    await ConversionService.ExportAsync(first, []);
    await ConversionService.ExportAsync(second, []);
    await Throws<InvalidDataException>(() => ConversionService.CreateRestorePlanAsync(first.PartPaths.Skip(1)));
    await Throws<InvalidDataException>(() => ConversionService.CreateRestorePlanAsync(first.PartPaths.Append(first.PartPaths[0])));
    await Throws<InvalidDataException>(() => ConversionService.CreateRestorePlanAsync(first.PartPaths.Skip(1).Prepend(second.PartPaths[0])));
    await Throws<InvalidDataException>(() => ConversionService.CreateRestorePlanAsync([input]));
    File.Delete(first.PartPaths[0]);
    await Throws<FileNotFoundException>(() => ConversionService.CreateRestorePlanAsync(first.PartPaths));
}

static async Task InvalidContents(string dir)
{
    string input = Path.Combine(dir, "source.bin");
    byte[] data = Bytes(100);
    await File.WriteAllBytesAsync(input, data);
    ExportPlan export = ConversionService.CreateExportPlan(input, dir, 40, true);
    await ConversionService.ExportAsync(export, []);
    RestorePlan verified = await ConversionService.CreateRestorePlanAsync(export.PartPaths, export.ManifestPath);
    string output = Path.Combine(dir, "existing.out");
    await File.WriteAllTextAsync(output, "do not replace");
    string first = await File.ReadAllTextAsync(export.PartPaths[0]);

    await File.WriteAllTextAsync(export.PartPaths[0], "!" + first[1..], Encoding.ASCII);
    await Throws<InvalidDataException>(() => ConversionService.RestoreAsync(verified, output, true));
    Check(await File.ReadAllTextAsync(output) == "do not replace", "Invalid input overwrote the destination.");

    await File.WriteAllTextAsync(export.PartPaths[0], "=" + first[1..], Encoding.ASCII);
    await Throws<InvalidDataException>(() => ConversionService.RestoreAsync(verified, output, true));
    Check(await File.ReadAllTextAsync(output) == "do not replace", "Bad padding overwrote the destination.");

    await File.WriteAllTextAsync(export.PartPaths[0], (first[0] == 'A' ? "B" : "A") + first[1..], Encoding.ASCII);
    await Throws<InvalidDataException>(() => ConversionService.RestoreAsync(verified, output, true));
    Check(await File.ReadAllTextAsync(output) == "do not replace", "A hash mismatch overwrote the destination.");
    RestorePlan unverified = await ConversionService.CreateRestorePlanAsync(export.PartPaths);
    await ConversionService.RestoreAsync(unverified, output, true);
    Check(!(await File.ReadAllBytesAsync(output)).SequenceEqual(data), "The unverified test did not modify the data.");
    CheckNoTemporaryFiles(dir);
}

static async Task InvalidManifest(string dir)
{
    string input = Path.Combine(dir, "source.bin");
    await File.WriteAllBytesAsync(input, Bytes(12));
    ExportPlan export = ConversionService.CreateExportPlan(input, dir, 40, true);
    await ConversionService.ExportAsync(export, []);
    string manifest = export.ManifestPath!;
    string original = await File.ReadAllTextAsync(manifest);
    var info = JsonSerializer.Deserialize<VerificationInfo>(original)!;
    await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(info with { PartCount = 2 }));
    await Throws<InvalidDataException>(() => ConversionService.CreateRestorePlanAsync(export.PartPaths, manifest));
    await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(info with { Sha256 = "not a hash" }));
    await Throws<InvalidDataException>(() => ConversionService.CreateRestorePlanAsync(export.PartPaths, manifest));
    await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(info with { OriginalFileName = "../unsafe.bin" }));
    await Throws<InvalidDataException>(() => ConversionService.CreateRestorePlanAsync(export.PartPaths, manifest));
    await File.WriteAllTextAsync(manifest, "{}");
    await Throws<InvalidDataException>(() => ConversionService.CreateRestorePlanAsync(export.PartPaths, manifest));
    await File.WriteAllTextAsync(manifest, "not json");
    await Throws<InvalidDataException>(() => ConversionService.CreateRestorePlanAsync(export.PartPaths, manifest));
    await File.WriteAllTextAsync(manifest, original);
    RestorePlan restore = await ConversionService.CreateRestorePlanAsync(export.PartPaths, manifest);
    string output = Path.Combine(dir, "verified.out");
    await ConversionService.RestoreAsync(restore, output, false);
    Check((await File.ReadAllBytesAsync(output)).SequenceEqual(Bytes(12)), "Valid manifest did not work.");
}

static async Task OverwriteAndInputProtection(string dir)
{
    string input = Path.Combine(dir, "source.bin");
    byte[] bytes = Bytes(50);
    await File.WriteAllBytesAsync(input, bytes);
    ExportPlan export = ConversionService.CreateExportPlan(input, dir, 40, true);
    await ConversionService.ExportAsync(export, []);
    RestorePlan restore = await ConversionService.CreateRestorePlanAsync(export.PartPaths, export.ManifestPath);
    string output = Path.Combine(dir, "existing.out");
    await File.WriteAllTextAsync(output, "original");
    await Throws<IOException>(() => ConversionService.RestoreAsync(restore, output, false));
    Check(await File.ReadAllTextAsync(output) == "original", "Unapproved overwrite changed the file.");
    await Throws<IOException>(() => ConversionService.RestoreAsync(restore, export.PartPaths[0], true));
    await Throws<IOException>(() => ConversionService.RestoreAsync(restore, export.ManifestPath!, true));
    await ConversionService.RestoreAsync(restore, output, true);
    Check((await File.ReadAllBytesAsync(output)).SequenceEqual(bytes), "Approved overwrite failed.");
    CheckNoTemporaryFiles(dir);
}

static async Task ExportRollback(string dir)
{
    string input = Path.Combine(dir, "source.bin");
    await File.WriteAllBytesAsync(input, Bytes(100));
    ExportPlan export = ConversionService.CreateExportPlan(input, dir, 40, true);
    await File.WriteAllTextAsync(export.PartPaths[0], "old first");
    await File.WriteAllTextAsync(export.PartPaths[1], "old second");
    await Throws<IOException>(() => ConversionService.ExportAsync(export, [export.PartPaths[0]]));
    Check(await File.ReadAllTextAsync(export.PartPaths[0]) == "old first", "First overwritten file was not restored.");
    Check(await File.ReadAllTextAsync(export.PartPaths[1]) == "old second", "Unapproved second file was changed.");
    Check(!File.Exists(export.PartPaths[2]), "Partial new output remained after rollback.");
    CheckNoTemporaryFiles(dir);
    await ConversionService.ExportAsync(export, [export.PartPaths[0], export.PartPaths[1]]);
    RestorePlan restore = await ConversionService.CreateRestorePlanAsync(export.PartPaths, export.ManifestPath);
    string output = Path.Combine(dir, "result.bin");
    await ConversionService.RestoreAsync(restore, output, false);
    Check((await File.ReadAllBytesAsync(output)).SequenceEqual(Bytes(100)), "Export after approved overwrite failed.");
    CheckNoTemporaryFiles(dir);
}

static async Task Cancellation(string dir)
{
    string input = Path.Combine(dir, "source.bin");
    await File.WriteAllBytesAsync(input, Bytes(300_001));
    ExportPlan export = ConversionService.CreateExportPlan(input, dir, 100_000, true);
    await File.WriteAllTextAsync(export.PartPaths[0], "original");
    using (var cts = new CancellationTokenSource())
    {
        var progress = new SynchronousProgress(value => { if (value.ProcessedBytes > 0) cts.Cancel(); });
        await Throws<OperationCanceledException>(() => ConversionService.ExportAsync(export, [export.PartPaths[0]], progress, cts.Token));
    }
    Check(await File.ReadAllTextAsync(export.PartPaths[0]) == "original", "Cancellation damaged existing output.");
    Check(export.PartPaths.Skip(1).All(p => !File.Exists(p)), "Cancellation left exported parts.");
    Check(!File.Exists(export.ManifestPath), "Cancellation left a manifest.");
    CheckNoTemporaryFiles(dir);

    await ConversionService.ExportAsync(export, [export.PartPaths[0]]);
    RestorePlan restore = await ConversionService.CreateRestorePlanAsync(export.PartPaths, export.ManifestPath);
    string output = Path.Combine(dir, "existing.out");
    await File.WriteAllTextAsync(output, "original destination");
    using (var cts = new CancellationTokenSource())
    {
        var progress = new SynchronousProgress(_ => cts.Cancel());
        await Throws<OperationCanceledException>(() => ConversionService.RestoreAsync(restore, output, true, progress, cts.Token));
    }
    Check(await File.ReadAllTextAsync(output) == "original destination", "Cancellation damaged the restore destination.");
    CheckNoTemporaryFiles(dir);
}

static byte[] Bytes(int length)
{
    var data = new byte[length];
    new Random(123456).NextBytes(data);
    return data;
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void CheckNoTemporaryFiles(string directory)
{
    Check(!Directory.EnumerateFileSystemEntries(directory, ".base64convert-*.tmp").Any(), "Temporary output was left behind.");
}

static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

sealed class SynchronousProgress(Action<ConversionProgress> report) : IProgress<ConversionProgress>
{
    public void Report(ConversionProgress value) => report(value);
}

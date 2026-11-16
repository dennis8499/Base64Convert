using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Base64Convert.Core;

namespace Base64Convert.Tests;

internal static class Performance
{
    internal static async Task<int> RunAsync()
    {
        foreach (var sample in new[] { ("csv", 10), ("csv", 100), ("random", 10), ("random", 100), ("zip", 10) })
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--measure");
            start.ArgumentList.Add(sample.Item1);
            start.ArgumentList.Add(sample.Item2.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var child = Process.Start(start)!;
            Task<string> stdout = child.StandardOutput.ReadToEndAsync();
            Task<string> stderr = child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync();
            Console.Write(await stdout);
            if (child.ExitCode != 0) { Console.Error.Write(await stderr); return child.ExitCode; }
        }
        return 0;
    }

    internal static async Task<int> MeasureAsync(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "Base64Convert-perf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string kind = args[1];
            int mb = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
            string source = Path.Combine(root, kind == "zip" ? "source.zip" : "source.bin");
            await using (var file = File.Create(source))
            {
                if (kind == "zip")
                {
                    using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
                    await using var entry = archive.CreateEntry("random.bin", CompressionLevel.Optimal).Open();
                    await GenerateAsync(entry, mb * 1_000_000L, true);
                }
                else await GenerateAsync(file, mb * 1_000_000L, kind == "random");
            }
            var clock = Stopwatch.StartNew();
            await using var plan = await ConversionService.CreateExportPlanAsync(source, root, 15_000_000);
            double prepareMs = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            ExportResult result = await ConversionService.ExportAsync(plan, []);
            double exportMs = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            RestorePlan restore = await ConversionService.CreateRestorePlanAsync(plan.PartPaths);
            string restored = Path.Combine(root, "restored.bin");
            await ConversionService.RestoreAsync(restore, restored, false);
            double restoreMs = clock.Elapsed.TotalMilliseconds;
            await using var a = File.OpenRead(source);
            await using var b = File.OpenRead(restored);
            byte[] sourceHash = await SHA256.HashDataAsync(a);
            byte[] outputHash = await SHA256.HashDataAsync(b);
            TestSupport.Check(sourceHash.SequenceEqual(outputHash), "Performance sample failed integrity verification.");
            using var current = Process.GetCurrentProcess();
            current.Refresh();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Sample = kind, Megabytes = mb, result.OriginalBytes, result.PayloadBytes, result.EncodedBytes,
                result.UncompressedEncodedBytes, Method = result.Compression.ToString(), result.PartCount,
                PrepareMilliseconds = Math.Round(prepareMs, 1), ExportMilliseconds = Math.Round(exportMs, 1),
                RestoreMilliseconds = Math.Round(restoreMs, 1), PeakWorkingSetBytes = current.PeakWorkingSet64
            }));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task GenerateAsync(Stream output, long length, bool random)
    {
        var buffer = new byte[64 * 1024];
        var generator = new Random(123456);
        byte[] row = Encoding.UTF8.GetBytes("2026-11-16,42,Base64Convert,中文測試資料,repeatable CSV data\n");
        for (int i = 0; i < buffer.Length; i++) buffer[i] = row[i % row.Length];
        while (length > 0)
        {
            int count = (int)Math.Min(length, buffer.Length);
            if (random) generator.NextBytes(buffer);
            await output.WriteAsync(buffer.AsMemory(0, count));
            length -= count;
        }
    }
}

namespace Base64Convert.Tests;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--measure") return await Performance.MeasureAsync(args);
        if (args.Length > 0 && args[0] == "--performance") return await Performance.RunAsync();
        var tests = new (string Name, Func<string, Task> Run)[]
        {
            (nameof(CoreTests.RoundTrips), CoreTests.RoundTrips),
            (nameof(CoreTests.CompressionSelection), CoreTests.CompressionSelection),
            (nameof(CoreTests.IndependentDecoding), CoreTests.IndependentDecoding),
            (nameof(CoreTests.SplitBoundaries), CoreTests.SplitBoundaries),
            (nameof(CoreTests.HeaderBounds), CoreTests.HeaderBounds),
            (nameof(CoreTests.DefaultExportBoundary), CoreTests.DefaultExportBoundary),
            (nameof(CoreTests.LargeBufferedStreams), CoreTests.LargeBufferedStreams),
            (nameof(CoreTests.Lifecycle), CoreTests.Lifecycle),
            (nameof(CoreTests.InvalidArguments), CoreTests.InvalidArguments),
            (nameof(CoreTests.InvalidPartSets), CoreTests.InvalidPartSets),
            (nameof(CoreTests.InvalidPackages), CoreTests.InvalidPackages),
            (nameof(CoreTests.InvalidBase64), CoreTests.InvalidBase64),
            (nameof(CoreTests.CorruptPayloads), CoreTests.CorruptPayloads),
            (nameof(CoreTests.ChangedHeader), CoreTests.ChangedHeader),
            (nameof(CoreTests.OverwriteProtection), CoreTests.OverwriteProtection),
            (nameof(CoreTests.ExportRollback), CoreTests.ExportRollback),
            (nameof(CoreTests.RollbackFailure), CoreTests.RollbackFailure),
            (nameof(CoreTests.CleanupFailures), CoreTests.CleanupFailures),
            (nameof(CoreTests.DiskSpace), CoreTests.DiskSpace),
            (nameof(CoreTests.Cancellation), CoreTests.Cancellation)
        };
        string root = Path.Combine(Path.GetTempPath(), "Base64Convert-v2-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int failed = 0;
        try
        {
            for (int i = 0; i < tests.Length; i++)
            {
                string directory = Path.Combine(root, $"test-{i:D2}");
                Directory.CreateDirectory(directory);
                try { await tests[i].Run(directory); Console.WriteLine($"PASS {tests[i].Name}"); }
                catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {tests[i].Name}: {ex}"); }
            }
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"Passed: {tests.Length - failed}, Failed: {failed}, Skipped: 0");
        return failed == 0 ? 0 : 1;
    }
}

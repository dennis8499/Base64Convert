using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Base64Convert.Tests")]

namespace Base64Convert.Core;

// Public entry points always use real operations; the internal overloads support controlled I/O failures.
internal class ConversionFileOperations
{
    internal static ConversionFileOperations Real { get; } = new();
    internal virtual Stream CreateOutput(string path, int bufferSize) => new FileStream(path,
        FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    internal virtual void MoveFile(string source, string destination, bool overwrite = false) => File.Move(source, destination, overwrite);
    internal virtual void DeleteFile(string path) => File.Delete(path);
    internal virtual void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
    internal virtual long? AvailableSpace(string directory)
    {
        try { return new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
    internal void EnsureSpace(string directory, long required)
    {
        if (AvailableSpace(directory) is long available && available < required)
            throw new IOException($"磁碟空間不足，還需要至少 {required:N0} 位元組可用空間。");
    }
}

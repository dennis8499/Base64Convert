using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Base64Convert.Core;

internal static class PackageFormat
{
    private const int MaximumHeaderBytes = 64 * 1024;
    private static ReadOnlySpan<byte> Magic => "B64CVT02"u8;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new CompressionConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    internal static long EncodedLength(long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return checked((length / 3 + (length % 3 == 0 ? 0 : 1)) * 4);
    }
    internal static byte[] CreateHeader(PackageMetadata metadata)
    {
        Validate(metadata);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions);
        if (json.Length > MaximumHeaderBytes) throw new InvalidDataException("容器標頭過大。");
        var header = new byte[12 + json.Length];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), (uint)json.Length);
        json.CopyTo(header, 12);
        return header;
    }
    internal static async Task<(PackageMetadata Metadata, byte[] Header)> ReadHeaderAsync(Stream stream, CancellationToken token)
    {
        try
        {
            var prefix = new byte[12];
            await stream.ReadExactlyAsync(prefix, token);
            if (!prefix.AsSpan(0, 8).SequenceEqual(Magic)) throw new InvalidDataException("不是支援的 v2 容器。");
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(8, 4));
            if (count == 0 || count > MaximumHeaderBytes) throw new InvalidDataException("容器標頭長度無效或超過 64 KiB。");
            var header = new byte[12 + (int)count];
            prefix.CopyTo(header, 0);
            await stream.ReadExactlyAsync(header.AsMemory(12), token);
            PackageMetadata? metadata = JsonSerializer.Deserialize<PackageMetadata>(header.AsSpan(12), JsonOptions);
            if (metadata is null) throw new InvalidDataException("容器標頭無效。");
            Validate(metadata);
            return (metadata, header);
        }
        catch (EndOfStreamException ex) { throw new InvalidDataException("容器標頭遭截斷。", ex); }
        catch (JsonException ex) { throw new InvalidDataException("容器 JSON 標頭無效或缺少必要欄位。", ex); }
    }
    private static void Validate(PackageMetadata metadata)
    {
        string name = metadata.OriginalFileName;
        if (metadata.FormatVersion != 2 || metadata.SetId is null || metadata.SetId.Length != 12 ||
            !metadata.SetId.All(Uri.IsHexDigit) || metadata.OriginalLength < 0 || metadata.PayloadLength < 0 ||
            !Enum.IsDefined(metadata.Compression) || metadata.Sha256 is null || metadata.Sha256.Length != 64 ||
            !metadata.Sha256.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(name) ||
            name.Length > 255 || name is "." or ".." || name.EndsWith(' ') || name.EndsWith('.') ||
            name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name != Path.GetFileName(name))
            throw new InvalidDataException("容器資訊無效、版本不受支援或檔名不安全。");
        string stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
            stem[3] is >= '1' and <= '9')) throw new InvalidDataException("原始檔名是系統保留名稱。");
        if ((metadata.Compression == CompressionMethod.None && metadata.PayloadLength != metadata.OriginalLength) ||
            (metadata.Compression == CompressionMethod.Brotli && metadata.PayloadLength == 0))
            throw new InvalidDataException("資料大小與壓縮方法不符。");
    }
    private sealed class CompressionConverter : JsonConverter<CompressionMethod>
    {
        public override CompressionMethod Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String ? reader.GetString() switch
            {
                "none" => CompressionMethod.None, "brotli" => CompressionMethod.Brotli,
                _ => throw new JsonException("不支援的壓縮方法。")
            } : throw new JsonException("壓縮方法必須是字串。");
        public override void Write(Utf8JsonWriter writer, CompressionMethod value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value switch
            {
                CompressionMethod.None => "none", CompressionMethod.Brotli => "brotli",
                _ => throw new JsonException("不支援的壓縮方法。")
            });
    }
}

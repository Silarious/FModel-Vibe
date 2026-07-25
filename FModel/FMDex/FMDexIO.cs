using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace FModel.FMDex;

/// <summary>Brotli (quality 11) read/write for FMDex JSON payloads.</summary>
public static class FMDexIO
{
    /// <summary>Brotli quality used for at-rest FMDex (matches ablation “Brotli-11”).</summary>
    public const int BrotliQuality = 11;

    public static bool IsBrotliPath(string path)
        => !string.IsNullOrEmpty(path) &&
           path.EndsWith(".br", StringComparison.OrdinalIgnoreCase);

    public static bool IsFmDexPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var name = Path.GetFileName(path);
        return name.EndsWith(FMDexDocument.FileSuffixBrotli, StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(FMDexDocument.FileSuffixPlain, StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(FMDexDocument.LegacyFileSuffixBrotli, StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(FMDexDocument.LegacyFileSuffixPlain, StringComparison.OrdinalIgnoreCase);
    }

    public static string ReadAllText(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!IsBrotliPath(path))
            return File.ReadAllText(path);

        using var input = File.OpenRead(path);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(brotli, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static void WriteAllTextBrotli(string path, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(json);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var utf8 = Encoding.UTF8.GetBytes(json);
        using var output = File.Create(path);
        using var brotli = new BrotliStream(output, new BrotliCompressionOptions { Quality = BrotliQuality });
        brotli.Write(utf8, 0, utf8.Length);
    }
}

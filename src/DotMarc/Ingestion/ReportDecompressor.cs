using System.IO.Compression;

namespace DotMarc.Ingestion;

/// <summary>Decompresses a DMARC report mailbox attachment. Detects format by magic bytes rather
/// than filename or content-type, since sending providers can mislabel either.</summary>
public static class ReportDecompressor
{
    public static byte[] Decompress(byte[] attachmentBytes)
    {
        if (IsGzip(attachmentBytes))
        {
            using var input = new MemoryStream(attachmentBytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }

        if (IsZip(attachmentBytes))
        {
            using var input = new MemoryStream(attachmentBytes);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read);
            var entry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                ?? archive.Entries.FirstOrDefault()
                ?? throw new InvalidDataException("Zip attachment contains no entries.");
            using var entryStream = entry.Open();
            using var output = new MemoryStream();
            entryStream.CopyTo(output);
            return output.ToArray();
        }

        return attachmentBytes;
    }

    private static bool IsGzip(byte[] bytes) =>
        bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;

    private static bool IsZip(byte[] bytes) =>
        bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04;
}

using System.IO.Compression;
using System.Text;
using DotMarc.Ingestion;
using Xunit;

namespace DotMarc.Tests.Ingestion;

public class ReportDecompressorTests
{
    private const string SampleXml = "<feedback><report_metadata /></feedback>";

    [Fact]
    public void Decompress_ReturnsOriginalBytes_ForPlainXml()
    {
        var bytes = Encoding.UTF8.GetBytes(SampleXml);

        var result = DotMarc.Ingestion.ReportDecompressor.Decompress(bytes);

        Assert.Equal(SampleXml, Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void Decompress_ReturnsOriginalXml_ForGzipInput()
    {
        var gzipped = Gzip(SampleXml);

        var result = DotMarc.Ingestion.ReportDecompressor.Decompress(gzipped);

        Assert.Equal(SampleXml, Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void Decompress_ReturnsOriginalXml_ForZipInput_WithSingleEntry()
    {
        var zipped = Zip(("report.xml", SampleXml));

        var result = DotMarc.Ingestion.ReportDecompressor.Decompress(zipped);

        Assert.Equal(SampleXml, Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void Decompress_PicksTheXmlEntry_WhenZipHasMultipleEntries()
    {
        var zipped = Zip(("readme.txt", "not the report"), ("report.xml", SampleXml));

        var result = DotMarc.Ingestion.ReportDecompressor.Decompress(zipped);

        Assert.Equal(SampleXml, Encoding.UTF8.GetString(result));
    }

    private static byte[] Gzip(string content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            gzip.Write(bytes, 0, bytes.Length);
        }
        return output.ToArray();
    }

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var entryStream = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                entryStream.Write(bytes, 0, bytes.Length);
            }
        }
        return output.ToArray();
    }
}

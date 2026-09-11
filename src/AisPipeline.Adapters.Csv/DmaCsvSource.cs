using System.IO.Compression;
using AisPipeline.Core.Domain;
using AisPipeline.Core.Ports;
using Sylvan.Data.Csv;

namespace AisPipeline.Adapters.Csv;

/// <summary>
/// Reads the Danish Maritime Authority's daily CSV, from a .csv or straight out of a .zip.
///
/// Streams rather than materialising: one day is ~3 GB expanded and ~17M rows.
/// </summary>
public sealed class DmaCsvSource : IAisSource
{
    private readonly string _path;

    public DmaCsvSource(string path)
    {
        _path = path;
        SourceName = Path.GetFileName(path);
    }

    public string SourceName { get; }

    /// <summary>
    /// The DMA header line begins "# Timestamp", and Sylvan's default comment character is '#'.
    /// Left at the default, the reader discards the real header, promotes the first DATA row to
    /// be the header, and silently loses one row from every file parsed. Disabling comments is
    /// mandatory -- see docs/rules/units-and-geodesy.md.
    /// </summary>
    internal static CsvDataReaderOptions ReaderOptions() => new() { Comment = '\0' };

    public IEnumerable<AisSourceLine> ReadLines()
    {
        using var reader = OpenText(_path, out var entryName);
        var source = entryName ?? SourceName;

        using var csv = CsvDataReader.Create(reader, ReaderOptions());

        // Line 1 is the header, so the first data row is line 2. Quoted fields may span
        // newlines in principle; RowNumber counts rows, which is what provenance needs.
        while (csv.Read())
        {
            var fields = new string[csv.FieldCount];
            for (var i = 0; i < csv.FieldCount; i++)
            {
                fields[i] = csv.GetString(i);
            }

            yield return new AisSourceLine(source, csv.RowNumber + 1, RawTextOf(fields), fields);
        }
    }

    /// <summary>
    /// Reconstructed row text for the quarantine record. The reader does not expose the
    /// original bytes, so a refused row carries its parsed fields rather than the exact
    /// characters -- enough to identify and re-judge it, which is what the evidence is for.
    /// </summary>
    private static string RawTextOf(IReadOnlyList<string> fields) => string.Join(',', fields);

    private static TextReader OpenText(string path, out string? entryName)
    {
        if (!Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            entryName = null;
            return new StreamReader(path);
        }

        var archive = ZipFile.OpenRead(path);
        var entry = archive.Entries.FirstOrDefault(e =>
            e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"no .csv entry inside '{path}'");

        entryName = entry.Name;

        // The archive must outlive the reader: disposing the reader disposes the entry stream,
        // and this wrapper disposes the archive behind it.
        return new ZipEntryReader(archive, entry.Open());
    }

    private sealed class ZipEntryReader : StreamReader
    {
        private readonly ZipArchive _archive;

        public ZipEntryReader(ZipArchive archive, Stream stream)
            : base(stream) => _archive = archive;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _archive.Dispose();
            }
        }
    }
}

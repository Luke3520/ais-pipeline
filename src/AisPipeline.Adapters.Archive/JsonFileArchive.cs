using AisPipeline.Core.Archive;
using AisPipeline.Core.Ports;

namespace AisPipeline.Adapters.Archive;

/// <summary>
/// The archive port over a directory of JSON files.
///
/// Names each file for the cutoff it was written at, so a directory of them reads as a history of
/// prunes rather than a pile that overwrites itself. The document already carries that cutoff,
/// which is why the caller does not pass it twice.
/// </summary>
public sealed class JsonFileArchive : IPortCallArchive
{
    private readonly string _directory;

    public JsonFileArchive(string directory) => _directory = directory;

    public string Write(ArchiveDocument document)
    {
        // Created here rather than at startup: a prune with nothing to archive never calls this,
        // and should not leave an empty directory behind suggesting it did.
        Directory.CreateDirectory(_directory);

        var path = Path.Combine(
            _directory,
            $"port-calls-before-{document.CutoffUtc:yyyyMMddTHHmmss}Z.json");

        ArchiveJson.WriteFile(path, document);
        return path;
    }

    public ArchiveDocument Read(string path) => ArchiveJson.ReadFile(path);
}

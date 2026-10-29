using System.IO;
using System.Text;
using System.Text.Json;

namespace DomainDesk.Services;

public sealed class AuditLog
{
    public string FilePath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DomainDesk", "audit.jsonl");

    public void Append(AuditEntry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.WriteLine(JsonSerializer.Serialize(entry));
    }

    public IReadOnlyList<AuditEntry> LoadRecent()
    {
        if (!File.Exists(FilePath)) return [];
        var result = new Queue<AuditEntry>();
        foreach (var line in File.ReadLines(FilePath))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<AuditEntry>(line);
                if (entry is null) continue;
                result.Enqueue(entry);
                if (result.Count > 500) result.Dequeue();
            }
            catch (JsonException) { /* A damaged line must not hide other audit records. */ }
        }
        return result.Reverse().ToArray();
    }
}

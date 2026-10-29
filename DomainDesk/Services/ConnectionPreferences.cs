using System.IO;
using System.Text.Json;

namespace DomainDesk.Services;

// This DTO deliberately cannot hold a password or a credential object.
public sealed record ConnectionProfile(string Server = "", string Username = "", bool UseCurrentIdentity = false,
    bool UseTls = true, bool AllowWrites = false);

public sealed class ConnectionPreferences
{
    private readonly string _path;
    public string? Warning { get; private set; }
    public ConnectionPreferences(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DomainDesk", "connections.json");

    private Dictionary<string, ConnectionProfile> Read()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, ConnectionProfile>>(File.ReadAllText(_path)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Warning = "无法读取连接设置，已使用默认值：" + ex.Message;
            return [];
        }
    }

    public ConnectionProfile Load(string key) => Read().GetValueOrDefault(key) ?? new();
    public void Save(string key, ConnectionProfile profile)
    {
        var profiles = Read();
        profiles[key] = profile;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

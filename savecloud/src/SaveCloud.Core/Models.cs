using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SaveCloud.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new InvalidDataException("資料格式錯誤。");
}

public sealed record SaveSlot
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Label { get; init; } = "存檔";
    public string Path { get; set; } = "";
    public bool IsFile { get; init; }
    public bool Recursive { get; init; } = true;
    public string[] Patterns { get; init; } = ["*"];
    public SaveSlot Portable() => this with { Path = "" };
}

public sealed record GameProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Source { get; set; } = "其他";
    public string ExePath { get; set; } = "";
    public List<SaveSlot> Slots { get; set; } = [];
    public bool Confirmed { get; set; }
    public string Status { get; set; } = "尚未同步";
    public string LastBackup { get; set; } = "—";
    public CloudGame Portable() => new() { Id = Id, Name = Name, Source = Source, Slots = Slots.Select(x => x.Portable()).ToList() };
    public override string ToString() => Name;
}

public sealed record CloudGame
{
    public int SchemaVersion { get; init; } = 1;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Source { get; init; } = "";
    public List<SaveSlot> Slots { get; init; } = [];
    public override string ToString() => $"{Name}  ·  {Source}  ·  {Id[..Math.Min(8, Id.Length)]}";
}

public sealed record SavedFile(string SlotId, string RelativePath, long Length, string Sha256)
{
    public string ArchivePath => $"{SlotId}/{RelativePath}";
}

public sealed record Snapshot
{
    public int SchemaVersion { get; init; } = 1;
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string GameId { get; init; } = "";
    public List<string> Parents { get; init; } = [];
    public string Device { get; init; } = Environment.MachineName;
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public List<SavedFile> Files { get; init; } = [];
    public string ContentHash { get; init; } = "";
    public string ArchiveSha256 { get; init; } = "";
    public string ArchiveMd5 { get; init; } = "";
    public long ArchiveLength { get; init; }
    public override string ToString() => $"{CreatedUtc.ToLocalTime():yyyy/MM/dd HH:mm:ss} · {Device} · {Files.Count} 個檔案 · {Id[..8]}";
}

public sealed record Baseline(string SnapshotId, string ContentHash);
public sealed record PendingUpload(Snapshot Snapshot, string ArchivePath);
public sealed record RestoreJournal(string GameId, Snapshot Backup, string BackupPath, string Phase);
public sealed record Candidate(string Name, string ExePath, string Evidence, List<SaveSlot> Slots)
{
    public override string ToString() => $"{Name}  |  {Evidence}\n{ExePath}";
}
public sealed record LocalInventory(List<SavedFile> Files, Dictionary<string, string> Sources)
{
    public string ContentHash => Hashing.Content(Files);
}
public static class Hashing
{
    public static string Content(IEnumerable<SavedFile> files) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\n', files.OrderBy(x => x.ArchivePath, StringComparer.Ordinal).Select(x => Json.Write(new { x.SlotId, x.RelativePath, x.Length, x.Sha256 }))))));
    public static string FileSha(string path)
    {
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }
    public static string FileMd5(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(MD5.HashData(file));
    }
}

public interface ICloudStore
{
    Task<List<CloudGame>> ListGamesAsync(CancellationToken ct);
    Task PutGameAsync(CloudGame game, CancellationToken ct);
    Task<List<Snapshot>> ListSnapshotsAsync(string gameId, CancellationToken ct);
    Task UploadAsync(PendingUpload upload, CancellationToken ct);
    Task DownloadAsync(Snapshot snapshot, string destination, CancellationToken ct);
}
public interface IGameMonitor
{
    bool IsRunning(GameProfile game);
    Task<bool> LaunchAndWaitAsync(GameProfile game, CancellationToken ct);
}
public sealed class AuthenticationRequiredException(string message) : Exception(message);
public sealed class ConflictException(List<Snapshot> heads) : Exception("本機與雲端有不同進度，請選擇要接續的版本。")
{
    public List<Snapshot> Heads { get; } = heads;
}

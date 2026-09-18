using Microsoft.Data.Sqlite;

namespace SaveCloud.Core;

public sealed class LocalStore
{
    public string DataDirectory { get; }
    private readonly string connectionString;
    public LocalStore(string dataDirectory)
    {
        DataDirectory = System.IO.Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(DataDirectory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = System.IO.Path.Combine(DataDirectory, "state.db") }.ToString();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS state (kind TEXT NOT NULL, id TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(kind,id));";
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
    public void Put<T>(string kind, string id, T value)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO state(kind,id,json) VALUES($kind,$id,$json) ON CONFLICT(kind,id) DO UPDATE SET json=excluded.json";
        cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$json", Json.Write(value));
        cmd.ExecuteNonQuery();
    }
    public T? Get<T>(string kind, string id)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT json FROM state WHERE kind=$kind AND id=$id";
        cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is string json ? Json.Read<T>(json) : default;
    }
    public List<T> All<T>(string kind)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT json FROM state WHERE kind=$kind ORDER BY id"; cmd.Parameters.AddWithValue("$kind", kind);
        using var reader = cmd.ExecuteReader(); var result = new List<T>();
        while (reader.Read()) result.Add(Json.Read<T>(reader.GetString(0)));
        return result;
    }
    public void Remove(string kind, string id)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM state WHERE kind=$kind AND id=$id";
        cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery();
    }
    public void SaveGame(GameProfile game) => Put("game", game.Id, game);
    public List<GameProfile> Games() => All<GameProfile>("game");
    public Baseline? Baseline(string id) => Get<Baseline>("baseline", id);
    public void SetBaseline(Snapshot snapshot) => Put("baseline", snapshot.GameId, new Baseline(snapshot.Id, snapshot.ContentHash));
    public List<PendingUpload> Pending(string gameId)
    {
        var remaining = All<PendingUpload>("pending").Where(x => x.Snapshot.GameId == gameId).ToList(); var ordered = new List<PendingUpload>();
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(x => !remaining.Any(y => x.Snapshot.Parents.Contains(y.Snapshot.Id))) ?? throw new InvalidDataException("待上傳版本關係有循環。");
            remaining.Remove(next); ordered.Add(next);
        }
        return ordered;
    }
    public void CommitRestore(Snapshot snapshot)
    {
        using var db = Open(); using var transaction = db.BeginTransaction();
        using var baseline = db.CreateCommand(); baseline.Transaction = transaction;
        baseline.CommandText = "INSERT INTO state(kind,id,json) VALUES('baseline',$id,$json) ON CONFLICT(kind,id) DO UPDATE SET json=excluded.json";
        baseline.Parameters.AddWithValue("$id", snapshot.GameId); baseline.Parameters.AddWithValue("$json", Json.Write(new Baseline(snapshot.Id, snapshot.ContentHash))); baseline.ExecuteNonQuery();
        using var journal = db.CreateCommand(); journal.Transaction = transaction; journal.CommandText = "DELETE FROM state WHERE kind='restore' AND id=$id";
        journal.Parameters.AddWithValue("$id", snapshot.GameId); journal.ExecuteNonQuery(); transaction.Commit();
    }
    public string SnapshotPath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("備份 ID 不正確。");
        var dir = System.IO.Path.Combine(DataDirectory, "snapshots"); Directory.CreateDirectory(dir);
        return System.IO.Path.Combine(dir, id + ".zip");
    }
}

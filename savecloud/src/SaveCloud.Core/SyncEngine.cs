namespace SaveCloud.Core;

public sealed class SyncEngine(LocalStore store, SnapshotService snapshots, ICloudStore cloud, IGameMonitor monitor)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public static List<Snapshot> Heads(List<Snapshot> snapshots)
    {
        foreach (var snapshot in snapshots) SnapshotService.ValidateManifest(snapshot);
        if (snapshots.Select(x => x.Id).Distinct().Count() != snapshots.Count) throw new InvalidDataException("雲端備份有重複 ID。");
        var remaining = snapshots.ToDictionary(s => s.Id);
        while (remaining.Count > 0)
        {
            var ready = remaining.Values.Where(s => !s.Parents.Any(remaining.ContainsKey)).Select(s => s.Id).ToList();
            if (ready.Count == 0) throw new InvalidDataException("雲端版本關係有循環。");
            foreach (var id in ready) remaining.Remove(id);
        }
        var parents = snapshots.SelectMany(x => x.Parents).ToHashSet();
        var heads = snapshots.Where(x => !parents.Contains(x.Id)).ToList();
        if (snapshots.Count > 0 && heads.Count == 0) throw new InvalidDataException("雲端版本關係有循環。");
        return heads;
    }
    private void Ready(GameProfile game)
    {
        if (!game.Confirmed) throw new InvalidOperationException("請先確認遊戲的存檔位置。");
        SafePaths.Validate(game);
        if (monitor.IsRunning(game)) throw new IOException("遊戲仍在執行，請先關閉遊戲。");
        snapshots.Recover(game, () => monitor.IsRunning(game));
    }
    public async Task SynchronizeAsync(GameProfile game, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            Ready(game);
            // 先讀取雲端；網路或授權失敗時不得改動本機進度。
            _ = await cloud.ListSnapshotsAsync(game.Id, ct);
            await FlushAsync(game, ct);
            var all = await cloud.ListSnapshotsAsync(game.Id, ct); var heads = Heads(all);
            foreach (var snapshot in all) store.Put("snapshot", snapshot.Id, snapshot);
            var inventory = await Task.Run(() => snapshots.Inventory(game, ct), ct);
            var baseline = store.Baseline(game.Id);
            if (heads.Count > 1) throw new ConflictException(heads);
            var head = heads.SingleOrDefault();
            if (head is null)
            {
                await cloud.PutGameAsync(game.Portable(), ct);
                if (inventory.Files.Count > 0) await PublishCurrentAsync(game, baseline is null ? [] : [baseline.SnapshotId], ct);
            }
            else if (inventory.ContentHash == head.ContentHash) store.SetBaseline(head);
            else if (baseline is null)
            {
                if (inventory.Files.Count == 0) await DownloadRestoreAsync(game, head, ct);
                else throw new ConflictException(heads);
            }
            else if (!all.Any(x => x.Id == baseline.SnapshotId)) throw new ConflictException(heads);
            else if (inventory.ContentHash == baseline.ContentHash) await DownloadRestoreAsync(game, head, ct);
            else if (head.Id == baseline.SnapshotId) await PublishCurrentAsync(game, [head.Id], ct);
            else throw new ConflictException(heads);
            await EnsureSingleHeadAsync(game.Id, ct);
        }
        finally { gate.Release(); }
    }
    public async Task QueueOfflineAsync(GameProfile game, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            Ready(game); var pending = store.Pending(game.Id).LastOrDefault();
            var inventory = await Task.Run(() => snapshots.Inventory(game, ct), ct);
            var baseline = store.Baseline(game.Id);
            if (inventory.ContentHash == (pending?.Snapshot.ContentHash ?? baseline?.ContentHash)) return;
            if (inventory.Files.Count == 0 && pending is null && baseline is null) return;
            var parent = pending?.Snapshot.Id ?? baseline?.SnapshotId;
            var backup = await snapshots.CaptureAsync(game, parent is null ? [] : [parent], ct);
            store.Put("pending", backup.Snapshot.Id, backup);
        }
        finally { gate.Release(); }
    }
    private async Task FlushAsync(GameProfile game, CancellationToken ct)
    {
        var pending = store.Pending(game.Id); if (pending.Count == 0) return;
        await cloud.PutGameAsync(game.Portable(), ct);
        // 版本不可變，即使另一台電腦有新進度也能先安全保留分支。
        foreach (var upload in pending)
        {
            await cloud.UploadAsync(upload, ct);
            store.SetBaseline(upload.Snapshot);
            store.Remove("pending", upload.Snapshot.Id);
        }
    }
    private async Task PublishCurrentAsync(GameProfile game, IEnumerable<string> parents, CancellationToken ct)
    {
        var upload = await snapshots.CaptureAsync(game, parents, ct);
        store.Put("pending", upload.Snapshot.Id, upload);
        await FlushAsync(game, ct);
    }
    private async Task DownloadRestoreAsync(GameProfile game, Snapshot snapshot, CancellationToken ct)
    {
        var path = store.SnapshotPath(snapshot.Id);
        if (!File.Exists(path) || await Task.Run(() => Hashing.FileSha(path), ct) != snapshot.ArchiveSha256) await cloud.DownloadAsync(snapshot, path, ct);
        await snapshots.RestoreAsync(game, snapshot, path, () => monitor.IsRunning(game), ct);
        store.Put("snapshot", snapshot.Id, snapshot);
    }
    private async Task EnsureSingleHeadAsync(string gameId, CancellationToken ct)
    {
        var heads = Heads(await cloud.ListSnapshotsAsync(gameId, ct));
        if (heads.Count > 1 || heads.Count == 1 && heads[0].Id != store.Baseline(gameId)?.SnapshotId) throw new ConflictException(heads);
    }
    public async Task ResolveAsync(GameProfile game, Snapshot? chosenCloudVersion, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            Ready(game); await FlushAsync(game, ct);
            var observed = await cloud.ListSnapshotsAsync(game.Id, ct);
            if (chosenCloudVersion is not null)
            {
                var selected = observed.SingleOrDefault(s => s.Id == chosenCloudVersion.Id) ?? throw new IOException("選擇的雲端版本已不存在。");
                var inventory = await Task.Run(() => snapshots.Inventory(game, ct), ct);
                if (inventory.ContentHash != selected.ContentHash)
                {
                    // 替換前先保留被取代的本機分支。
                    var baseline = store.Baseline(game.Id);
                    await PublishCurrentAsync(game, baseline is null ? [] : [baseline.SnapshotId], ct);
                    // 只包含剛保留的分支及本次操作已讀取的版本，
                    // 不得靜默合併並行上傳的新分支。
                    var localId = store.Baseline(game.Id)!.SnapshotId;
                    var local = store.Get<Snapshot>("snapshot", localId)!;
                    observed.Add(local);
                    await DownloadRestoreAsync(game, selected, ct);
                }
            }
            await PublishCurrentAsync(game, Heads(observed).Select(s => s.Id), ct);
            await EnsureSingleHeadAsync(game.Id, ct);
        }
        finally { gate.Release(); }
    }
}

// 整合測試使用的固定檔案傳輸；正式介面使用 GoogleDriveStore。
public sealed class DirectoryCloudStore(string root) : ICloudStore
{
    private string Folder(string gameId)
    {
        if (!Guid.TryParseExact(gameId, "N", out _)) throw new InvalidDataException();
        var folder = Path.Combine(root, gameId); Directory.CreateDirectory(folder); return folder;
    }
    public Task<List<CloudGame>> ListGamesAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        return Task.FromResult(Directory.EnumerateFiles(root, "game.json", SearchOption.AllDirectories).Select(p => Json.Read<CloudGame>(File.ReadAllText(p))).ToList());
    }
    public Task PutGameAsync(CloudGame game, CancellationToken ct)
    {
        var path = Path.Combine(Folder(game.Id), "game.json");
        if (!File.Exists(path)) File.WriteAllText(path, Json.Write(game));
        return Task.CompletedTask;
    }
    public Task<List<Snapshot>> ListSnapshotsAsync(string gameId, CancellationToken ct) => Task.FromResult(Directory.EnumerateFiles(Folder(gameId), "*.snapshot.json").Select(p => Json.Read<Snapshot>(File.ReadAllText(p))).ToList());
    public Task UploadAsync(PendingUpload upload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var s = upload.Snapshot;
        var zip = Path.Combine(Folder(s.GameId), s.Id + ".zip");
        if (Hashing.FileSha(upload.ArchivePath) != s.ArchiveSha256) throw new InvalidDataException("本機備份校驗失敗。");
        File.Copy(upload.ArchivePath, zip, true);
        var metadata = Path.Combine(Folder(s.GameId), s.Id + ".snapshot.json");
        File.WriteAllText(metadata, Json.Write(s)); return Task.CompletedTask;
    }
    public Task DownloadAsync(Snapshot snapshot, string destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); File.Copy(Path.Combine(Folder(snapshot.GameId), snapshot.Id + ".zip"), destination, true); return Task.CompletedTask;
    }
}

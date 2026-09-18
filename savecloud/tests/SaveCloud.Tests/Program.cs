using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SaveCloud.Core;
using SaveCloud.App;

var repo = Path.GetFullPath(args.FirstOrDefault() ?? Environment.CurrentDirectory);
var runRoot = Path.Combine(repo, "artifacts", "tests", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(runRoot);
var results = new List<object>(); var failed = 0;
async Task Test(string name, Func<Task> body)
{
    var watch = Stopwatch.StartNew();
    try { await body(); Console.WriteLine("PASS " + name); results.Add(new { name, passed = true, seconds = watch.Elapsed.TotalSeconds }); }
    catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + "\n" + e); results.Add(new { name, passed = false, error = e.ToString(), seconds = watch.Elapsed.TotalSeconds }); }
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name);
}
Fixture New(string name) => new(Path.Combine(runRoot, name));

await Test("two computers: A → B → A, different local paths and Chinese filenames", async () =>
{
    var f = New("roundtrip"); File.WriteAllText(f.AFile, "A 第一次進度");
    await f.ASync.SynchronizeAsync(f.AGame, default);
    await f.BSync.SynchronizeAsync(f.BGame, default);
    Check(File.ReadAllText(f.BFile) == "A 第一次進度", "B did not restore A");
    File.WriteAllText(f.BFile, "B 第二次進度"); await f.BSync.SynchronizeAsync(f.BGame, default);
    await f.ASync.SynchronizeAsync(f.AGame, default);
    Check(File.ReadAllText(f.AFile) == "B 第二次進度", "A did not restore B");
    Check(SyncEngine.Heads(await f.Cloud.ListSnapshotsAsync(f.AGame.Id, default)).Count == 1, "unexpected branch");
});
await Test("offline divergent progress is retained and requires a choice", async () =>
{
    var f = New("conflict"); File.WriteAllText(f.AFile, "base"); await f.ASync.SynchronizeAsync(f.AGame, default); await f.BSync.SynchronizeAsync(f.BGame, default);
    File.WriteAllText(f.AFile, "A offline"); await f.ASync.QueueOfflineAsync(f.AGame, default);
    File.WriteAllText(f.BFile, "B offline"); await f.BSync.QueueOfflineAsync(f.BGame, default);
    await f.ASync.SynchronizeAsync(f.AGame, default);
    await Throws<ConflictException>(() => f.BSync.SynchronizeAsync(f.BGame, default));
    Check(File.ReadAllText(f.BFile) == "B offline", "local conflict overwritten");
    var heads = SyncEngine.Heads(await f.Cloud.ListSnapshotsAsync(f.BGame.Id, default)); Check(heads.Count == 2, "branches were lost");
    var aHead = heads.Single(s => s.ContentHash == f.A.Baseline(f.AGame.Id)!.ContentHash);
    await f.BSync.ResolveAsync(f.BGame, aHead, default);
    Check(File.ReadAllText(f.BFile) == "A offline", "chosen cloud not applied");
    var all = await f.Cloud.ListSnapshotsAsync(f.BGame.Id, default);
    Check(all.Any(s => s.ContentHash == heads.Single(s => s.Id != aHead.Id).ContentHash), "losing version deleted");
    Check(SyncEngine.Heads(all).Count == 1, "resolution did not converge");
});
await Test("first link with different local contents never auto-overwrites", async () =>
{
    var f = New("first-link"); File.WriteAllText(f.AFile, "remote"); File.WriteAllText(f.BFile, "local");
    await f.ASync.SynchronizeAsync(f.AGame, default);
    await Throws<ConflictException>(() => f.BSync.SynchronizeAsync(f.BGame, default)); Check(File.ReadAllText(f.BFile) == "local", "overwritten");
    await f.BSync.ResolveAsync(f.BGame, null, default);
    Check(File.ReadAllText(f.BFile) == "local", "local choice lost");
    await f.ASync.SynchronizeAsync(f.AGame, default); Check(File.ReadAllText(f.AFile) == "local", "local choice not propagated");
});
await Test("no save yet: cloud registration without empty progress upload", async () =>
{
    var f = New("empty"); await f.ASync.SynchronizeAsync(f.AGame, default);
    Check((await f.Cloud.ListSnapshotsAsync(f.AGame.Id, default)).Count == 0, "empty initial snapshot");
    Check((await f.Cloud.ListGamesAsync(default)).Count == 1, "game not discoverable");
});
await Test("multiple save locations, single-file mapping, selective deletion", async () =>
{
    var f = New("multiple");
    f.AGame.Slots[0] = f.AGame.Slots[0] with { Patterns = ["*.sav"] };
    var extra = Path.Combine(f.Root, "settings", "persistent"); Directory.CreateDirectory(Path.GetDirectoryName(extra)!); File.WriteAllText(extra, "persistent-original");
    f.AGame.Slots.Add(new() { Path = extra, IsFile = true, Label = "persistent" });
    File.WriteAllText(f.AFile, "original"); File.WriteAllText(Path.Combine(Path.GetDirectoryName(f.AFile)!, "assets.bin"), "untouched");
    var snap = await f.ASnapshots.CaptureAsync(f.AGame, [], default, false);
    File.WriteAllText(f.AFile, "changed"); File.WriteAllText(extra, "changed"); var added = Path.Combine(Path.GetDirectoryName(f.AFile)!, "new.sav"); File.WriteAllText(added, "new");
    await f.ASnapshots.RestoreAsync(f.AGame, snap.Snapshot, snap.ArchivePath, () => false, default);
    Check(File.ReadAllText(f.AFile) == "original" && File.ReadAllText(extra) == "persistent-original", "multi-root restore failed");
    Check(!File.Exists(added), "deleted managed file retained"); Check(File.ReadAllText(Path.Combine(Path.GetDirectoryName(f.AFile)!, "assets.bin")) == "untouched", "unmanaged asset deleted");
});
await Test("corrupt archive is rejected before touching original saves", async () =>
{
    var f = New("corrupt"); File.WriteAllText(f.AFile, "before"); var snap = await f.ASnapshots.CaptureAsync(f.AGame, [], default, false);
    File.WriteAllText(f.AFile, "current"); File.WriteAllText(snap.ArchivePath, "broken zip");
    await Throws<InvalidDataException>(() => f.ASnapshots.RestoreAsync(f.AGame, snap.Snapshot, snap.ArchivePath, () => false, default));
    Check(File.ReadAllText(f.AFile) == "current", "corrupt archive modified original");
});
await Test("ZIP traversal, duplicate names, and tampered file hash are rejected", async () =>
{
    var f = New("zip-boundary"); File.WriteAllText(f.AFile, "safe"); var content = Encoding.UTF8.GetBytes("evil");
    foreach (var relative in new[] { "../../escape.sav", "../escape.sav", "C:/escape.sav", "a\\..\\escape.sav", "file.sav:stream", "folder./save.sav", "NUL.sav", "folder/COM1.dat" })
    {
        var file = new SavedFile(f.AGame.Slots[0].Id, relative, content.Length, Convert.ToHexStringLower(SHA256.HashData(content)));
        var archive = Path.Combine(f.Root, Guid.NewGuid().ToString("N") + ".zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) { using var output = zip.CreateEntry(file.ArchivePath).Open(); output.Write(content); }
        var s = new Snapshot { GameId = f.AGame.Id, Files = [file], ContentHash = Hashing.Content([file]), ArchiveLength = new FileInfo(archive).Length, ArchiveSha256 = Hashing.FileSha(archive) };
        await Throws<InvalidDataException>(() => Task.Run(() => SnapshotService.ValidateArchive(f.AGame, s, archive, default)));
    }
    var snapshot = await f.ASnapshots.CaptureAsync(f.AGame, [], default, false);
    var bad = snapshot.Snapshot with { Files = snapshot.Snapshot.Files.Select(x => x with { Sha256 = new string('0', 64) }).ToList() };
    bad = bad with { ContentHash = Hashing.Content(bad.Files) };
    await Throws<InvalidDataException>(() => Task.Run(() => SnapshotService.ValidateArchive(f.AGame, bad, snapshot.ArchivePath, default)));
    var duplicate = snapshot.Snapshot with { Files = [.. snapshot.Snapshot.Files, .. snapshot.Snapshot.Files] };
    duplicate = duplicate with { ContentHash = Hashing.Content(duplicate.Files) };
    await Throws<InvalidDataException>(() => Task.Run(() => SnapshotService.ValidateArchive(f.AGame, duplicate, snapshot.ArchivePath, default)));
    Check(File.ReadAllText(f.AFile) == "safe", "zip attack changed save");
});
await Test("game running blocks restore and launch-time synchronization", async () =>
{
    var f = New("running"); File.WriteAllText(f.AFile, "save"); var snap = await f.ASnapshots.CaptureAsync(f.AGame, [], default, false);
    await Throws<IOException>(() => f.ASnapshots.RestoreAsync(f.AGame, snap.Snapshot, snap.ArchivePath, () => true, default));
    f.Monitor.Running = true; await Throws<IOException>(() => f.ASync.SynchronizeAsync(f.AGame, default));
});
await Test("locked save and canceled backup leave no committed snapshot", async () =>
{
    var f = New("locked"); File.WriteAllText(f.AFile, "save");
    using (var locked = File.Open(f.AFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) await Throws<IOException>(() => f.ASnapshots.CaptureAsync(f.AGame, [], default, false));
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    await Throws<OperationCanceledException>(() => f.ASnapshots.CaptureAsync(f.AGame, [], cancel.Token));
    Check(f.A.All<Snapshot>("snapshot").Count == 0, "failed backup committed");
});
await Test("interrupted restore is rolled back on next startup", async () =>
{
    var f = New("journal"); File.WriteAllText(f.AFile, "original"); var backup = await f.ASnapshots.CaptureAsync(f.AGame, [], default, false);
    f.A.Put("restore", f.AGame.Id, new RestoreJournal(f.AGame.Id, backup.Snapshot, backup.ArchivePath, "applying"));
    File.WriteAllText(f.AFile, "half restored"); File.WriteAllText(Path.Combine(Path.GetDirectoryName(f.AFile)!, "partial.sav"), "partial");
    var reopened = new LocalStore(f.A.DataDirectory); new SnapshotService(reopened).Recover(f.AGame, () => false);
    Check(File.ReadAllText(f.AFile) == "original", "recovery did not restore original");
    Check(!File.Exists(Path.Combine(Path.GetDirectoryName(f.AFile)!, "partial.sav")), "partial file survived rollback");
    Check(reopened.Get<RestoreJournal>("restore", f.AGame.Id) is null, "journal retained after successful rollback");
});
await Test("network failure retains queued local backup; retry is idempotent", async () =>
{
    var f = New("network"); File.WriteAllText(f.AFile, "offline"); await f.ASync.QueueOfflineAsync(f.AGame, default);
    var cloud = new FaultCloud(f.Cloud) { FailUpload = true }; var engine = new SyncEngine(f.A, f.ASnapshots, cloud, f.Monitor);
    await Throws<HttpRequestException>(() => engine.SynchronizeAsync(f.AGame, default));
    Check(f.A.Pending(f.AGame.Id).Count == 1, "pending removed on failure");
    cloud.FailUpload = false; cloud.ThrowAfterUpload = true;
    await Throws<HttpRequestException>(() => engine.SynchronizeAsync(f.AGame, default));
    await engine.SynchronizeAsync(f.AGame, default);
    Check((await f.Cloud.ListSnapshotsAsync(f.AGame.Id, default)).Count == 1 && f.A.Pending(f.AGame.Id).Count == 0, "retry duplicated commit");
});
await Test("authorization failure cannot trigger restore or discard pending progress", async () =>
{
    var f = New("auth"); File.WriteAllText(f.AFile, "private local save"); await f.ASync.QueueOfflineAsync(f.AGame, default);
    var cloud = new FaultCloud(f.Cloud) { AuthFailure = true }; var engine = new SyncEngine(f.A, f.ASnapshots, cloud, f.Monitor);
    await Throws<AuthenticationRequiredException>(() => engine.SynchronizeAsync(f.AGame, default));
    Check(File.ReadAllText(f.AFile) == "private local save" && f.A.Pending(f.AGame.Id).Count == 1, "auth failure lost data");
});
await Test("pending snapshots follow parent order even when device clock goes backward", () =>
{
    var f = New("clock"); var one = new Snapshot { GameId = f.AGame.Id, ContentHash = Hashing.Content([]), CreatedUtc = DateTimeOffset.UtcNow };
    var two = one with { Id = Guid.NewGuid().ToString("N"), Parents = [one.Id], CreatedUtc = one.CreatedUtc.AddDays(-7) };
    f.A.Put("pending", one.Id, new PendingUpload(one, "one.zip")); f.A.Put("pending", two.Id, new PendingUpload(two, "two.zip"));
    Check(f.A.Pending(f.AGame.Id).Select(p => p.Snapshot.Id).SequenceEqual(new[] { one.Id, two.Id }), "used timestamp order"); return Task.CompletedTask;
});
await Test("concurrent upload remains a branch instead of silently taking latest clock", async () =>
{
    var f = New("concurrent"); File.WriteAllText(f.AFile, "base"); await f.ASync.SynchronizeAsync(f.AGame, default); await f.BSync.SynchronizeAsync(f.BGame, default);
    File.WriteAllText(f.AFile, "A choice"); File.WriteAllText(f.BFile, "B simultaneous");
    await f.ASync.QueueOfflineAsync(f.AGame, default); await f.BSync.QueueOfflineAsync(f.BGame, default);
    foreach (var p in f.A.Pending(f.AGame.Id)) await f.Cloud.UploadAsync(p, default);
    foreach (var p in f.B.Pending(f.BGame.Id)) await f.Cloud.UploadAsync(p, default);
    await Throws<ConflictException>(() => f.ASync.SynchronizeAsync(f.AGame, default));
    await Throws<ConflictException>(() => f.BSync.SynchronizeAsync(f.BGame, default));
    Check(SyncEngine.Heads(await f.Cloud.ListSnapshotsAsync(f.AGame.Id, default)).Count == 2, "simultaneous branch hidden");
});
await Test("scanner: nested Chinese paths, same exe names, engine rules and cancellation", async () =>
{
    var f = New("scanner");
    var first = Path.Combine(f.Root, "遊戲庫", "第一款"); var second = Path.Combine(f.Root, "遊戲庫", "分組", "第二款");
    Directory.CreateDirectory(Path.Combine(first, "www", "js")); Directory.CreateDirectory(second);
    File.WriteAllText(Path.Combine(first, "Game.exe"), "not executed"); File.WriteAllText(Path.Combine(second, "Game.exe"), "not executed");
    File.WriteAllText(Path.Combine(first, "uninstall.exe"), "not executed"); File.WriteAllText(Path.Combine(first, "www", "js", "rpg_core.js"), "");
    var scanner = new GameScanner(Path.Combine(repo, "data"));
    var candidates = await scanner.ScanAsync([Path.Combine(f.Root, "遊戲庫")], null, default);
    Check(candidates.Count == 2, "missed or duplicated exe");
    Check(candidates.Single(c => c.ExePath.StartsWith(first)).Slots.Single().Patterns.Contains("*.rpgsave"), "MV rule missing");
    using var cts = new CancellationTokenSource(); cts.Cancel(); await Throws<OperationCanceledException>(() => scanner.ScanAsync([f.Root], null, cts.Token));
    // Known manifest directory, no engine execution required.
    var known = Path.Combine(f.Root, "Anyway"); Directory.CreateDirectory(known); File.WriteAllText(Path.Combine(known, "Game.exe"), "");
    Check(scanner.Identify(Path.Combine(known, "Game.exe")).Evidence.Contains("Ludusavi"), "pinned manifest not used");
});
await Test("Google Drive adapter: chunked upload, completion verification and uncertain retry", async () =>
{
    var f = New("drive-http"); using var server = new FakeDriveHandler(); using var http = new HttpClient(server);
    var drive = new GoogleDriveStore(http, _ => Task.FromResult("test-token"), f.A);
    await drive.CheckAccountAsync(default);
    var bytes = RandomNumberGenerator.GetBytes(9 * 1024 * 1024); File.WriteAllBytes(f.AFile, bytes);
    var upload = await f.ASnapshots.CaptureAsync(f.AGame, [], default, false);
    await drive.PutGameAsync(f.AGame.Portable(), default);
    server.FailAfterArchiveCommit = true;
    await Throws<HttpRequestException>(() => drive.UploadAsync(upload, default));
    Check((await drive.ListSnapshotsAsync(f.AGame.Id, default)).Count == 0, "incomplete upload published");
    await drive.UploadAsync(upload, default); await drive.UploadAsync(upload, default);
    Check(server.ArchiveCount == 1, "uncertain retry created duplicate archive");
    Check(server.ChunkCount >= 2, "did not use chunking");
    var versions = await drive.ListSnapshotsAsync(f.AGame.Id, default); Check(versions.Count == 1, "duplicate commit");
    var destination = Path.Combine(f.Root, "downloaded.zip"); await drive.DownloadAsync(versions[0], destination, default);
    SnapshotService.ValidateArchive(f.AGame, versions[0], destination, default);
    Check((await drive.ListGamesAsync(default)).Single().Slots.All(s => s.Path.Length == 0), "uploaded absolute local paths");
    server.Unauthorized = true; await Throws<AuthenticationRequiredException>(() => drive.ListGamesAsync(default));
});
await Test("RGSS rule excludes bundled game database files in Data subdirectory", async () =>
{
    var f = New("rgss"); var dir = Path.Combine(f.Root, "RGSS game"); Directory.CreateDirectory(Path.Combine(dir, "Data"));
    var exe = Path.Combine(dir, "Game.exe"); File.WriteAllText(exe, "not executed"); File.WriteAllText(Path.Combine(dir, "Game.rgss3a"), "engine");
    File.WriteAllText(Path.Combine(dir, "Save01.rvdata2"), "save"); File.WriteAllText(Path.Combine(dir, "Data", "System.rvdata2"), "game assets");
    var scanner = new GameScanner(Path.Combine(repo, "data")); var candidate = scanner.Identify(exe);
    var game = f.AGame with { ExePath = exe, Slots = candidate.Slots };
    var inventory = await Task.Run(() => f.ASnapshots.Inventory(game));
    Check(inventory.Files.Count == 1 && inventory.Files[0].RelativePath == "Save01.rvdata2", "game database included in save backup");
});
await Test("Google Drive adapter: pagination and account mismatch rejection", async () =>
{
    var f = New("drive-pages"); using var server = new FakeDriveHandler(); using var http = new HttpClient(server);
    var drive = new GoogleDriveStore(http, _ => Task.FromResult("test-token"), f.A); await drive.CheckAccountAsync(default);
    for (var i = 0; i < 3; i++) await drive.PutGameAsync(f.AGame.Portable() with { Id = Guid.NewGuid().ToString("N"), Name = "Game " + i }, default);
    Check((await drive.ListGamesAsync(default)).Count == 3 && server.PageRequests > 0, "pagination dropped games");
    server.AccountId = "different-account"; await Throws<AuthenticationRequiredException>(() => drive.CheckAccountAsync(default));
});
await Test("Windows process monitor: real exe completion and child process tracking", async () =>
{
    var stub = Path.Combine(repo, "tests", "GameStub", "bin", "Debug", "net10.0", "GameStub.exe");
    Check(File.Exists(stub), "build GameStub before running tests");
    var game = new GameProfile { ExePath = stub }; var monitor = new WindowsGameMonitor();
    var flag = Path.Combine(Path.GetDirectoryName(stub)!, "spawn-child"); if (File.Exists(flag)) File.Delete(flag);
    Check(await monitor.LaunchAndWaitAsync(game, default), "direct exe considered uncertain");
    Check(File.Exists(Path.Combine(Path.GetDirectoryName(stub)!, "stub.sav")), "game did not write save");
    File.WriteAllText(flag, "1"); var watch = Stopwatch.StartNew();
    try { Check(await monitor.LaunchAndWaitAsync(game, default), "child process completion uncertain"); Check(watch.ElapsedMilliseconds >= 3500, "did not wait for child"); }
    finally { File.Delete(flag); }
});
await Test("cloud advances during download: do not launch with silently stale progress", async () =>
{
    var f = New("advance-during-download"); File.WriteAllText(f.AFile, "base"); await f.ASync.SynchronizeAsync(f.AGame, default); await f.BSync.SynchronizeAsync(f.BGame, default);
    File.WriteAllText(f.AFile, "remote one"); await f.ASync.SynchronizeAsync(f.AGame, default);
    var race = new FaultCloud(f.Cloud) { AfterDownload = async () => { File.WriteAllText(f.AFile, "remote two"); await f.ASync.SynchronizeAsync(f.AGame, default); } };
    var engine = new SyncEngine(f.B, f.BSnapshots, race, f.Monitor);
    await Throws<ConflictException>(() => engine.SynchronizeAsync(f.BGame, default));
    Check(File.ReadAllText(f.BFile) == "remote one", "unexpected download contents");
});
await Test("changing save is detected during stability window", async () =>
{
    var f = New("changing"); File.WriteAllText(f.AFile, "before");
    var capture = f.ASnapshots.CaptureAsync(f.AGame, [], default); await Task.Delay(300); File.WriteAllText(f.AFile, "during");
    await Throws<IOException>(async () => await capture);
    Check(f.A.All<Snapshot>("snapshot").Count == 0, "unstable backup committed");
});
await Test("game starts mid-restore: durable journal recovers partial replacement", async () =>
{
    var f = New("mid-restore"); File.WriteAllText(f.AFile, "older"); var second = Path.Combine(Path.GetDirectoryName(f.AFile)!, "second.sav"); File.WriteAllText(second, "older-second");
    var target = await f.ASnapshots.CaptureAsync(f.AGame, [], default, false);
    File.WriteAllText(f.AFile, "current"); File.WriteAllText(second, "current-second"); var calls = 0;
    await Throws<IOException>(() => f.ASnapshots.RestoreAsync(f.AGame, target.Snapshot, target.ArchivePath, () => ++calls >= 4, default));
    Check(f.A.Get<RestoreJournal>("restore", f.AGame.Id) is not null, "partial restore journal discarded");
    f.ASnapshots.Recover(f.AGame, () => false);
    Check(File.ReadAllText(f.AFile) == "current" && File.ReadAllText(second) == "current-second", "partial restore did not recover both saves");
});

var report = Path.Combine(runRoot, "results.json"); File.WriteAllText(report, Json.Write(results));
Console.WriteLine($"{results.Count - failed}/{results.Count} passed. Report: {report}");
Environment.ExitCode = failed == 0 ? 0 : 1;

sealed class FakeMonitor : IGameMonitor
{
    public bool Running { get; set; }
    public bool IsRunning(GameProfile game) => Running;
    public Task<bool> LaunchAndWaitAsync(GameProfile game, CancellationToken ct) => Task.FromResult(true);
}
sealed class Fixture
{
    public string Root { get; } public LocalStore A { get; } public LocalStore B { get; }
    public GameProfile AGame { get; } public GameProfile BGame { get; }
    public string AFile { get; } public string BFile { get; }
    public SnapshotService ASnapshots { get; } public SnapshotService BSnapshots { get; }
    public DirectoryCloudStore Cloud { get; } public FakeMonitor Monitor { get; } = new();
    public SyncEngine ASync { get; } public SyncEngine BSync { get; }
    public Fixture(string root)
    {
        Root = root; A = new(Path.Combine(root, "A-state")); B = new(Path.Combine(root, "B-state"));
        var aSave = Path.Combine(root, "A 遊戲", "存檔"); var bSave = Path.Combine(root, "B different", "進度"); Directory.CreateDirectory(aSave); Directory.CreateDirectory(bSave);
        AGame = new() { Name = "測試遊戲", Confirmed = true, Slots = [new() { Path = aSave }] };
        BGame = AGame with { Slots = AGame.Slots.Select(s => s with { Path = bSave }).ToList() };
        A.SaveGame(AGame); B.SaveGame(BGame); AFile = Path.Combine(aSave, "進度.sav"); BFile = Path.Combine(bSave, "進度.sav");
        ASnapshots = new(A); BSnapshots = new(B); Cloud = new(Path.Combine(root, "cloud"));
        ASync = new(A, ASnapshots, Cloud, Monitor); BSync = new(B, BSnapshots, Cloud, Monitor);
    }
}
sealed class FaultCloud(ICloudStore inner) : ICloudStore
{
    public Func<Task>? AfterDownload { get; set; }
    public bool FailUpload { get; set; } public bool ThrowAfterUpload { get; set; } public bool AuthFailure { get; set; }
    public Task<List<CloudGame>> ListGamesAsync(CancellationToken ct) => inner.ListGamesAsync(ct);
    public Task PutGameAsync(CloudGame game, CancellationToken ct) => inner.PutGameAsync(game, ct);
    public Task<List<Snapshot>> ListSnapshotsAsync(string gameId, CancellationToken ct) => AuthFailure ? throw new AuthenticationRequiredException("expired") : inner.ListSnapshotsAsync(gameId, ct);
    public async Task UploadAsync(PendingUpload upload, CancellationToken ct)
    { if (FailUpload) throw new HttpRequestException("offline"); await inner.UploadAsync(upload, ct); if (ThrowAfterUpload) { ThrowAfterUpload = false; throw new HttpRequestException("response lost"); } }
    public async Task DownloadAsync(Snapshot snapshot, string destination, CancellationToken ct) { await inner.DownloadAsync(snapshot, destination, ct); if (AfterDownload is not null) await AfterDownload(); }
}
sealed class FakeDriveHandler : HttpMessageHandler
{
    private sealed class Stored(string id, string name, Dictionary<string, string> props)
    { public string Id = id, Name = name; public Dictionary<string, string> Props = props; public byte[] Bytes = []; public object Meta() => new { id = Id, name = Name, size = Bytes.Length.ToString(), md5Checksum = Convert.ToHexStringLower(MD5.HashData(Bytes)) }; }
    private readonly Dictionary<string, Stored> files = []; private readonly Dictionary<string, (Stored File, MemoryStream Content)> sessions = [];
    private int next;
    public bool FailAfterArchiveCommit { get; set; } public bool Unauthorized { get; set; } public string AccountId { get; set; } = "account-one";
    public int ChunkCount { get; private set; } public int PageRequests { get; private set; }
    public int ArchiveCount => files.Values.Count(f => f.Props.GetValueOrDefault("kind") == "archive");
    private static HttpResponseMessage Response(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(Json.Write(body), Encoding.UTF8, "application/json") };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Unauthorized || request.Headers.Authorization?.Parameter != "test-token") return Response(new { error = "unauthorized" }, HttpStatusCode.Unauthorized);
        var uri = request.RequestUri!; var path = uri.AbsolutePath;
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "");
        if (path.EndsWith("/about")) return Response(new { user = new { permissionId = AccountId, emailAddress = "test@example.invalid" } });
        if (path.EndsWith("/generateIds")) return Response(new { ids = new[] { "drive" + ++next } });
        if (path.StartsWith("/session/"))
        {
            ChunkCount++; var session = sessions[path]; var content = await request.Content!.ReadAsByteArrayAsync(ct);
            if (request.Content.Headers.ContentRange!.From != session.Content.Length) return Response(new { error = "offset" }, HttpStatusCode.BadRequest);
            session.Content.Write(content);
            if (session.Content.Length < request.Content.Headers.ContentRange.Length)
            { var partial = Response(new { }, (HttpStatusCode)308); partial.Headers.TryAddWithoutValidation("Range", "bytes=0-" + (session.Content.Length - 1)); return partial; }
            session.File.Bytes = session.Content.ToArray(); files[session.File.Id] = session.File;
            if (FailAfterArchiveCommit && session.File.Props.GetValueOrDefault("kind") == "archive") { FailAfterArchiveCommit = false; throw new HttpRequestException("response lost after commit"); }
            return Response(session.File.Meta());
        }
        if (request.Method == HttpMethod.Post && path.EndsWith("/files"))
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); var root = document.RootElement;
            var file = new Stored(root.GetProperty("id").GetString()!, root.GetProperty("name").GetString()!, root.GetProperty("appProperties").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!));
            if (query.GetValueOrDefault("uploadType") == "resumable")
            { var key = "/session/" + file.Id; sessions[key] = (file, new()); var response = Response(new { }); response.Headers.Location = new Uri("https://www.googleapis.com" + key); return response; }
            files[file.Id] = file; return Response(file.Meta());
        }
        if (path.EndsWith("/files") && request.Method == HttpMethod.Get)
        {
            IEnumerable<Stored> matching = files.Values; var q = query.GetValueOrDefault("q") ?? "";
            foreach (Match match in Regex.Matches(q, "key='([^']+)' and value='([^']+)'")) matching = matching.Where(f => f.Props.GetValueOrDefault(match.Groups[1].Value) == match.Groups[2].Value).ToList();
            var name = Regex.Match(q, "(?:^| and )name='([^']+)'"); if (name.Success) matching = matching.Where(f => f.Name == name.Groups[1].Value);
            var list = matching.OrderBy(f => f.Id).ToList(); var offset = int.Parse(query.GetValueOrDefault("pageToken") ?? "0"); if (offset > 0) PageRequests++;
            if (offset + 1 < list.Count) return Response(new { nextPageToken = (offset + 1).ToString(), files = list.Skip(offset).Take(1).Select(f => f.Meta()).ToArray() });
            return Response(new { files = list.Skip(offset).Take(1).Select(f => f.Meta()).ToArray() });
        }
        if (request.Method == HttpMethod.Get && path.Contains("/files/"))
        {
            if (!files.TryGetValue(path.Split('/').Last(), out var file)) return Response(new { }, HttpStatusCode.NotFound);
            if (query.GetValueOrDefault("alt") == "media") return new(HttpStatusCode.OK) { Content = new ByteArrayContent(file.Bytes) };
            return Response(file.Meta());
        }
        throw new InvalidOperationException("Unexpected Drive request: " + request.Method + " " + uri);
    }
}

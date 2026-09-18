using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SaveCloud.App;
using SaveCloud.Core;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(false);
var options = new JsonSerializerOptions(Json.Options) { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };
Bridge? bridge = null;
try
{
    if (args.Length != 1) throw new ArgumentException("需要資料目錄。");
    var input = await Console.In.ReadToEndAsync();
    if (input.Length > 1024 * 1024) throw new InvalidDataException("請求過大。");
    var request = JsonSerializer.Deserialize<Request>(input, options) ?? throw new InvalidDataException("請求格式錯誤。");
    bridge = new Bridge(args[0]);
    // 追蹤遊戲的程序不能長時間占用操作鎖；進度結算時才重新取得鎖。
    if (request.Action == "track") await bridge.TrackAsync(request);
    using var operationLock = await bridge.LockAsync();
    var result = await bridge.ExecuteAsync(request);
    Console.WriteLine(JsonSerializer.Serialize(new { ok = true, data = result }, options));
}
catch (Exception error)
{
    var code = error switch { ConflictException => "conflict", AuthenticationRequiredException => "auth", HttpRequestException or TaskCanceledException => "network", _ => "error" };
    Console.WriteLine(JsonSerializer.Serialize(new { ok = false, code, message = error.Message, heads = (error as ConflictException)?.Heads }, options));
}

public sealed record Request(string Action, int? GameId = null, string? Name = null, string? ExePath = null,
    List<SaveSlot>? Slots = null, string? CloudId = null, string? SnapshotId = null, string? ClientPath = null,
    bool Confirmed = false, bool Offline = false, int? ProcessId = null);
public sealed record Binding(string CloudId, bool Enabled);

public sealed class Bridge
{
    private readonly LocalStore store;
    private readonly SnapshotService snapshots;
    private readonly WindowsGameMonitor monitor;
    private readonly GoogleAuth auth;
    private readonly ICloudStore cloud;
    private readonly SyncEngine engine;
    private readonly CancellationToken ct = CancellationToken.None;
    public Bridge(string directory)
    {
        store = new(directory); snapshots = new(store); monitor = new(store);
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        auth = new(directory, http); cloud = new GoogleDriveStore(http, auth.AccessTokenAsync, store);
        engine = new(store, snapshots, cloud, monitor);
    }
    public async Task<FileStream> LockAsync()
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return new FileStream(Path.Combine(store.DataDirectory, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 600) { await Task.Delay(500); }
        }
    }
    private Binding? Mapping(int id) => store.Get<Binding>("reina", id.ToString());
    private GameProfile? Game(int id) => Mapping(id) is { } binding ? store.Get<GameProfile>("game", binding.CloudId) : null;
    private void Idle(GameProfile game)
    {
        if (store.Get<string>("active", game.Id) is not null || monitor.IsRunning(game))
            throw new IOException("遊戲仍在執行，或等待確認遊戲已結束。請在存檔頁確認。");
        snapshots.Recover(game, () => monitor.IsRunning(game));
    }
    private object Status(int? id)
    {
        var game = id is { } key ? Game(key) : null;
        var pending = store.All<PendingUpload>("pending");
        return new { hasClient = auth.HasClient, signedIn = auth.HasToken, game, enabled = id is { } k && Mapping(k)?.Enabled == true,
            pending = game is null ? pending.Count : pending.Count(p => p.Snapshot.GameId == game.Id),
            active = game is not null && store.Get<string>("active", game.Id) is not null,
            activeCount = store.All<string>("active").Count, retryCount = store.All<string>("retry").Count,
            last = game is null ? null : store.All<Snapshot>("snapshot").Where(s => s.GameId == game.Id).OrderByDescending(s => s.CreatedUtc).FirstOrDefault() };
    }
    private async Task Sync(GameProfile game)
    {
        try { await engine.SynchronizeAsync(game, ct); game.Status = "同步完成"; store.Remove("retry", game.Id); }
        catch (Exception e) { game.Status = e.Message; throw; }
        finally { store.SaveGame(game); }
    }
    private async Task Backup(GameProfile game)
    {
        Idle(game);
        store.Put("retry", game.Id, "等待備份及上傳");
        await engine.QueueOfflineAsync(game, ct);
        await Sync(game);
    }
    public async Task TrackAsync(Request request)
    {
        var game = Game(request.GameId ?? throw new InvalidDataException());
        if (game is null || Mapping(request.GameId!.Value)?.Enabled != true) return;
        var reliable = false;
        try { reliable = await monitor.ObserveAsync(game, request.ProcessId ?? throw new InvalidDataException(), ct); }
        catch (Exception e) { game.Status = "等待確認遊戲已結束：" + e.Message; }
        using var operationLock = await LockAsync();
        if (reliable)
        {
            store.Remove("active", game.Id);
            try { await Backup(game); }
            catch (Exception e) { game.Status = e.Message; }
        }
        else game.Status = "等待確認遊戲已結束";
        store.SaveGame(game);
    }
    public async Task<object?> ExecuteAsync(Request request)
    {
        var id = request.GameId ?? 0; var game = Game(id);
        switch (request.Action)
        {
            case "status": case "track": return Status(request.GameId);
            case "import-client": auth.ImportClient(request.ClientPath ?? ""); return Status(id);
            case "login": await auth.SignInAsync(ct); return Status(id);
            case "cloud-games": return await cloud.ListGamesAsync(ct);
            case "retry":
                foreach (var pendingGame in store.Games().Where(p => store.Get<string>("active", p.Id) is null &&
                    (store.Pending(p.Id).Count > 0 || store.Get<string>("retry", p.Id) is not null)))
                {
                    if (!store.All<Binding>("reina").Any(b => b.CloudId == pendingGame.Id && b.Enabled)) continue;
                    try { await Backup(pendingGame); } catch (Exception e) { pendingGame.Status = e.Message; store.SaveGame(pendingGame); }
                }
                return Status(null);
            case "identify":
                var exe = request.ExePath ?? throw new InvalidDataException("請先選擇本機主程式。");
                if (!File.Exists(exe)) throw new FileNotFoundException("找不到遊戲主程式。");
                return new GameScanner(Path.Combine(AppContext.BaseDirectory, "data")).Identify(exe);
            case "preview": case "configure":
                if (game is not null) Idle(game);
                var profile = new GameProfile { Name = request.Name ?? "", ExePath = request.ExePath ?? "", Slots = request.Slots ?? [], Confirmed = true };
                if (request.CloudId is { Length: > 0 } cloudId)
                {
                    var remote = (await cloud.ListGamesAsync(ct)).Single(g => g.Id == cloudId);
                    if (remote.Slots.Count != profile.Slots.Count) throw new InvalidDataException("請依雲端每個存檔位置依序連結本機路徑。");
                    profile = profile with { Id = remote.Id, Name = remote.Name, Source = remote.Source,
                        Slots = remote.Slots.Select((slot, index) => slot with { Path = profile.Slots[index].Path }).ToList() };
                }
                SafePaths.Validate(profile);
                var inventory = snapshots.Inventory(profile);
                if (request.Action == "preview") return new { profile, files = inventory.Files.Take(200).ToList(), total = inventory.Files.Count };
                if (!request.Confirmed) throw new InvalidOperationException("請先檢視並確認待備份檔案。");
                if (game is not null) throw new InvalidOperationException("此遊戲已設定。要管理其他版本，請在遊戲庫建立另一筆遊戲。");
                if (store.All<Binding>("reina").Any(b => b.CloudId == profile.Id)) throw new InvalidOperationException("這個雲端遊戲已連結另一筆本機遊戲。");
                store.SaveGame(profile); store.Put("reina", id.ToString(), new Binding(profile.Id, true)); return Status(id);
            case "before-launch":
                if (game is null || Mapping(id)?.Enabled != true) return null;
                Idle(game);
                if (request.ExePath != game.ExePath) throw new IOException("主程式路徑已變更，請先在存檔頁重新連結。");
                if (request.Offline)
                {
                    if (store.Get<string>("offline-allowed", game.Id) is null) throw new IOException("請先嘗試同步。");
                }
                else
                {
                    store.Remove("offline-allowed", game.Id);
                    try { await Sync(game); }
                    catch (Exception e) when (e is AuthenticationRequiredException or HttpRequestException or TaskCanceledException)
                    { store.Put("offline-allowed", game.Id, "網路或登入失敗"); throw; }
                }
                store.Remove("offline-allowed", game.Id);
                store.Put("active", game.Id, "等待遊戲結束"); return null;
            case "launch-failed":
                if (game is not null && !monitor.IsRunning(game)) store.Remove("active", game.Id);
                return null;
        }
        if (game is null) throw new InvalidOperationException("請先設定雲端存檔。");
        if (request.Action == "confirm-ended")
        {
            if (!request.Confirmed || monitor.IsRunning(game)) throw new IOException("遊戲仍在執行。");
            store.Remove("active", game.Id); await Backup(game); return Status(id);
        }
        Idle(game);
        switch (request.Action)
        {
            case "relink":
                if (!request.Confirmed || !File.Exists(request.ExePath)) throw new IOException("請確認新的主程式路徑。");
                game.ExePath = request.ExePath!; store.SaveGame(game); break;
            case "pause": store.Put("reina", id.ToString(), new Binding(game.Id, !Mapping(id)!.Enabled)); break;
            case "sync": await Sync(game); break;
            case "backup": await Backup(game); break;
            case "history": return await cloud.ListSnapshotsAsync(game.Id, ct);
            case "resolve":
                if (!request.Confirmed) throw new InvalidOperationException("請先確認要保留的進度。");
                var chosen = request.SnapshotId is null ? null : (await cloud.ListSnapshotsAsync(game.Id, ct)).Single(s => s.Id == request.SnapshotId);
                await engine.ResolveAsync(game, chosen, ct); game.Status = "同步完成"; store.SaveGame(game); break;
            default: throw new InvalidDataException("不支援的操作。");
        }
        return Status(id);
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SaveCloud.Core;

public sealed class GoogleDriveStore(HttpClient http, Func<CancellationToken, Task<string>> accessToken, LocalStore local) : ICloudStore
{
    private const string Api = "https://www.googleapis.com/drive/v3/";
    private string? folderId;
    private sealed record DriveFile(string Id, string Name, long Size, string Md5);
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await accessToken(ct));
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        { response.Dispose(); throw new AuthenticationRequiredException("Google 授權已失效，請重新登入。"); }
        return response;
    }
    private static async Task CheckAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var message = response.StatusCode switch
        {
            HttpStatusCode.Forbidden => "Google Drive 拒絕存取。請檢查 Drive API 是否啟用、授權範圍及雲端容量。",
            HttpStatusCode.TooManyRequests => "Google Drive 暫時限制請求，備份已保留，請稍後重試。",
            HttpStatusCode.NotFound => "找不到雲端檔案，可能已被移除。",
            _ => $"Google Drive 連線失敗（HTTP {(int)response.StatusCode}），請稍後重試。"
        };
        // 回應本文可能包含帳號資料，因此不顯示原始內容。
        _ = await response.Content.ReadAsByteArrayAsync(ct);
        throw new HttpRequestException(message, null, response.StatusCode);
    }
    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("'", "\\'");
    private static string Props(string kind) => $"trashed=false and appProperties has {{ key='savecloud' and value='v1' }} and appProperties has {{ key='kind' and value='{Escape(kind)}' }}";
    private async Task<List<DriveFile>> ListAsync(string query, CancellationToken ct)
    {
        var files = new List<DriveFile>(); string? page = null;
        do
        {
            var url = Api + "files?spaces=drive&pageSize=1000&fields=nextPageToken,files(id,name,size,md5Checksum)&q=" + Uri.EscapeDataString(query);
            if (page is not null) url += "&pageToken=" + Uri.EscapeDataString(page);
            using var request = new HttpRequestMessage(HttpMethod.Get, url); using var response = await SendAsync(request, ct); await CheckAsync(response, ct);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            foreach (var f in json.RootElement.GetProperty("files").EnumerateArray()) files.Add(Parse(f));
            page = json.RootElement.TryGetProperty("nextPageToken", out var token) ? token.GetString() : null;
        } while (page is not null);
        return files;
    }
    private static DriveFile Parse(JsonElement f) => new(f.GetProperty("id").GetString()!, f.GetProperty("name").GetString()!, f.TryGetProperty("size", out var size) ? long.Parse(size.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : 0, f.TryGetProperty("md5Checksum", out var md5) ? md5.GetString()! : "");
    private async Task<DriveFile?> GetAsync(string id, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Api + "files/" + Uri.EscapeDataString(id) + "?fields=id,name,size,md5Checksum");
        using var response = await SendAsync(request, ct); if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await CheckAsync(response, ct); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); return Parse(json.RootElement);
    }
    public async Task<string> CheckAccountAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Api + "about?fields=user(permissionId,emailAddress)");
        using var response = await SendAsync(request, ct); await CheckAsync(response, ct);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); var user = json.RootElement.GetProperty("user");
        var id = user.GetProperty("permissionId").GetString()!;
        var bound = local.Get<string>("setting", "accountId");
        if (bound is not null && bound != id) throw new AuthenticationRequiredException("此電腦的資料已連結另一個 Google 帳號。請登入原帳號，避免將備份傳到錯誤帳號。");
        local.Put("setting", "accountId", id); folderId = null;
        return user.GetProperty("emailAddress").GetString()!;
    }
    private async Task<string> ReserveIdAsync(string key, CancellationToken ct)
    {
        var cached = local.Get<string>("driveId", key); if (cached is not null) return cached;
        using var request = new HttpRequestMessage(HttpMethod.Get, Api + "files/generateIds?count=1&space=drive&type=files");
        using var response = await SendAsync(request, ct); await CheckAsync(response, ct);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); var id = json.RootElement.GetProperty("ids")[0].GetString()!;
        local.Put("driveId", key, id); return id;
    }
    private async Task<string> RootAsync(CancellationToken ct)
    {
        if (folderId is not null) return folderId;
        var existing = await ListAsync(Props("root") + " and mimeType='application/vnd.google-apps.folder'", ct);
        if (existing.Count > 0) return folderId = existing.OrderBy(f => f.Id, StringComparer.Ordinal).First().Id;
        var id = await ReserveIdAsync("root", ct);
        if (await GetAsync(id, ct) is null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Api + "files")
            { Content = new StringContent(Json.Write(new { id, name = "遊戲存檔 SaveCloud", mimeType = "application/vnd.google-apps.folder", appProperties = new { savecloud = "v1", kind = "root" } }), Encoding.UTF8, "application/json") };
            using var response = await SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.Conflict) await CheckAsync(response, ct);
        }
        return folderId = id;
    }
    private async Task<string> ReadTextAsync(string id, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Api + "files/" + Uri.EscapeDataString(id) + "?alt=media");
        using var response = await SendAsync(request, ct); await CheckAsync(response, ct);
        if (response.Content.Headers.ContentLength > 32 * 1024 * 1024) throw new InvalidDataException("雲端清單過大。");
        using var input = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream();
        var buffer = new byte[65536]; int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0) { if (output.Length + read > 32 * 1024 * 1024) throw new InvalidDataException("雲端清單過大。"); output.Write(buffer, 0, read); }
        return Encoding.UTF8.GetString(output.ToArray());
    }
    public async Task<List<CloudGame>> ListGamesAsync(CancellationToken ct)
    {
        var files = await ListAsync(Props("game"), ct); var games = new List<CloudGame>();
        foreach (var file in files)
        {
            var game = Json.Read<CloudGame>(await ReadTextAsync(file.Id, ct));
            if (game.SchemaVersion != 1 || !Guid.TryParseExact(game.Id, "N", out _) || game.Slots.Count == 0 || game.Slots.Any(s => !Guid.TryParseExact(s.Id, "N", out _))) throw new InvalidDataException("雲端遊戲清單格式不支援。");
            games.Add(game);
        }
        return games.DistinctBy(g => g.Id).OrderBy(g => g.Name).ToList();
    }
    public async Task PutGameAsync(CloudGame game, CancellationToken ct)
    {
        var existing = await ListAsync(Props("game") + $" and appProperties has {{ key='gameId' and value='{Escape(game.Id)}' }}", ct);
        if (existing.Count > 0)
        {
            var saved = Json.Read<CloudGame>(await ReadTextAsync(existing[0].Id, ct));
            if (Json.Write(saved.Slots) != Json.Write(game.Slots)) throw new InvalidOperationException("這款遊戲的雲端存檔位置定義不同，請重新連結或以不同版本新增。");
            local.Put("publishedGame", game.Id, true);
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(Json.Write(game));
        await UploadBytesAsync("game-" + game.Id, game.Id + ".game.json", "game", game.Id, bytes, ct);
        local.Put("publishedGame", game.Id, true);
    }
    public async Task<List<Snapshot>> ListSnapshotsAsync(string gameId, CancellationToken ct)
    {
        var files = await ListAsync(Props("snapshot") + $" and appProperties has {{ key='gameId' and value='{Escape(gameId)}' }}", ct);
        var result = new List<Snapshot>();
        foreach (var f in files)
        {
            var snapshot = Json.Read<Snapshot>(await ReadTextAsync(f.Id, ct)); SnapshotService.ValidateManifest(snapshot);
            if (snapshot.GameId != gameId) throw new InvalidDataException("雲端版本遊戲 ID 不符。");
            result.Add(snapshot);
        }
        return result.DistinctBy(s => s.Id).ToList();
    }
    private async Task UploadBytesAsync(string key, string name, string kind, string gameId, byte[] bytes, CancellationToken ct)
    {
        var id = await ReserveIdAsync(key, ct);
        var md5 = Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(bytes));
        using var input = new MemoryStream(bytes);
        await UploadStreamAsync(id, name, kind, gameId, input, md5, ct);
    }
    private async Task UploadStreamAsync(string id, string name, string kind, string gameId, Stream input, string md5, CancellationToken ct)
    {
        var existing = await GetAsync(id, ct);
        if (existing is not null)
        {
            if (existing.Size != input.Length || existing.Md5 != md5) throw new InvalidDataException("雲端同 ID 檔案校驗不符，已停止覆寫。");
            return;
        }
        var root = await RootAsync(ct);
        var metadata = new { id, name, parents = new[] { root }, appProperties = new { savecloud = "v1", kind, gameId, key = name } };
        using var start = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable")
        { Content = new StringContent(Json.Write(metadata), Encoding.UTF8, "application/json") };
        start.Headers.Add("X-Upload-Content-Type", kind == "archive" ? "application/zip" : "application/json");
        start.Headers.Add("X-Upload-Content-Length", input.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var started = await SendAsync(start, ct); await CheckAsync(started, ct);
        var session = started.Headers.Location ?? throw new IOException("無法建立 Google Drive 上傳工作。");
        if (session.Scheme != "https" || session.Host != "www.googleapis.com") throw new IOException("Google Drive 回傳非預期的上傳位址。");
        var buffer = new byte[8 * 1024 * 1024]; long offset = 0;
        while (offset < input.Length)
        {
            ct.ThrowIfCancellationRequested(); input.Position = offset;
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, input.Length - offset)), ct);
            if (count == 0) throw new EndOfStreamException();
            using var chunk = new HttpRequestMessage(HttpMethod.Put, session) { Content = new ByteArrayContent(buffer, 0, count) };
            chunk.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + count - 1, input.Length);
            using var sent = await SendAsync(chunk, ct);
            if ((int)sent.StatusCode == 308)
            {
                var next = sent.Headers.TryGetValues("Range", out var ranges) && long.TryParse(ranges.Single().Split('-').Last(), out var last) ? last + 1 : 0;
                if (next <= offset || next > offset + count) throw new IOException("雲端未確認上傳區塊，請重試。");
                offset = next;
            }
            else { await CheckAsync(sent, ct); offset += count; }
        }
        var verified = await GetAsync(id, ct);
        if (verified is null || verified.Size != input.Length || verified.Md5 != md5) throw new InvalidDataException("雲端上傳校驗失敗，備份仍保留在本機。");
    }
    public async Task UploadAsync(PendingUpload upload, CancellationToken ct)
    {
        var s = upload.Snapshot; SnapshotService.ValidateManifest(s);
        if (await Task.Run(() => Hashing.FileSha(upload.ArchivePath), ct) != s.ArchiveSha256) throw new InvalidDataException("本機備份已損壞。");
        var id = await ReserveIdAsync("archive-" + s.Id, ct);
        using (var file = File.OpenRead(upload.ArchivePath)) await UploadStreamAsync(id, s.Id + ".zip", "archive", s.GameId, file, s.ArchiveMd5, ct);
        // 壓縮檔完整上傳且驗證成功後才建立可還原紀錄。
        await UploadBytesAsync("snapshot-" + s.Id, s.Id + ".snapshot.json", "snapshot", s.GameId, Encoding.UTF8.GetBytes(Json.Write(s)), ct);
    }
    public async Task DownloadAsync(Snapshot snapshot, string destination, CancellationToken ct)
    {
        var archives = await ListAsync(Props("archive") + $" and appProperties has {{ key='gameId' and value='{Escape(snapshot.GameId)}' }} and name='{Escape(snapshot.Id)}.zip'", ct);
        var archive = archives.FirstOrDefault(f => f.Size == snapshot.ArchiveLength && f.Md5 == snapshot.ArchiveMd5) ?? throw new IOException("找不到完整的雲端備份檔。");
        using var request = new HttpRequestMessage(HttpMethod.Get, Api + "files/" + Uri.EscapeDataString(archive.Id) + "?alt=media");
        using var response = await SendAsync(request, ct); await CheckAsync(response, ct);
        var partial = destination + ".download";
        try
        {
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var input = await response.Content.ReadAsStreamAsync(ct); var buffer = new byte[131072]; int count;
                while ((count = await input.ReadAsync(buffer, ct)) > 0)
                { if (output.Length + count > snapshot.ArchiveLength) throw new InvalidDataException("下載超出預期大小。"); await output.WriteAsync(buffer.AsMemory(0, count), ct); }
                output.Flush(true);
            }
            if (new FileInfo(partial).Length != snapshot.ArchiveLength || await Task.Run(() => Hashing.FileSha(partial), ct) != snapshot.ArchiveSha256) throw new InvalidDataException("下載的備份校驗失敗。");
            File.Move(partial, destination, true);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
}

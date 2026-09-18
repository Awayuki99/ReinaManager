using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SaveCloud.Core;

namespace SaveCloud.App;

public sealed class GoogleAuth(string dataDirectory, HttpClient http)
{
    private record Client(string ClientId, string ClientSecret);
    private record Token(string AccessToken, string RefreshToken, DateTimeOffset ExpiresUtc);
    private readonly SemaphoreSlim gate = new(1, 1);
    private string ClientPath => Path.Combine(dataDirectory, "oauth-client.bin");
    private string TokenPath => Path.Combine(dataDirectory, "tokens.bin");
    public bool HasClient => File.Exists(ClientPath);
    public bool HasToken => File.Exists(TokenPath);
    private static T Read<T>(string path) => Json.Read<T>(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser)));
    private static void Save<T>(string path, T value)
    {
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(Json.Write(value)), null, DataProtectionScope.CurrentUser);
        var temp = path + ".tmp";
        using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { output.Write(encrypted); output.Flush(true); }
        File.Move(temp, path, true);
    }
    public void ImportClient(string path)
    {
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("OAuth 設定檔過大。");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("installed", out var installed)) throw new InvalidDataException("請匯入 Google 的「桌面應用程式」OAuth JSON 設定。");
        var client = new Client(installed.GetProperty("client_id").GetString()!, installed.GetProperty("client_secret").GetString()!);
        if (!client.ClientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(client.ClientSecret)) throw new InvalidDataException("Google OAuth 設定內容不正確。");
        if (HasClient && Read<Client>(ClientPath).ClientId != client.ClientId && HasToken) throw new InvalidOperationException("已有登入資料，請使用與其他電腦相同的 OAuth 應用程式設定。");
        Save(ClientPath, client);
    }
    private static string Base64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Query(Dictionary<string, string> values) => string.Join('&', values.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
    public async Task SignInAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!HasClient) throw new AuthenticationRequiredException("請先匯入 Google OAuth 設定。");
            var client = Read<Client>(ClientPath); var verifier = Base64(RandomNumberGenerator.GetBytes(64)); var state = Base64(RandomNumberGenerator.GetBytes(32));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(5));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
            var url = "https://accounts.google.com/o/oauth2/v2/auth?" + Query(new()
            {
                ["client_id"] = client.ClientId, ["redirect_uri"] = redirect, ["response_type"] = "code",
                ["scope"] = "https://www.googleapis.com/auth/drive.file", ["access_type"] = "offline", ["prompt"] = "consent",
                ["code_challenge"] = Base64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), ["code_challenge_method"] = "S256", ["state"] = state
            });
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            string? code = null;
            while (code is null)
            {
                using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
                using var stream = connection.GetStream(); using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token); requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var line = await reader.ReadLineAsync(requestTimeout.Token);
                if (line is null || line.Length > 8192 || !line.StartsWith("GET /", StringComparison.Ordinal)) continue;
                var pieces = line.Split(' '); if (pieces.Length != 3) continue;
                var headerSize = line.Length; string? headerLine;
                while (!string.IsNullOrEmpty(headerLine = await reader.ReadLineAsync(requestTimeout.Token)))
                { headerSize += headerLine.Length; if (headerSize > 16384) throw new InvalidDataException("登入回呼內容過大。"); }
                var uri = new Uri(new Uri(redirect), pieces[1]);
                var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
                    .Where(p => p.Length == 2).GroupBy(p => Uri.UnescapeDataString(p[0])).ToDictionary(g => g.Key, g => Uri.UnescapeDataString(g.First()[1].Replace('+', ' ')));
                var valid = uri.AbsolutePath == "/" && query.GetValueOrDefault("state") == state;
                var body = Encoding.UTF8.GetBytes(valid ? "<!doctype html><meta charset='utf-8'><title>SaveCloud</title><p>Google 授權回應已收到，請回到遊戲存檔程式。</p>" : "Invalid authorization state.");
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(valid ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, timeout.Token); await stream.WriteAsync(body, timeout.Token);
                if (!valid) continue;
                code = query.GetValueOrDefault("code") ?? throw new AuthenticationRequiredException("Google 登入已取消或未授予雲端硬碟權限。");
            }
            var token = await ExchangeAsync(new() { ["client_id"] = client.ClientId, ["client_secret"] = client.ClientSecret, ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = redirect, ["grant_type"] = "authorization_code" }, null, timeout.Token);
            Save(TokenPath, token);
        }
        finally { gate.Release(); }
    }
    private async Task<Token> ExchangeAsync(Dictionary<string, string> parameters, string? refresh, CancellationToken ct)
    {
        using var response = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(parameters), ct);
        if (!response.IsSuccessStatusCode) throw new AuthenticationRequiredException("Google 授權無效或已到期，請重新登入；若使用測試模式，更新權杖可能於 7 天後失效。");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); var root = json.RootElement;
        return new(root.GetProperty("access_token").GetString()!, root.TryGetProperty("refresh_token", out var rt) ? rt.GetString()! : refresh ?? throw new AuthenticationRequiredException("未取得離線授權，請重新登入。"), DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32() - 60));
    }
    public async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!HasToken) throw new AuthenticationRequiredException("請先登入 Google。");
            var token = Read<Token>(TokenPath);
            if (DateTimeOffset.UtcNow >= token.ExpiresUtc)
            {
                var client = Read<Client>(ClientPath);
                token = await ExchangeAsync(new() { ["client_id"] = client.ClientId, ["client_secret"] = client.ClientSecret, ["refresh_token"] = token.RefreshToken, ["grant_type"] = "refresh_token" }, token.RefreshToken, ct);
                Save(TokenPath, token);
            }
            return token.AccessToken;
        }
        finally { gate.Release(); }
    }
}

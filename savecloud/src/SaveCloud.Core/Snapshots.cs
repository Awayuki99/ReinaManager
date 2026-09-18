using System.IO.Compression;
using System.IO.Enumeration;

namespace SaveCloud.Core;

public static class SafePaths
{
    public static void NoLinks(string path)
    {
        for (var cursor = System.IO.Path.GetFullPath(path); !string.IsNullOrEmpty(cursor); cursor = System.IO.Path.GetDirectoryName(cursor))
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"存檔路徑含有連結，請改選實際資料夾：{cursor}");
    }
    public static string Under(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':') || relative.StartsWith('/') ||
            relative.Split('/').Any(s => s is "" or "." or ".." || s.EndsWith('.') || s.EndsWith(' ') || s.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || System.Text.RegularExpressions.Regex.IsMatch(s, "^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            throw new InvalidDataException("備份含有不安全的檔案路徑。");
        var fullRoot = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        var result = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullRoot, relative));
        if (!result.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("備份路徑超出存檔範圍。");
        NoLinks(result);
        return result;
    }
    public static bool Includes(SaveSlot slot, string relative) => slot.Patterns.Any(p => FileSystemName.MatchesSimpleExpression(p, System.IO.Path.GetFileName(relative), true));
    public static void Validate(GameProfile game)
    {
        if (!Guid.TryParseExact(game.Id, "N", out _) || game.Slots.Count == 0) throw new InvalidDataException("請先設定存檔位置。");
        var roots = new List<string>();
        foreach (var slot in game.Slots)
        {
            if (!Guid.TryParseExact(slot.Id, "N", out _) || game.Slots.Count(s => s.Id == slot.Id) != 1 || slot.Patterns.Length == 0)
                throw new InvalidDataException("存檔位置設定不正確。");
            if (string.IsNullOrWhiteSpace(slot.Path) || !System.IO.Path.IsPathFullyQualified(slot.Path)) throw new InvalidDataException("請指定完整的本機存檔路徑。");
            var path = System.IO.Path.GetFullPath(slot.Path).TrimEnd('\\', '/'); NoLinks(path);
            if (path.Skip(2).Contains(':')) throw new InvalidDataException("存檔位置不可包含替代資料串流。");
            if (path == System.IO.Path.GetPathRoot(path)?.TrimEnd('\\', '/') ||
                new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) }.Any(x => path.Equals(x, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("請選擇遊戲專用的存檔資料夾，不可選整個磁碟或使用者／系統資料夾。");
            if (roots.Any(x => path.Equals(x, StringComparison.OrdinalIgnoreCase) || path.StartsWith(x + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || x.StartsWith(path + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("存檔位置互相重疊，請合併設定。");
            roots.Add(path);
            if (slot.IsFile && Directory.Exists(path) || !slot.IsFile && File.Exists(path)) throw new InvalidDataException("存檔位置的檔案／資料夾類型不符。");
            if (slot.IsFile && path.Equals(game.ExePath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("遊戲主程式不是存檔。");
        }
    }
}

public sealed class SnapshotService(LocalStore store)
{
    public LocalInventory Inventory(GameProfile game, CancellationToken ct = default)
    {
        SafePaths.Validate(game);
        var files = new List<SavedFile>(); var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var slot in game.Slots)
        {
            IEnumerable<string> paths = slot.IsFile ? (File.Exists(slot.Path) ? [slot.Path] : []) : Walk(slot.Path, ct, slot.Recursive);
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested(); SafePaths.NoLinks(path);
                var relative = slot.IsFile ? "data" : System.IO.Path.GetRelativePath(slot.Path, path).Replace('\\', '/');
                if (!slot.IsFile && !SafePaths.Includes(slot, relative)) continue;
                // 備份時也檢查檔名，確保產生的壓縮檔能安全還原。
                _ = SafePaths.Under(store.DataDirectory, slot.Id + "/" + relative);
                var info = new FileInfo(path); var file = new SavedFile(slot.Id, relative, info.Length, Hashing.FileSha(path));
                if (!sources.TryAdd(file.ArchivePath, path)) throw new InvalidDataException("存檔包含重複路徑。");
                files.Add(file); total += file.Length;
                if (files.Count > 100000 || total > 20L * 1024 * 1024 * 1024) throw new IOException("單次備份上限為 100,000 個檔案或 20 GB，請縮小存檔範圍。");
            }
        }
        return new(files, sources);
    }
    private static IEnumerable<string> Walk(string path, CancellationToken ct, bool recursive)
    {
        if (!Directory.Exists(path)) yield break;
        var stack = new Stack<string>(); stack.Push(path);
        while (stack.TryPop(out var dir))
        {
            ct.ThrowIfCancellationRequested(); SafePaths.NoLinks(dir);
            foreach (var file in Directory.EnumerateFiles(dir)) yield return file;
            if (recursive) foreach (var sub in Directory.EnumerateDirectories(dir)) { SafePaths.NoLinks(sub); stack.Push(sub); }
        }
    }
    public async Task<PendingUpload> CaptureAsync(GameProfile game, IEnumerable<string> parents, CancellationToken ct, bool stable = true)
    {
        var before = await Task.Run(() => Inventory(game, ct), ct);
        if (stable)
        {
            await Task.Delay(1500, ct);
            if ((await Task.Run(() => Inventory(game, ct), ct)).ContentHash != before.ContentHash) throw new IOException("存檔仍在變更，請稍後重試備份。");
        }
        var snapshot = new Snapshot { GameId = game.Id, Parents = parents.Distinct().ToList(), Files = before.Files, ContentHash = before.ContentHash };
        var destination = store.SnapshotPath(snapshot.Id); var partial = destination + ".part";
        try
        {
            await Task.Run(() =>
            {
                using (var output = File.Open(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
                    foreach (var file in before.Files)
                    {
                        ct.ThrowIfCancellationRequested();
                        using var input = File.Open(before.Sources[file.ArchivePath], FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var entry = zip.CreateEntry(file.ArchivePath, CompressionLevel.Fastest).Open(); input.CopyTo(entry);
                    }
                if (Inventory(game, ct).ContentHash != before.ContentHash) throw new IOException("備份期間存檔有變更，已保留原始存檔，請重試。");
                snapshot = snapshot with { ArchiveLength = new FileInfo(partial).Length, ArchiveSha256 = Hashing.FileSha(partial), ArchiveMd5 = Hashing.FileMd5(partial) };
                ValidateArchive(game, snapshot, partial, ct);
                File.Move(partial, destination);
            }, ct);
            store.Put("snapshot", snapshot.Id, snapshot);
            return new(snapshot, destination);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
    public static void ValidateManifest(Snapshot snapshot)
    {
        if (snapshot.SchemaVersion != 1 || !Guid.TryParseExact(snapshot.Id, "N", out _) || !Guid.TryParseExact(snapshot.GameId, "N", out _) ||
            snapshot.Parents.Any(p => !Guid.TryParseExact(p, "N", out _) || p == snapshot.Id) || snapshot.Files.Count > 100000 ||
            snapshot.Files.Any(f => f.Length < 0 || f.Length > 20L * 1024 * 1024 * 1024) || snapshot.Files.Sum(f => f.Length) > 20L * 1024 * 1024 * 1024 ||
            snapshot.ContentHash != Hashing.Content(snapshot.Files)) throw new InvalidDataException("備份清單不完整或版本不支援。");
    }
    public static void ValidateArchive(GameProfile game, Snapshot snapshot, string archivePath, CancellationToken ct)
    {
        SafePaths.Validate(game); ValidateManifest(snapshot);
        if (snapshot.GameId != game.Id || new FileInfo(archivePath).Length != snapshot.ArchiveLength || Hashing.FileSha(archivePath) != snapshot.ArchiveSha256)
            throw new InvalidDataException("備份校驗失敗，尚未變更本機存檔。");
        using var zip = ZipFile.OpenRead(archivePath);
        if (zip.Entries.Count != snapshot.Files.Count) throw new InvalidDataException("備份檔案數量不符。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in snapshot.Files)
        {
            ct.ThrowIfCancellationRequested(); var slot = game.Slots.SingleOrDefault(s => s.Id == file.SlotId) ?? throw new InvalidDataException("請先連結所有存檔位置。");
            if (!seen.Add(file.ArchivePath)) throw new InvalidDataException("備份含有重複路徑。");
            if (slot.IsFile ? file.RelativePath != "data" : !SafePaths.Includes(slot, file.RelativePath) || !slot.Recursive && file.RelativePath.Contains('/')) throw new InvalidDataException("備份內容不符合存檔範圍。");
            _ = Target(slot, file);
            var entry = zip.GetEntry(file.ArchivePath) ?? throw new InvalidDataException("備份缺少檔案。");
            if (entry.Length != file.Length) throw new InvalidDataException("備份檔案大小不符。");
            using var input = entry.Open(); using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            var buffer = new byte[65536]; long total = 0; int count;
            while ((count = input.Read(buffer)) > 0) { ct.ThrowIfCancellationRequested(); total += count; if (total > file.Length) throw new InvalidDataException("解壓內容超出預期大小。"); hash.AppendData(buffer, 0, count); }
            if (total != file.Length || Convert.ToHexStringLower(hash.GetHashAndReset()) != file.Sha256) throw new InvalidDataException("備份檔案雜湊不符。");
        }
    }
    private static string Target(SaveSlot slot, SavedFile file)
    {
        if (slot.IsFile) { SafePaths.NoLinks(slot.Path); return slot.Path; }
        return SafePaths.Under(slot.Path, file.RelativePath);
    }
    public async Task RestoreAsync(GameProfile game, Snapshot snapshot, string archive, Func<bool> isRunning, CancellationToken ct)
    {
        if (isRunning()) throw new IOException("遊戲執行中，無法還原存檔。");
        await Task.Run(() => ValidateArchive(game, snapshot, archive, ct), ct);
        var backup = await CaptureAsync(game, [], ct);
        if (isRunning()) throw new IOException("遊戲已啟動，已取消還原。");
        var journal = new RestoreJournal(game.Id, backup.Snapshot, backup.ArchivePath, "applying");
        store.Put("restore", game.Id, journal);
        try
        {
            await Task.Run(() => Apply(game, snapshot, archive, isRunning, ct), ct);
            store.CommitRestore(snapshot);
        }
        catch
        {
            // 完整回復落盤前保留紀錄，下次啟動繼續復原。
            if (!isRunning()) { Apply(game, backup.Snapshot, backup.ArchivePath, isRunning, CancellationToken.None); store.Remove("restore", game.Id); }
            throw;
        }
    }
    public void Recover(GameProfile game, Func<bool> isRunning)
    {
        var journal = store.Get<RestoreJournal>("restore", game.Id); if (journal is null) return;
        if (isRunning()) throw new IOException("有未完成的還原，請關閉遊戲後重新啟動本程式。");
        ValidateArchive(game, journal.Backup, journal.BackupPath, CancellationToken.None);
        Apply(game, journal.Backup, journal.BackupPath, isRunning, CancellationToken.None);
        store.Remove("restore", game.Id);
    }
    private void Apply(GameProfile game, Snapshot snapshot, string archive, Func<bool> isRunning, CancellationToken ct)
    {
        ValidateArchive(game, snapshot, archive, ct);
        var current = Inventory(game, ct);
        var desired = snapshot.Files.Select(f => f.ArchivePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var zip = ZipFile.OpenRead(archive);
        foreach (var file in snapshot.Files)
        {
            ct.ThrowIfCancellationRequested(); if (isRunning()) throw new IOException("偵測到遊戲啟動，還原暫停。");
            var target = Target(game.Slots.Single(s => s.Id == file.SlotId), file);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!); SafePaths.NoLinks(target);
            var temp = target + ".savecloud-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { using var input = zip.GetEntry(file.ArchivePath)!.Open(); input.CopyTo(output); output.Flush(true); }
                SafePaths.NoLinks(target); File.Move(temp, target, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        foreach (var file in current.Files.Where(f => !desired.Contains(f.ArchivePath)))
        {
            ct.ThrowIfCancellationRequested(); if (isRunning()) throw new IOException("偵測到遊戲啟動，還原暫停。");
            var target = current.Sources[file.ArchivePath]; SafePaths.NoLinks(target); File.Delete(target);
        }
        if (Inventory(game, ct).ContentHash != snapshot.ContentHash) throw new IOException("還原後校驗失敗，將嘗試復原。");
    }
}

using System.IO.Enumeration;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace SaveCloud.Core;

public sealed class GameScanner(string dataDirectory)
{
    private readonly object manifestLock = new();
    private Dictionary<string, List<(string Name, YamlMappingNode Entry)>>? index;
    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static YamlNode? Child(YamlMappingNode map, string key) => map.Children.TryGetValue(new YamlScalarNode(key), out var node) ? node : null;
    private void LoadManifest()
    {
        lock (manifestLock)
        {
            if (index is not null) return;
            var path = Path.Combine(dataDirectory, "manifest.yaml");
            var version = Json.Read<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dataDirectory, "manifest-version.json")));
            if (Hashing.FileSha(path) != version["sha256"]) throw new InvalidDataException("內建遊戲資料庫校驗失敗，請重新解壓程式。");
            var yaml = new YamlStream(); using var reader = File.OpenText(path); yaml.Load(reader);
            var result = new Dictionary<string, List<(string, YamlMappingNode)>>();
            foreach (var item in ((YamlMappingNode)yaml.Documents[0].RootNode).Children)
            {
                var name = ((YamlScalarNode)item.Key).Value!; var entry = (YamlMappingNode)item.Value;
                var aliases = new List<string> { name };
                if (Child(entry, "installDir") is YamlMappingNode dirs) aliases.AddRange(dirs.Children.Keys.Select(k => ((YamlScalarNode)k).Value!));
                foreach (var alias in aliases.Select(Normalize).Where(x => x.Length > 2).Distinct())
                {
                    if (!result.TryGetValue(alias, out var entries)) result[alias] = entries = [];
                    entries.Add((name, entry));
                }
            }
            index = result;
        }
    }
    public Task<List<Candidate>> ScanAsync(IEnumerable<string> roots, IProgress<string>? progress, CancellationToken ct) => Task.Run(() =>
    {
        LoadManifest(); var results = new List<Candidate>(); var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var notification = System.Diagnostics.Stopwatch.StartNew();
        foreach (var root in roots)
        {
            var stack = new Stack<string>(); stack.Push(Path.GetFullPath(root));
            while (stack.TryPop(out var dir))
            {
                ct.ThrowIfCancellationRequested(); if (!visited.Add(dir)) continue;
                try
                {
                    SafePaths.NoLinks(dir); if (notification.ElapsedMilliseconds > 100) { progress?.Report(dir); notification.Restart(); }
                    var executables = Directory.GetFiles(dir, "*.exe");
                    foreach (var exe in executables)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (Regex.IsMatch(Path.GetFileNameWithoutExtension(exe), "unins|uninstall|setup|crash|report|updat|redist|config|helper|notification|unitycrash|^python|^7z", RegexOptions.IgnoreCase)) continue;
                        SafePaths.NoLinks(exe); results.Add(Identify(exe));
                    }
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                    {
                        var leaf = Path.GetFileName(sub).ToLowerInvariant();
                        if (new[] { "node_modules", ".git", ".tools", "windows", "$recycle.bin", "system volume information" }.Contains(leaf)) continue;
                        if (executables.Length > 0 && (new[] { "renpy", "lib", "locales", "www", "js", "audio", "img", "data", "game" }.Contains(leaf) || leaf.EndsWith("_data", StringComparison.Ordinal))) continue;
                        stack.Push(sub);
                    }
                }
                catch (Exception e) when (e is UnauthorizedAccessException or IOException) { progress?.Report($"略過無法讀取的位置：{dir}"); }
            }
        }
        return results.DistinctBy(x => x.ExePath, StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Name).ToList();
    }, ct);

    public Candidate Identify(string exe)
    {
        LoadManifest(); var dir = Path.GetDirectoryName(exe)!; var folder = Path.GetFileName(dir);
        var slots = new List<SaveSlot>(); var evidence = new List<string>(); var name = folder;
        if (index!.TryGetValue(Normalize(folder), out var matches) && matches.Select(m => m.Name).Distinct().Count() == 1)
        {
            var match = matches[0]; name = match.Name; evidence.Add("Ludusavi 目錄名稱對應，需確認版本");
            if (Child(match.Entry, "files") is YamlMappingNode files)
                foreach (var item in files.Children)
                {
                    if (item.Value is YamlMappingNode meta)
                    {
                        if (Child(meta, "when") is YamlSequenceNode conditions && conditions.Children.Count > 0 &&
                            !conditions.Children.OfType<YamlMappingNode>().Any(c => Child(c, "os") is null || ((YamlScalarNode)Child(c, "os")!).Value == "windows")) continue;
                        if (Child(meta, "tags") is YamlSequenceNode tags && tags.Children.Count > 0 && !tags.Children.OfType<YamlScalarNode>().Any(t => t.Value == "save")) continue;
                    }
                    var template = ((YamlScalarNode)item.Key).Value!;
                    var path = template.Replace("<base>", dir).Replace("<home>", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                        .Replace("<winAppData>", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
                        .Replace("<winLocalAppData>", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
                        .Replace("<winLocalAppDataLow>", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow"))
                        .Replace("<winDocuments>", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
                        .Replace("<winSavedGames>", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games"));
                    if (path.Contains('<') || path.Contains('>')) continue;
                    AddPath(slots, path);
                }
        }
        if (File.Exists(Path.Combine(dir, "www", "js", "rpg_core.js")))
        { evidence.Add("RPG Maker MV"); AddDirectory(slots, Path.Combine(dir, "www", "save"), ["*.rpgsave"]); }
        else if (File.Exists(Path.Combine(dir, "js", "rpg_core.js")))
        { evidence.Add("RPG Maker MV"); AddDirectory(slots, Path.Combine(dir, "save"), ["*.rpgsave"]); }
        if (File.Exists(Path.Combine(dir, "js", "rmmz_core.js")))
        { evidence.Add("RPG Maker MZ"); AddDirectory(slots, Path.Combine(dir, "save"), ["*.rmmzsave"]); }
        foreach (var pair in new[] { ("Game.rgss3a", "*.rvdata2"), ("Game.rgss2a", "*.rvdata"), ("Game.rgssad", "*.rxdata") })
            if (File.Exists(Path.Combine(dir, pair.Item1))) { evidence.Add("RPG Maker RGSS（僅根目錄存檔候選）"); slots.Add(new() { Label = "RGSS 存檔", Path = dir, Patterns = [pair.Item2], Recursive = false }); }
        if (Directory.Exists(Path.Combine(dir, "renpy")))
        {
            evidence.Add("Ren’Py"); AddDirectory(slots, Path.Combine(dir, "game", "saves"), ["*"]);
            var options = Path.Combine(dir, "game", "options.rpy");
            if (File.Exists(options) && new FileInfo(options).Length < 1024 * 1024)
            {
                // 只讀取字面設定，避免為了找路徑而執行遊戲腳本。
                var match = Regex.Match(File.ReadAllText(options), "(?m)^\\s*define\\s+config\\.save_directory\\s*=\\s*[\"']([^\"'\\r\\n]+)[\"']");
                if (match.Success && match.Groups[1].Value.IndexOfAny(['/', '\\', ':']) < 0 && match.Groups[1].Value is not "." and not "..")
                    AddDirectory(slots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RenPy", match.Groups[1].Value), ["*"]);
                else evidence.Add("使用者存檔目錄需手動確認");
            }
            else evidence.Add("使用者存檔目錄需手動確認");
        }
        if (Directory.Exists(Path.Combine(dir, Path.GetFileNameWithoutExtension(exe) + "_Data"))) evidence.Add("Unity；未有資料庫對應時需手動選存檔");
        if (evidence.Count == 0) evidence.Add("未辨識；請確認是遊戲並設定存檔");
        // 辨識來源重疊時保留外層位置，避免同一檔案重複備份。
        slots = slots.DistinctBy(s => s.Path, StringComparer.OrdinalIgnoreCase).ToList();
        return new(name, exe, string.Join(" / ", evidence), slots);
    }
    private static void AddDirectory(List<SaveSlot> slots, string path, string[] patterns) => slots.Add(new() { Label = Path.GetFileName(path), Path = Path.GetFullPath(path), Patterns = patterns });
    private static void AddPath(List<SaveSlot> slots, string path)
    {
        path = path.Replace('/', Path.DirectorySeparatorChar);
        try
        {
            var parent = Path.GetDirectoryName(path); var name = Path.GetFileName(path);
            if (parent is null || parent.Contains('*') || parent.Contains('?') || !Path.IsPathFullyQualified(path)) return;
            if (name.Contains('*') || name.Contains('?')) AddDirectory(slots, parent, [name]);
            else if (File.Exists(path)) slots.Add(new() { Path = Path.GetFullPath(path), Label = name, IsFile = true });
            else if (Directory.Exists(path)) AddDirectory(slots, path, ["*"]);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { }
    }
}

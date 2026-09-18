using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using SaveCloud.Core;

namespace SaveCloud.App;

public sealed class WindowsGameMonitor(LocalStore? store = null) : IGameMonitor
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId; public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId; public int BasePriority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    private static List<(int Id, int Parent)> Tree()
    {
        var handle = CreateToolhelp32Snapshot(2, 0);
        if (handle == new IntPtr(-1)) throw new IOException("無法讀取遊戲程序清單。");
        try
        {
            var result = new List<(int, int)>(); var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExeFile = "" };
            if (!Process32First(handle, ref entry)) throw new IOException("無法追蹤遊戲子程序。");
            do { result.Add(((int)entry.ProcessId, (int)entry.ParentProcessId)); } while (Process32Next(handle, ref entry));
            return result;
        }
        finally { CloseHandle(handle); }
    }
    public bool IsRunning(GameProfile game)
    {
        var saved = store?.Get<Dictionary<int, DateTime>>("processes", game.Id);
        if (saved is not null)
            foreach (var item in saved)
            {
                try { using var p = Process.GetProcessById(item.Key); if (!p.HasExited && p.StartTime == item.Value) return true; }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { return true; }
            }
        if (string.IsNullOrWhiteSpace(game.ExePath)) return false;
        var name = Path.GetFileNameWithoutExtension(game.ExePath);
        var root = Path.GetDirectoryName(Path.GetFullPath(game.ExePath))! + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is not null && (path.Equals(game.ExePath, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root, StringComparison.OrdinalIgnoreCase))) return true;
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    try { if (process.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)) return true; } catch (InvalidOperationException) { }
                }
            }
        }
        return false;
    }
    public Task<bool> LaunchAndWaitAsync(GameProfile game, CancellationToken ct) => ObserveCoreAsync(game, null, ct);
    public Task<bool> ObserveAsync(GameProfile game, int processId, CancellationToken ct) => ObserveCoreAsync(game, processId, ct);
    private Task<bool> ObserveCoreAsync(GameProfile game, int? processId, CancellationToken ct) => Task.Run(async () =>
    {
        if (processId is null && IsRunning(game)) throw new IOException("遊戲已在執行。");
        if (!File.Exists(game.ExePath) || !game.ExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("找不到遊戲主程式，請重新連結。");
        using var process = processId is { } existingId ? Process.GetProcessById(existingId) : Process.Start(new ProcessStartInfo(game.ExePath) { WorkingDirectory = Path.GetDirectoryName(game.ExePath), UseShellExecute = false }) ?? throw new IOException("無法啟動遊戲。");
        var tracked = new Dictionary<int, DateTime> { [process.Id] = process.StartTime };
        var ended = new Dictionary<int, DateTime>();
        store?.Put("processes", game.Id, tracked);
        var timer = Stopwatch.StartNew(); var uncertain = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var tree = Tree(); bool added;
                do
                {
                    added = false;
                    foreach (var entry in tree.Where(x => tracked.ContainsKey(x.Parent) && !tracked.ContainsKey(x.Id)))
                    {
                        try
                        {
                            using var child = Process.GetProcessById(entry.Id);
                            if (child.StartTime < tracked[entry.Parent] || ended.TryGetValue(entry.Parent, out var end) && child.StartTime.ToUniversalTime() > end) continue;
                            try { using var parent = Process.GetProcessById(entry.Parent); if (parent.StartTime != tracked[entry.Parent]) continue; } catch (ArgumentException) { }
                            tracked[entry.Id] = child.StartTime; store?.Put("processes", game.Id, tracked); added = true;
                        }
                        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { uncertain = true; }
                    }
                } while (added);
                var alive = false;
                foreach (var item in tracked)
                {
                    try { using var p = Process.GetProcessById(item.Key); if (!p.HasExited && p.StartTime == item.Value) alive = true; else ended.TryAdd(item.Key, DateTime.UtcNow); }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException) { ended.TryAdd(item.Key, DateTime.UtcNow); }
                    catch (System.ComponentModel.Win32Exception) { uncertain = true; }
                }
                if (!alive && !IsRunning(game)) break;
            }
            catch (IOException) { return false; }
            await Task.Delay(250, ct);
        }
        if (timer.Elapsed < TimeSpan.FromSeconds(1)) uncertain = true;
        await Task.Delay(2000, ct);
        var reliable = !uncertain && !IsRunning(game);
        if (reliable) store?.Remove("processes", game.Id);
        return reliable;
    }, ct);
}

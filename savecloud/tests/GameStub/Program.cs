using System.Diagnostics;
if (args.Contains("--child")) { await Task.Delay(1800); return; }
if (File.Exists(Path.Combine(AppContext.BaseDirectory, "spawn-child")))
{
    Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "--child" }, UseShellExecute = false });
    await Task.Delay(500);
    return;
}
await Task.Delay(1300);
File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "stub.sav"), "game-finished");

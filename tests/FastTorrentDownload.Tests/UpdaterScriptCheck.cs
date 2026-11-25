using System.Diagnostics;
using FastTorrentDownload.Services;

/// <summary>
/// Runs the real updater script against a stand-in install folder. An update once failed because
/// the app's executable was still in use when the script copied the new files: the script stopped
/// without logging why and never started the app again.
/// </summary>
internal static class UpdaterScriptCheck
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "fast-torrent-updater-" + Guid.NewGuid().ToString("N"));
        try
        {
            // A process id that no longer exists: the script's wait for the old app ends at once.
            using var gone = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true, UseShellExecute = false })!;
            await gone.WaitForExitAsync();

            // 1. The executable is in use for two seconds, then free: the update waits and is applied.
            var first = Prepare(Path.Combine(root, "first"));
            int exit;
            using (var busy = new FileStream(first.Executable, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var run = RunScriptAsync(first, gone.Id, unlockSeconds: 30);
                await Task.Delay(2000);
                Require(!File.Exists(Path.Combine(first.Install, "new.txt")), "Nothing may be copied while the executable is in use.");
                busy.Dispose();
                exit = await run;
            }

            var log = await File.ReadAllTextAsync(Path.Combine(first.Work, "apply-update.log"));
            Require(exit == 0 && File.ReadAllText(Path.Combine(first.Install, "new.txt")) == "new", $"The update was not applied after the file was released (exit {exit}):\n{log}");
            Require(log.Contains("Not applied yet:") && log.Contains("Update v9.9.9 applied") && log.Contains("Starting app"), $"The log must record the wait, the result and the restart:\n{log}");
            Require(!File.Exists(first.Note) && AppUpdateService.TakeFailedUpdate(first.Note) is null, "A successful update must leave no failure note.");

            // 2. The executable stays in use: nothing is copied, the reason is logged, the app is started anyway and the next start is told.
            var second = Prepare(Path.Combine(root, "second"));
            using (new FileStream(second.Executable, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                exit = await RunScriptAsync(second, gone.Id, unlockSeconds: 3);
            }

            log = await File.ReadAllTextAsync(Path.Combine(second.Work, "apply-update.log"));
            Require(exit == 0 && !File.Exists(Path.Combine(second.Install, "new.txt")) && File.Exists(Path.Combine(second.Install, "old.txt")),
                $"A file that stays in use must leave the install untouched (exit {exit}):\n{log}");
            Require(log.Contains("Update v9.9.9 NOT applied:") && log.Contains("Starting app"), $"The failure and the restart must be logged:\n{log}");
            Require(AppUpdateService.TakeFailedUpdate(second.Note) == "v9.9.9" && !File.Exists(second.Note), "The failed update must be reported once.");

            // The note is plain data from a shared temp folder: anything but a version is ignored.
            await File.WriteAllTextAsync(second.Note, "v1.0.0'; calc");
            Require(AppUpdateService.TakeFailedUpdate(second.Note) is null && !File.Exists(second.Note), "A note that is not a plain version must be ignored and removed.");
            Console.WriteLine("PASS updater script waits for a file in use, applies the update when it is freed, and otherwise leaves the install untouched, logs why, restarts the app and reports it once");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static (string Stage, string Install, string Work, string Executable, string Note) Prepare(string folder)
    {
        var stage = Directory.CreateDirectory(Path.Combine(folder, "stage")).FullName;
        var install = Directory.CreateDirectory(Path.Combine(folder, "install")).FullName;
        var work = Directory.CreateDirectory(Path.Combine(folder, "work")).FullName;
        // A windowless system program stands in for the app: the script starts it and it exits at once.
        var stub = Path.Combine(Environment.SystemDirectory, "rundll32.exe");
        File.Copy(stub, Path.Combine(stage, "FastTorrentDownload.exe"));
        File.Copy(stub, Path.Combine(install, "FastTorrentDownload.exe"));
        File.WriteAllText(Path.Combine(stage, "new.txt"), "new");
        File.WriteAllText(Path.Combine(install, "old.txt"), "old");
        return (stage, install, work, Path.Combine(install, "FastTorrentDownload.exe"), Path.Combine(folder, "last-failure.txt"));
    }

    private static async Task<int> RunScriptAsync((string Stage, string Install, string Work, string Executable, string Note) target, int processId, int unlockSeconds)
    {
        var script = Path.Combine(target.Work, "apply-update.ps1");
        await File.WriteAllTextAsync(script, AppUpdateService.BuildUpdaterScript(
            processId, target.Stage, target.Install, target.Executable, target.Work, "v9.9.9", target.Note, unlockSeconds));
        using var process = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath()
        })!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
        return process.ExitCode;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

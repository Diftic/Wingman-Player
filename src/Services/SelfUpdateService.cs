namespace wingman_player.Services;

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;


internal static class SelfUpdateService
{
    private static readonly HttpClient _http = new();

    private static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Constants.AppDataFolderName);

    public static string UpdateLogPath    => Path.Combine(AppDataDir, "update.log");
    public static string MsiLogPath       => Path.Combine(AppDataDir, "update.msi.log");
    public static string UpdateStatusPath => Path.Combine(AppDataDir, "update.status.json");

    public sealed record LastUpdateResult(
        [property: JsonPropertyName("timestamp")] string Timestamp,
        [property: JsonPropertyName("success")]   bool   Success,
        [property: JsonPropertyName("exitCode")]  int    ExitCode,
        [property: JsonPropertyName("msiPath")]   string MsiPath);

    /// <summary>
    /// Reads the persisted result of the most recent MSI update attempt, if any.
    /// Written by the PowerShell helper so we can surface failures on next launch.
    /// </summary>
    public static LastUpdateResult? ReadLastResult()
    {
        try
        {
            if (!File.Exists(UpdateStatusPath)) return null;
            var json = File.ReadAllText(UpdateStatusPath);
            return JsonSerializer.Deserialize<LastUpdateResult>(json);
        }
        catch
        {
            return null;
        }
    }

    public static void ClearLastResult()
    {
        try { if (File.Exists(UpdateStatusPath)) File.Delete(UpdateStatusPath); } catch { }
    }

    public static async Task ApplyAsync(UpdateInfo info, Action quit, ILogger? log = null)
    {
        if (IsMsiInstall() && !string.IsNullOrEmpty(info.MsiUrl))
        {
            log?.LogInformation("MSI install detected — updating via msiexec");
            await ApplyMsiAsync(info.MsiUrl, quit, log);
        }
        else
        {
            log?.LogInformation("Portable install detected — updating via exe-swap");
            await ApplyPortableAsync(info.DownloadUrl, quit, log);
        }
    }

    private static bool IsMsiInstall()
    {
        var exe      = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        var userProg = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs");
        var pf   = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        return exe.StartsWith(userProg, StringComparison.OrdinalIgnoreCase)
            || exe.StartsWith(pf,       StringComparison.OrdinalIgnoreCase)
            || exe.StartsWith(pf86,     StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ApplyMsiAsync(string msiUrl, Action quit, ILogger? log)
    {
        Directory.CreateDirectory(AppDataDir);

        var currentExe = Process.GetCurrentProcess().MainModule?.FileName
            ?? Path.Combine(AppContext.BaseDirectory, "Wingman-Player.exe");
        var currentPid = Process.GetCurrentProcess().Id;

        var tempDir = Path.Combine(Path.GetTempPath(), "wingman_player_update");
        Directory.CreateDirectory(tempDir);
        var tempMsi = Path.Combine(tempDir, "wingman_player_update.msi");

        log?.LogInformation("Downloading MSI from {Url}", msiUrl);
        using var response = await _http.GetAsync(msiUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using (var src  = await response.Content.ReadAsStreamAsync())
        await using (var dest = File.Create(tempMsi))
            await src.CopyToAsync(dest);
        log?.LogInformation("MSI download complete — saved to {Msi}", tempMsi);

        // Clear previous status file so a stale failure doesn't mask a success,
        // and so the startup check only reports results from this attempt.
        ClearLastResult();

        // PS script responsibilities:
        //  1. Wait for our PID to exit so msiexec can overwrite the locked exe.
        //     (Portable path already does this; MSI path historically didn't —
        //     likely cause of silent install failures on slow shutdowns.)
        //  2. Run msiexec with /qb (basic UI with progress). /passive was
        //     flagged heuristically by Bitdefender / Malwarebytes on our
        //     unsigned MSI; /qb shows a visible progress window, which is
        //     closer to user-initiated install behaviour.
        //  3. Tee an MSI verbose log (/l*v) to update.msi.log for diagnostics.
        //  4. Persist a JSON status file so the next launch can surface failure.
        //  5. Relaunch the app only on exit codes 0 / 3010 (success / reboot-
        //     suggested, both post-install-complete since we pass /norestart).
        var logEsc       = EscapeSingleQuoted(UpdateLogPath);
        var msiLogEsc    = EscapeSingleQuoted(MsiLogPath);
        var statusEsc    = EscapeSingleQuoted(UpdateStatusPath);
        var msiEsc       = EscapeSingleQuoted(tempMsi);
        var exeEsc       = EscapeSingleQuoted(currentExe);

        var script = $$"""
            $ErrorActionPreference = 'Continue'
            $target  = {{currentPid}}
            $msi     = '{{msiEsc}}'
            $exe     = '{{exeEsc}}'
            $log     = '{{logEsc}}'
            $msiLog  = '{{msiLogEsc}}'
            $status  = '{{statusEsc}}'

            function Write-UpdateLog($msg) {
                $line = '[' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '] ' + $msg
                try { Add-Content -Path $log -Value $line -ErrorAction SilentlyContinue } catch {}
            }

            try {
                Write-UpdateLog ('--- Update start — PID ' + $target + ' ---')
                Write-UpdateLog ('MSI: ' + $msi)

                for ($i = 0; $i -lt 60; $i++) {
                    if (-not (Get-Process -Id $target -ErrorAction SilentlyContinue)) { break }
                    Start-Sleep -Milliseconds 500
                }
                if (Get-Process -Id $target -ErrorAction SilentlyContinue) {
                    Write-UpdateLog 'WARNING: old process did not exit within 30s — continuing anyway'
                } else {
                    Write-UpdateLog 'Old process exited'
                }

                $cmdArgs = '/i "' + $msi + '" /qb /norestart /l*v "' + $msiLog + '"'
                Write-UpdateLog ('Running: msiexec.exe ' + $cmdArgs)
                $p = Start-Process msiexec.exe -ArgumentList $cmdArgs -Wait -PassThru
                $code = $p.ExitCode
                Write-UpdateLog ('msiexec exit code: ' + $code)

                $success = ($code -eq 0 -or $code -eq 3010)
                $obj = [PSCustomObject]@{
                    timestamp = (Get-Date).ToString('o')
                    success   = $success
                    exitCode  = $code
                    msiPath   = $msi
                }
                $obj | ConvertTo-Json -Compress | Set-Content -Path $status -Encoding UTF8

                if ($success) {
                    Start-Sleep -Seconds 1
                    Start-Process $exe
                    Write-UpdateLog 'Relaunched application'
                } else {
                    Write-UpdateLog ('Update failed — MSI kept at ' + $msi + ' for manual retry')
                }
            } catch {
                Write-UpdateLog ('Script exception: ' + $_.Exception.Message)
                try {
                    $obj = [PSCustomObject]@{
                        timestamp = (Get-Date).ToString('o')
                        success   = $false
                        exitCode  = -1
                        msiPath   = $msi
                    }
                    $obj | ConvertTo-Json -Compress | Set-Content -Path $status -Encoding UTF8
                } catch {}
            }
            """;

        var scriptPath = Path.Combine(tempDir, "update.ps1");
        await File.WriteAllTextAsync(scriptPath, script);
        log?.LogInformation("MSI update script written to {Script} — launching and quitting", scriptPath);

        Process.Start(new ProcessStartInfo
        {
            FileName        = "powershell.exe",
            Arguments       = $"-NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = true,
            WindowStyle     = ProcessWindowStyle.Hidden,
        });

        quit();
    }

    // PowerShell single-quoted strings: only the ' character needs escaping (doubled).
    private static string EscapeSingleQuoted(string value) => value.Replace("'", "''");

    private static async Task ApplyPortableAsync(string exeUrl, Action quit, ILogger? log)
    {
        var currentExe = Process.GetCurrentProcess().MainModule?.FileName
            ?? Path.Combine(AppContext.BaseDirectory, "Wingman-Player.exe");

        log?.LogInformation("Self-update: current exe = {Exe}", currentExe);

        var tempDir = Path.Combine(Path.GetTempPath(), "wingman_player_update");
        Directory.CreateDirectory(tempDir);
        var tempExe = Path.Combine(tempDir, "Wingman-Player.exe");

        log?.LogInformation("Downloading update from {Url}", exeUrl);
        using var response = await _http.GetAsync(exeUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var src  = await response.Content.ReadAsStreamAsync();
        await using var dest = File.Create(tempExe);
        await src.CopyToAsync(dest);
        log?.LogInformation("Download complete — saved to {Temp}", tempExe);

        // Wait for the old process to exit before overwriting the exe. Avoids the
        // file-lock race that a fixed Start-Sleep can lose on slow shutdowns.
        var currentPid = Process.GetCurrentProcess().Id;
        var script = $$"""
            $target = {{currentPid}}
            for ($i = 0; $i -lt 60; $i++) {
                if (-not (Get-Process -Id $target -ErrorAction SilentlyContinue)) { break }
                Start-Sleep -Milliseconds 500
            }
            Copy-Item -Force '{{tempExe}}' '{{currentExe}}'
            Start-Process '{{currentExe}}'
            """;

        var scriptPath = Path.Combine(tempDir, "update.ps1");
        await File.WriteAllTextAsync(scriptPath, script);
        log?.LogInformation("Update script written to {Script} — launching and quitting", scriptPath);

        Process.Start(new ProcessStartInfo
        {
            FileName        = "powershell.exe",
            Arguments       = $"-NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = true,
            WindowStyle     = ProcessWindowStyle.Hidden,
        });

        quit();
    }
}

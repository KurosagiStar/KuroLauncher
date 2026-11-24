using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace KuroLauncher.Services;

public record LauncherUpdateInfo(string Version, string DownloadUrl, string FileName, string Notes);

public static class UpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/KurosagiStar/KuroLauncher/releases/latest";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    static UpdateService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("KuroLauncher/1.0");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";
        }
    }

    public static async Task<LauncherUpdateInfo?> CheckForUpdateAsync()
    {
        using var res = await Http.GetAsync(LatestReleaseApi);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tag = root.TryGetProperty("tag_name", out var tp) ? tp.GetString() ?? "" : "";
        var cleanRemote = tag.TrimStart('v', 'V').Split('-')[0];
        if (!Version.TryParse(cleanRemote, out var remoteVer) ||
            !Version.TryParse(CurrentVersion, out var localVer) ||
            remoteVer <= localVer)
        {
            return null;
        }

        string? setupUrl = null, setupName = null;
        string? exeUrl = null, exeName = null;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
                var url = a.TryGetProperty("browser_download_url", out var up) ? up.GetString() : null;
                if (string.IsNullOrEmpty(url) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                {
                    setupUrl = url;
                    setupName = name;
                }
                else
                {
                    exeUrl = url;
                    exeName = name;
                }
            }
        }

        var chosenUrl = setupUrl ?? exeUrl;
        var chosenName = setupName ?? exeName;
        if (string.IsNullOrEmpty(chosenUrl) || string.IsNullOrEmpty(chosenName))
            return null;

        var notes = root.TryGetProperty("body", out var bp) ? bp.GetString() ?? "" : "";
        return new LauncherUpdateInfo(cleanRemote, chosenUrl, chosenName, notes);
    }

    public static async Task DownloadAndApplyUpdateAsync(LauncherUpdateInfo info)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), info.FileName);
        await CurseForgeService.DownloadFileAsync(info.DownloadUrl, tempFile);

        if (info.FileName.Contains("Setup", StringComparison.OrdinalIgnoreCase))
        {
            Process.Start(new ProcessStartInfo(tempFile)
            {
                Arguments = "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
                UseShellExecute = true
            });
            Environment.Exit(0);
            return;
        }

        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentExe))
            throw new Exception("No se pudo determinar la ruta del ejecutable actual.");

        var batPath = Path.Combine(Path.GetTempPath(), "kuro_update.cmd");
        var script = $"""
            @echo off
            timeout /t 1 /nobreak >nul
            move /y "{tempFile}" "{currentExe}" >nul
            start "" "{currentExe}"
            del "%~f0"
            """;
        File.WriteAllText(batPath, script);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{batPath}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        });
        Environment.Exit(0);
    }
}

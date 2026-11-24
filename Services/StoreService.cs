using System.Diagnostics;
using System.Text.Json;
using KuroLauncher.Models;

namespace KuroLauncher.Services;

public static class StoreService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string DataDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "glasscraft-launcher");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string DefaultInstancesDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "KuroLauncher", "instances");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string DefaultServersDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "KuroLauncher", "servers");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static List<string> DetectJavaInstallations()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var p = Path.Combine(javaHome, "bin", "java.exe");
            if (File.Exists(p)) found.Add(p);
        }

        try
        {
            var psi = new ProcessStartInfo("where", "java")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var outText = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(2000);
                foreach (var line in outText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    if (File.Exists(trimmed)) found.Add(trimmed);
                }
            }
        }
        catch { }

        string[] roots =
        [
            @"C:\Program Files\Eclipse Adoptium",
            @"C:\Program Files\Java",
            @"C:\Program Files\Microsoft",
            @"C:\Program Files\Zulu",
            @"C:\Program Files\BellSoft",
        ];

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var sub in Directory.GetDirectories(root))
                {
                    var exe = Path.Combine(sub, "bin", "java.exe");
                    if (File.Exists(exe)) found.Add(exe);
                }
            }
            catch { }
        }

        return found.OrderBy(x => x).ToList();
    }

    public static LauncherSettings LoadSettings()
    {
        var file = Path.Combine(DataDir, "settings.json");
        var defaults = new LauncherSettings
        {
            JavaPath = DetectJavaInstallations().FirstOrDefault() ?? "java",
            MinRamMb = 2048,
            MaxRamMb = 6144,
            InstancesDir = DefaultInstancesDir,
            ServersDir = DefaultServersDir,
            BackgroundBrightness = 0.55,
            BackgroundBlur = 6
        };

        if (!File.Exists(file))
        {
            SaveSettings(defaults);
            return defaults;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(file), JsonOpts);
            if (loaded != null)
            {
                if (string.IsNullOrWhiteSpace(loaded.InstancesDir)) loaded.InstancesDir = DefaultInstancesDir;
                if (string.IsNullOrWhiteSpace(loaded.ServersDir)) loaded.ServersDir = DefaultServersDir;
                if (string.IsNullOrWhiteSpace(loaded.JavaPath)) loaded.JavaPath = defaults.JavaPath;
                if (loaded.BackgroundBrightness <= 0) loaded.BackgroundBrightness = 0.55;
                return loaded;
            }
        }
        catch { }
        return defaults;
    }

    public static void SaveSettings(LauncherSettings settings)
    {
        var file = Path.Combine(DataDir, "settings.json");
        File.WriteAllText(file, JsonSerializer.Serialize(settings, JsonOpts));
    }

    public static string GetEffectiveApiKey()
    {
        var settings = LoadSettings();
        if (!string.IsNullOrWhiteSpace(settings.CurseforgeApiKey))
            return settings.CurseforgeApiKey.Trim();

        var baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(Directory.GetCurrentDirectory(), ".env"),
            Path.Combine(Directory.GetCurrentDirectory(), "..", ".env"),
            Path.Combine(baseDir, ".env"),
            Path.Combine(baseDir, "..", ".env"),
            Path.Combine(baseDir, "..", "..", "..", ".env"),
        ];

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            var val = ParseCurseForgeKeyFromLines(File.ReadAllLines(path));
            if (!string.IsNullOrEmpty(val)) return val;
        }

        // Fallback: embedded .env resource inside single-file exe
        try
        {
            var asm = typeof(StoreService).Assembly;
            var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".env", StringComparison.OrdinalIgnoreCase));
            if (resName != null)
            {
                using var s = asm.GetManifestResourceStream(resName);
                if (s != null)
                {
                    using var sr = new StreamReader(s);
                    var val = ParseCurseForgeKeyFromLines(sr.ReadToEnd().Split('\n'));
                    if (!string.IsNullOrEmpty(val)) return val;
                }
            }
        }
        catch { }

        return Environment.GetEnvironmentVariable("CURSEFORGE_API_KEY")?.Trim() ?? "";
    }

    private static string? ParseCurseForgeKeyFromLines(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#') || !trimmed.StartsWith("CURSEFORGE_API_KEY=")) continue;
            var val = trimmed["CURSEFORGE_API_KEY=".Length..].Trim().Trim('\'', '"').Trim();
            if (!string.IsNullOrEmpty(val)) return val;
        }
        return null;
    }

    public static List<InstanceConfig> LoadInstances()
    {
        var file = Path.Combine(DataDir, "instances.json");
        if (!File.Exists(file)) return new();
        try
        {
            var list = JsonSerializer.Deserialize<List<InstanceConfig>>(File.ReadAllText(file), JsonOpts) ?? new();
            foreach (var inst in list)
            {
                var modsDir = Path.Combine(inst.Path, "mods");
                inst.ModCount = Directory.Exists(modsDir)
                    ? Directory.GetFiles(modsDir, "*.jar").Length
                    : 0;
            }
            return list;
        }
        catch
        {
            return new();
        }
    }

    public static void SaveInstances(List<InstanceConfig> list)
    {
        var file = Path.Combine(DataDir, "instances.json");
        File.WriteAllText(file, JsonSerializer.Serialize(list, JsonOpts));
    }

    public static void UpsertInstance(InstanceConfig inst)
    {
        var list = LoadInstances();
        var idx = list.FindIndex(i => i.Id == inst.Id);
        if (idx >= 0) list[idx] = inst;
        else list.Insert(0, inst);
        SaveInstances(list);
    }

    public static InstanceConfig? RemoveInstance(string id)
    {
        var list = LoadInstances();
        var target = list.FirstOrDefault(i => i.Id == id);
        if (target != null)
        {
            list.RemoveAll(i => i.Id == id);
            SaveInstances(list);
        }
        return target;
    }

    public static MinecraftAccount? LoadAccount()
    {
        var file = Path.Combine(DataDir, "account.json");
        if (!File.Exists(file)) return null;
        try
        {
            return JsonSerializer.Deserialize<MinecraftAccount>(File.ReadAllText(file), JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    public static void SaveAccount(MinecraftAccount? account)
    {
        var file = Path.Combine(DataDir, "account.json");
        if (account == null)
        {
            if (File.Exists(file)) File.Delete(file);
            return;
        }
        File.WriteAllText(file, JsonSerializer.Serialize(account, JsonOpts));
    }

    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    public static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public static void CopyDirectoryRecursive(string src, string dest)
    {
        if (!Directory.Exists(src)) return;
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(src))
        {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
        }
        foreach (var dir in Directory.GetDirectories(src))
        {
            CopyDirectoryRecursive(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }
    }

    public static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "instance" : cleaned;
    }
}

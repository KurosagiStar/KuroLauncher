using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using KuroLauncher.Models;

namespace KuroLauncher.Services;

public static class LauncherService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Process> RunningProcesses = new();

    public static bool KillInstance(string instanceId)
    {
        if (!RunningProcesses.TryRemove(instanceId, out var proc)) return false;
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
            return true;
        }
        catch { return false; }
    }

    private static string? MavenToPath(string coord)
    {
        var splitExt = coord.Split('@');
        var ext = splitExt.Length > 1 ? splitExt[1] : "jar";
        var parts = splitExt[0].Split(':');
        if (parts.Length < 3) return null;
        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        return parts.Length >= 4
            ? $"{group}/{artifact}/{version}/{artifact}-{version}-{parts[3]}.{ext}"
            : $"{group}/{artifact}/{version}/{artifact}-{version}.{ext}";
    }

    private static string ArtifactKey(string coord)
    {
        var parts = coord.Split('@')[0].Split(':');
        if (parts.Length >= 4) return $"{parts[0]}:{parts[1]}:{parts[3]}";
        if (parts.Length >= 2) return $"{parts[0]}:{parts[1]}";
        return coord;
    }

    private static bool RulesAllowWindows(JsonElement lib)
    {
        if (!lib.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            return true;
        bool allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.TryGetProperty("features", out _)) continue;
            var action = rule.TryGetProperty("action", out var a) ? a.GetString() : "allow";
            bool osOk = true;
            bool archOk = true;
            if (rule.TryGetProperty("os", out var os))
            {
                if (os.TryGetProperty("name", out var n)) osOk = n.GetString() == "windows";
                if (os.TryGetProperty("arch", out var ar)) archOk = ar.GetString() != "x86";
            }
            if (osOk && archOk) allowed = action == "allow";
        }
        return allowed;
    }

    private static int? DetectJavaMajor(string javaExe)
    {
        try
        {
            var psi = new ProcessStartInfo(javaExe, "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var combined = p.StandardOutput.ReadToEnd() + "\n" + p.StandardError.ReadToEnd();
            p.WaitForExit(3000);
            var parts = combined.Split('"');
            if (parts.Length < 2) return null;
            var ver = parts[1];
            if (ver.StartsWith("1."))
                return int.TryParse(ver[2..].Split('.')[0], out var v8) ? v8 : null;
            var digits = new string(ver.TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, out var v) ? v : null;
        }
        catch { return null; }
    }

    private static string? FindJavaExeInDir(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        var direct = Path.Combine(dir, "bin", "java.exe");
        if (File.Exists(direct)) return direct;
        foreach (var sub in Directory.GetDirectories(dir))
        {
            var c = Path.Combine(sub, "bin", "java.exe");
            if (File.Exists(c)) return c;
        }
        return null;
    }

    private static async Task<string> EnsureCompatibleJavaAsync(
        InstanceConfig inst, string taskId, string configuredJava, int requiredMajor,
        Action<TaskProgressEvent> emitProgress, Action<ConsoleLogEvent> emitLog)
    {
        var found = DetectJavaMajor(configuredJava);
        if (found.HasValue && (found.Value == requiredMajor || (requiredMajor >= 17 && found.Value >= requiredMajor)))
            return configuredJava;

        var runtimeDir = Path.Combine(StoreService.DataDir, "runtimes", $"java-{requiredMajor}");
        if (FindJavaExeInDir(runtimeDir) is string existing) return existing;

        emitProgress(new(taskId, $"Iniciando {inst.Name}", inst.Id, "running", 8, $"Descargando Java {requiredMajor} (Temurin)..."));
        emitLog(new("info", $"Descargando runtime Java {requiredMajor} automático (Adoptium Temurin)...", DateTime.Now.ToString("HH:mm:ss")));

        var url = $"https://api.adoptium.net/v3/binary/latest/{requiredMajor}/ga/windows/x64/jre/hotspot/normal/eclipse";
        var zipPath = Path.Combine(StoreService.DataDir, "runtimes", $"temurin-{requiredMajor}.zip");
        await CurseForgeService.DownloadFileAsync(url, zipPath);
        ZipFile.ExtractToDirectory(zipPath, runtimeDir, true);
        File.Delete(zipPath);

        return FindJavaExeInDir(runtimeDir) ?? throw new Exception($"No se encontró java.exe en Temurin {requiredMajor}.");
    }

    private static async Task<(string Id, JsonElement Json)?> EnsureLoaderProfileAsync(
        InstanceConfig inst, string javaExe, Action<ConsoleLogEvent> emitLog)
    {
        var loader = inst.Loader.ToLowerInvariant();
        if (loader == "vanilla") return null;

        if (loader is "fabric" or "quilt")
        {
            var lv = !string.IsNullOrWhiteSpace(inst.LoaderVersion) ? inst.LoaderVersion : (loader == "fabric" ? "0.16.10" : "0.26.4");
            var profileId = $"{loader}-loader-{lv}-{inst.MinecraftVersion}";
            var jsonPath = Path.Combine(inst.Path, "versions", profileId, $"{profileId}.json");
            if (!File.Exists(jsonPath))
            {
                emitLog(new("info", $"Descargando perfil {loader} {lv}...", DateTime.Now.ToString("HH:mm:ss")));
                var url = loader == "fabric"
                    ? $"https://meta.fabricmc.net/v2/versions/loader/{inst.MinecraftVersion}/{lv}/profile/json"
                    : $"https://meta.quiltmc.org/v3/versions/loader/{inst.MinecraftVersion}/{lv}/profile/json";
                var text = await Http.GetStringAsync(url);
                Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
                await File.WriteAllTextAsync(jsonPath, text);
            }
            return (profileId, JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath)).RootElement.Clone());
        }

        if (loader is "forge" or "neoforge" && !string.IsNullOrWhiteSpace(inst.LoaderVersion))
        {
            var isNeo = loader == "neoforge";
            var profileId = isNeo ? $"neoforge-{inst.LoaderVersion}" : $"{inst.MinecraftVersion}-forge-{inst.LoaderVersion}";
            var jsonPath = Path.Combine(inst.Path, "versions", profileId, $"{profileId}.json");

            if (!File.Exists(jsonPath))
            {
                var fileName = isNeo
                    ? $"neoforge-{inst.LoaderVersion}-installer.jar"
                    : $"forge-{inst.MinecraftVersion}-{inst.LoaderVersion}-installer.jar";
                var url = isNeo
                    ? $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{inst.LoaderVersion}/{fileName}"
                    : $"https://maven.minecraftforge.net/net/minecraftforge/forge/{inst.MinecraftVersion}-{inst.LoaderVersion}/{fileName}";

                var installerPath = Path.Combine(inst.Path, ".installers", fileName);
                if (!File.Exists(installerPath))
                {
                    emitLog(new("info", $"Descargando instalador {fileName}...", DateTime.Now.ToString("HH:mm:ss")));
                    await CurseForgeService.DownloadFileAsync(url, installerPath);
                }

                var profilesFile = Path.Combine(inst.Path, "launcher_profiles.json");
                if (!File.Exists(profilesFile)) await File.WriteAllTextAsync(profilesFile, "{\"profiles\":{}}");

                emitLog(new("info", $"Instalando {profileId} con Java...", DateTime.Now.ToString("HH:mm:ss")));
                var psi = new ProcessStartInfo(javaExe, $"-jar \"{installerPath}\" --installClient \"{inst.Path}\"")
                {
                    WorkingDirectory = inst.Path,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi) ?? throw new Exception("No se pudo iniciar instalador del loader.");
                await proc.WaitForExitAsync();
                if (proc.ExitCode != 0) throw new Exception($"Instalador falló (código {proc.ExitCode}).");
            }

            if (File.Exists(jsonPath))
                return (profileId, JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath)).RootElement.Clone());
        }

        return null;
    }

    private static string Sub(string input, Dictionary<string, string> vars)
    {
        foreach (var kv in vars) input = input.Replace($"${{{kv.Key}}}", kv.Value);
        return input;
    }

    private static void CollectArgs(JsonElement parent, string key, Dictionary<string, string> vars, List<string> dest)
    {
        if (!parent.TryGetProperty("arguments", out var args) || !args.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                dest.Add(Sub(item.GetString()!, vars));
            else if (item.ValueKind == JsonValueKind.Object && RulesAllowWindows(item) && item.TryGetProperty("value", out var val))
            {
                if (val.ValueKind == JsonValueKind.String) dest.Add(Sub(val.GetString()!, vars));
                else if (val.ValueKind == JsonValueKind.Array)
                    foreach (var s in val.EnumerateArray())
                        if (s.GetString() is string str) dest.Add(Sub(str, vars));
            }
        }
    }

    private static void ExtractNatives(string jarPath, string nativesDir)
    {
        try
        {
            Directory.CreateDirectory(nativesDir);
            using var z = ZipFile.OpenRead(jarPath);
            foreach (var e in z.Entries)
            {
                if (!e.FullName.StartsWith("META-INF") && e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    e.ExtractToFile(Path.Combine(nativesDir, Path.GetFileName(e.FullName)), true);
            }
        }
        catch { }
    }

    public static async Task LaunchInstanceAsync(
        InstanceConfig inst,
        Action<TaskProgressEvent> emitProgress,
        Action<ConsoleLogEvent> emitLog,
        Action<string, bool> emitRunningState)
    {
        var settings = StoreService.LoadSettings();
        var account = await AuthService.EnsureFreshAccountAsync()
            ?? throw new Exception("Inicia sesión con Microsoft (o modo offline) antes de jugar.");

        var taskId = $"launch-{inst.Id}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        Directory.CreateDirectory(inst.Path);
        emitProgress(new(taskId, $"Iniciando {inst.Name}", inst.Id, "running", 5, "Resolviendo versión..."));

        var vanillaJsonPath = Path.Combine(inst.Path, "versions", inst.MinecraftVersion, $"{inst.MinecraftVersion}.json");
        JsonElement vanillaJson;
        if (File.Exists(vanillaJsonPath))
        {
            vanillaJson = JsonDocument.Parse(await File.ReadAllTextAsync(vanillaJsonPath)).RootElement.Clone();
        }
        else
        {
            var manifest = JsonDocument.Parse(await Http.GetStringAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json")).RootElement;
            string? verUrl = null;
            foreach (var v in manifest.GetProperty("versions").EnumerateArray())
                if (v.GetProperty("id").GetString() == inst.MinecraftVersion) { verUrl = v.GetProperty("url").GetString(); break; }
            if (verUrl == null) throw new Exception($"Versión {inst.MinecraftVersion} no encontrada en Mojang.");
            var text = await Http.GetStringAsync(verUrl);
            Directory.CreateDirectory(Path.GetDirectoryName(vanillaJsonPath)!);
            await File.WriteAllTextAsync(vanillaJsonPath, text);
            vanillaJson = JsonDocument.Parse(text).RootElement.Clone();
        }

        var reqJava = vanillaJson.TryGetProperty("javaVersion", out var jv) && jv.TryGetProperty("majorVersion", out var mv) ? mv.GetInt32() : 17;
        var cfgJava = !string.IsNullOrWhiteSpace(inst.JavaPath) ? inst.JavaPath : settings.JavaPath;
        var javaExe = await EnsureCompatibleJavaAsync(inst, taskId, cfgJava, reqJava, emitProgress, emitLog);

        var clientJar = Path.Combine(inst.Path, "versions", inst.MinecraftVersion, $"{inst.MinecraftVersion}.jar");
        if (!File.Exists(clientJar) && vanillaJson.GetProperty("downloads").GetProperty("client").TryGetProperty("url", out var cUrl))
            await CurseForgeService.DownloadFileAsync(cUrl.GetString()!, clientJar);

        var loaderProfile = await EnsureLoaderProfileAsync(inst, javaExe, emitLog);
        var activeVer = loaderProfile?.Id ?? inst.MinecraftVersion;
        JsonElement? loaderJson = loaderProfile?.Json;

        var mainClass = (loaderJson?.TryGetProperty("mainClass", out var lmc) == true ? lmc.GetString() : null)
            ?? (vanillaJson.TryGetProperty("mainClass", out var vmc) ? vmc.GetString() : null)
            ?? "net.minecraft.client.main.Main";

        var libsDir = Path.Combine(inst.Path, "libraries");
        var nativesDir = Path.Combine(inst.Path, "natives");
        Directory.CreateDirectory(libsDir);
        Directory.CreateDirectory(nativesDir);

        var downloads = new List<(string Url, string Dest, bool IsNative)>();
        var cp = new List<string>();
        var seenCp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenArtifacts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void PushCp(string p) { if (seenCp.Add(p)) cp.Add(p); }

        void CollectLibs(JsonElement vjson)
        {
            if (!vjson.TryGetProperty("libraries", out var libs) || libs.ValueKind != JsonValueKind.Array) return;
            foreach (var lib in libs.EnumerateArray())
            {
                if (!RulesAllowWindows(lib)) continue;
                bool isNew = !lib.TryGetProperty("name", out var np) || seenArtifacts.Add(ArtifactKey(np.GetString()!));

                if (isNew)
                {
                    if (lib.TryGetProperty("downloads", out var dl) && dl.TryGetProperty("artifact", out var art))
                    {
                        var p = art.GetProperty("path").GetString()!;
                        var dest = Path.Combine(libsDir, p.Replace('/', Path.DirectorySeparatorChar));
                        PushCp(dest);
                        var u = art.TryGetProperty("url", out var up) ? up.GetString() ?? "" : "";
                        if (!File.Exists(dest) && !string.IsNullOrEmpty(u)) downloads.Add((u, dest, false));
                    }
                    else if (lib.TryGetProperty("name", out var n2) && MavenToPath(n2.GetString()!) is string rel)
                    {
                        var dest = Path.Combine(libsDir, rel.Replace('/', Path.DirectorySeparatorChar));
                        PushCp(dest);
                        if (!File.Exists(dest))
                        {
                            var b = (lib.TryGetProperty("url", out var up2) ? up2.GetString() : "https://libraries.minecraft.net/")!.TrimEnd('/');
                            downloads.Add(($"{b}/{rel}", dest, false));
                        }
                    }
                }

                if (lib.TryGetProperty("natives", out var nat) && nat.TryGetProperty("windows", out var nw))
                {
                    var key = nw.GetString()!.Replace("${arch}", "64");
                    if (lib.TryGetProperty("downloads", out var dl2) && dl2.TryGetProperty("classifiers", out var cls) && cls.TryGetProperty(key, out var cobj))
                    {
                        var dest = Path.Combine(libsDir, cobj.GetProperty("path").GetString()!.Replace('/', Path.DirectorySeparatorChar));
                        var u = cobj.GetProperty("url").GetString() ?? "";
                        if (!File.Exists(dest) && !string.IsNullOrEmpty(u)) downloads.Add((u, dest, true));
                        else if (File.Exists(dest)) ExtractNatives(dest, nativesDir);
                    }
                }
            }
        }

        if (loaderJson.HasValue) CollectLibs(loaderJson.Value);
        CollectLibs(vanillaJson);
        if (mainClass != "cpw.mods.bootstraplauncher.BootstrapLauncher") PushCp(clientJar);

        if (downloads.Count > 0)
        {
            int done = 0;
            using var sem = new SemaphoreSlim(10);
            await Task.WhenAll(downloads.Select(async d =>
            {
                await sem.WaitAsync();
                try
                {
                    await CurseForgeService.DownloadFileAsync(d.Url, d.Dest);
                    if (d.IsNative) ExtractNatives(d.Dest, nativesDir);
                }
                catch { }
                finally
                {
                    var c = Interlocked.Increment(ref done);
                    emitProgress(new(taskId, $"Iniciando {inst.Name}", inst.Id, "running", Math.Min(50, 20 + c * 30 / downloads.Count), $"Librerías ({c}/{downloads.Count})"));
                    sem.Release();
                }
            }));
        }

        // Assets
        var assetsDir = Path.Combine(inst.Path, "assets");
        var assetId = vanillaJson.GetProperty("assetIndex").GetProperty("id").GetString() ?? inst.MinecraftVersion;
        var assetIdxPath = Path.Combine(assetsDir, "indexes", $"{assetId}.json");
        if (!File.Exists(assetIdxPath))
        {
            var text = await Http.GetStringAsync(vanillaJson.GetProperty("assetIndex").GetProperty("url").GetString()!);
            Directory.CreateDirectory(Path.GetDirectoryName(assetIdxPath)!);
            await File.WriteAllTextAsync(assetIdxPath, text);
        }
        var assetIdx = JsonDocument.Parse(await File.ReadAllTextAsync(assetIdxPath)).RootElement;
        var missingAssets = new List<(string Url, string Dest)>();
        foreach (var prop in assetIdx.GetProperty("objects").EnumerateObject())
        {
            var hash = prop.Value.GetProperty("hash").GetString()!;
            var dest = Path.Combine(assetsDir, "objects", hash[..2], hash);
            if (!File.Exists(dest)) missingAssets.Add(($"https://resources.download.minecraft.net/{hash[..2]}/{hash}", dest));
        }

        if (missingAssets.Count > 0)
        {
            int done = 0;
            using var sem = new SemaphoreSlim(16);
            await Task.WhenAll(missingAssets.Select(async a =>
            {
                await sem.WaitAsync();
                try { await CurseForgeService.DownloadFileAsync(a.Url, a.Dest); } catch { }
                finally
                {
                    var c = Interlocked.Increment(ref done);
                    if (c % 25 == 0 || c == missingAssets.Count)
                        emitProgress(new(taskId, $"Iniciando {inst.Name}", inst.Id, "running", Math.Min(95, 50 + c * 45 / missingAssets.Count), $"Assets ({c}/{missingAssets.Count})"));
                    sem.Release();
                }
            }));
        }

        var cpJoined = string.Join(";", cp);
        var userType = account.Type == "microsoft" ? "msa" : "legacy";
        var vars = new Dictionary<string, string>
        {
            ["natives_directory"] = nativesDir,
            ["launcher_name"] = "KuroLauncher",
            ["launcher_version"] = "2.0.0",
            ["classpath"] = cpJoined,
            ["classpath_separator"] = ";",
            ["library_directory"] = libsDir,
            ["version_name"] = activeVer,
            ["auth_player_name"] = account.Name,
            ["game_directory"] = inst.Path,
            ["assets_root"] = assetsDir,
            ["game_assets"] = assetsDir,
            ["assets_index_name"] = assetId,
            ["auth_uuid"] = account.Id,
            ["auth_access_token"] = account.AccessToken,
            ["auth_session"] = account.AccessToken,
            ["clientid"] = "",
            ["auth_xuid"] = "",
            ["user_type"] = userType,
            ["version_type"] = "release",
            ["user_properties"] = "{}"
        };

        var minRam = inst.MinRamMb ?? settings.MinRamMb;
        var maxRam = inst.MaxRamMb ?? settings.MaxRamMb;
        var jvmArgs = new List<string> { $"-Xms{minRam}M", $"-Xmx{maxRam}M" };
        if (!string.IsNullOrWhiteSpace(inst.JvmArgs))
            jvmArgs.AddRange(inst.JvmArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        bool hasModernJvm = (vanillaJson.TryGetProperty("arguments", out var va) && va.TryGetProperty("jvm", out _)) ||
            (loaderJson?.TryGetProperty("arguments", out var la) == true && la.TryGetProperty("jvm", out _));
        if (hasModernJvm)
        {
            CollectArgs(vanillaJson, "jvm", vars, jvmArgs);
            if (loaderJson.HasValue) CollectArgs(loaderJson.Value, "jvm", vars, jvmArgs);
        }
        else
        {
            jvmArgs.Add($"-Djava.library.path={nativesDir}");
            jvmArgs.Add("-cp");
            jvmArgs.Add(cpJoined);
        }

        var gameArgs = new List<string>();
        bool hasModernGame = (vanillaJson.TryGetProperty("arguments", out var va2) && va2.TryGetProperty("game", out _)) ||
            (loaderJson?.TryGetProperty("arguments", out var la2) == true && la2.TryGetProperty("game", out _));
        if (hasModernGame)
        {
            CollectArgs(vanillaJson, "game", vars, gameArgs);
            if (loaderJson.HasValue) CollectArgs(loaderJson.Value, "game", vars, gameArgs);
        }
        else
        {
            var legacy = (loaderJson?.TryGetProperty("minecraftArguments", out var lma) == true ? lma.GetString() : null)
                ?? (vanillaJson.TryGetProperty("minecraftArguments", out var vma) ? vma.GetString() : null);
            if (!string.IsNullOrEmpty(legacy))
                foreach (var t in legacy.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    gameArgs.Add(Sub(t, vars));
        }

        if (inst.Fullscreen == true) gameArgs.Add("--fullscreen");
        else if (inst.WindowWidth > 0 && inst.WindowHeight > 0)
        {
            gameArgs.Add("--width"); gameArgs.Add(inst.WindowWidth.Value.ToString());
            gameArgs.Add("--height"); gameArgs.Add(inst.WindowHeight.Value.ToString());
        }

        inst.LastPlayed = DateTime.UtcNow.ToString("O");
        StoreService.UpsertInstance(inst);

        emitLog(new("info", $"Lanzando {inst.Name} ({mainClass}) con Java {reqJava}", DateTime.Now.ToString("HH:mm:ss")));

        var psiLaunch = new ProcessStartInfo(javaExe)
        {
            WorkingDirectory = inst.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in jvmArgs) psiLaunch.ArgumentList.Add(a);
        psiLaunch.ArgumentList.Add(mainClass);
        foreach (var a in gameArgs) psiLaunch.ArgumentList.Add(a);

        var process = Process.Start(psiLaunch) ?? throw new Exception($"Error iniciando Java ({javaExe}).");
        RunningProcesses[inst.Id] = process;
        emitRunningState(inst.Id, true);
        emitProgress(new(taskId, $"Iniciando {inst.Name}", inst.Id, "done", 100, "En ejecución"));

        _ = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is string line)
                emitLog(new("info", line, DateTime.Now.ToString("HH:mm:ss")));
        });
        _ = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is string line)
                emitLog(new("warn", line, DateTime.Now.ToString("HH:mm:ss")));
        });
        _ = Task.Run(async () =>
        {
            await process.WaitForExitAsync();
            RunningProcesses.TryRemove(inst.Id, out _);
            emitLog(new(process.ExitCode == 0 ? "info" : "error", $"Minecraft finalizó con código {process.ExitCode}", DateTime.Now.ToString("HH:mm:ss")));
            emitRunningState(inst.Id, false);
        });
    }
}

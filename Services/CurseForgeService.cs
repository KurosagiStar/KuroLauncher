using System.IO.Compression;
using System.Text;
using System.Text.Json;
using KuroLauncher.Models;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace KuroLauncher.Services;

public static class CurseForgeService
{
    private const string CfBase = "https://api.curseforge.com/v1";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private static async Task<JsonElement> CfGetAsync(string endpoint)
    {
        var key = StoreService.GetEffectiveApiKey();
        if (string.IsNullOrEmpty(key))
            throw new Exception("Falta CURSEFORGE_API_KEY en .env.");

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{CfBase}{endpoint}");
        req.Headers.Add("x-api-key", key);
        req.Headers.Add("Accept", "application/json");
        var res = await Http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            throw new Exception($"CurseForge API {(int)res.StatusCode}: {text[..Math.Min(180, text.Length)]}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<JsonElement> CfPostAsync(string endpoint, object body)
    {
        var key = StoreService.GetEffectiveApiKey();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{CfBase}{endpoint}")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("x-api-key", key);
        req.Headers.Add("Accept", "application/json");
        var res = await Http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            throw new Exception($"CurseForge API {(int)res.StatusCode}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static CurseForgeFileSummary MapFile(JsonElement raw)
    {
        var gv = new List<string>();
        if (raw.TryGetProperty("gameVersions", out var gvArr) && gvArr.ValueKind == JsonValueKind.Array)
            foreach (var x in gvArr.EnumerateArray())
                if (x.GetString() is string s) gv.Add(s);

        return new CurseForgeFileSummary
        {
            Id = raw.GetProperty("id").GetInt64(),
            DisplayName = raw.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "",
            FileName = raw.TryGetProperty("fileName", out var fn) ? fn.GetString() ?? "mod.zip" : "mod.zip",
            FileDate = raw.TryGetProperty("fileDate", out var fd) ? fd.GetString() ?? "" : "",
            FileLength = raw.TryGetProperty("fileLength", out var fl) ? fl.GetInt64() : 0,
            DownloadUrl = raw.TryGetProperty("downloadUrl", out var du) && du.ValueKind == JsonValueKind.String ? du.GetString() : null,
            GameVersions = gv,
            IsServerPack = raw.TryGetProperty("isServerPack", out var sp) && sp.ValueKind == JsonValueKind.True,
            ServerPackFileId = raw.TryGetProperty("serverPackFileId", out var spId) && spId.ValueKind == JsonValueKind.Number ? spId.GetInt64() : null
        };
    }

    public static async Task<List<CurseForgeModpack>> SearchModpacksAsync(
        string query, string gameVersion, int modLoaderType, int sortField)
    {
        var qs = new List<string>
        {
            "gameId=432",
            "classId=4471",
            $"sortField={sortField}",
            "sortOrder=desc",
            "pageSize=24"
        };
        if (!string.IsNullOrWhiteSpace(query))
            qs.Add($"searchFilter={Uri.EscapeDataString(query.Trim())}");
        if (!string.IsNullOrWhiteSpace(gameVersion) && gameVersion != "Todas")
            qs.Add($"gameVersion={Uri.EscapeDataString(gameVersion.Trim())}");
        if (modLoaderType > 0)
            qs.Add($"modLoaderType={modLoaderType}");

        var root = await CfGetAsync($"/mods/search?{string.Join("&", qs)}");
        var list = new List<CurseForgeModpack>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in data.EnumerateArray())
            {
                var authors = new List<string>();
                if (m.TryGetProperty("authors", out var authArr) && authArr.ValueKind == JsonValueKind.Array)
                    foreach (var a in authArr.EnumerateArray())
                        if (a.TryGetProperty("name", out var an) && an.GetString() is string s) authors.Add(s);

                var files = new List<CurseForgeFileSummary>();
                if (m.TryGetProperty("latestFiles", out var lfArr) && lfArr.ValueKind == JsonValueKind.Array)
                    foreach (var f in lfArr.EnumerateArray()) files.Add(MapFile(f));

                string? logo = null;
                if (m.TryGetProperty("logo", out var l) && l.ValueKind == JsonValueKind.Object)
                {
                    logo = l.TryGetProperty("thumbnailUrl", out var tu) && tu.ValueKind == JsonValueKind.String
                        ? tu.GetString()
                        : l.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                }

                string? web = null;
                if (m.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Object &&
                    links.TryGetProperty("websiteUrl", out var wu) && wu.ValueKind == JsonValueKind.String)
                    web = wu.GetString();

                list.Add(new CurseForgeModpack
                {
                    Id = m.GetProperty("id").GetInt64(),
                    Name = m.GetProperty("name").GetString() ?? "Modpack",
                    Slug = m.TryGetProperty("slug", out var sl) ? sl.GetString() ?? "" : "",
                    Summary = m.TryGetProperty("summary", out var su) ? su.GetString() ?? "" : "",
                    DownloadCount = m.TryGetProperty("downloadCount", out var dc) ? dc.GetInt64() : 0,
                    LogoUrl = logo,
                    Authors = authors,
                    WebsiteUrl = web,
                    LatestFiles = files
                });
            }
        }
        return list;
    }

    public static async Task<string> GetModDescriptionAsync(long modId)
    {
        var root = await CfGetAsync($"/mods/{modId}/description");
        var html = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(html)) return "Sin descripción disponible.";

        var text = System.Text.RegularExpressions.Regex.Replace(html, @"<\s*br\s*/?\s*>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"</\s*(p|div|h[1-6]|li|tr)\s*>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<\s*li[^>]*>", "• ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]+>", "");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]+\n", "\n");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    public static async Task<List<CurseForgeFileSummary>> GetModFilesAsync(long modId, string? gameVersion)
    {
        var ep = $"/mods/{modId}/files?pageSize=25";
        if (!string.IsNullOrWhiteSpace(gameVersion) && gameVersion != "Todas")
            ep += $"&gameVersion={Uri.EscapeDataString(gameVersion)}";
        var root = await CfGetAsync(ep);
        var list = new List<CurseForgeFileSummary>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var f in data.EnumerateArray()) list.Add(MapFile(f));
        return list;
    }

    public static async Task<List<string>> GetMcVersionsAsync(bool includeSnapshots)
    {
        var text = await Http.GetStringAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
        var root = JsonDocument.Parse(text).RootElement;
        var list = new List<string>();
        foreach (var v in root.GetProperty("versions").EnumerateArray())
        {
            var type = v.GetProperty("type").GetString();
            if (type == "release" || (includeSnapshots && type == "snapshot"))
                if (v.GetProperty("id").GetString() is string id) list.Add(id);
        }
        return list;
    }

    public static async Task<List<string>> GetLoaderVersionsAsync(string mcVersion, string loader)
    {
        var lower = loader.ToLowerInvariant();
        if (lower == "vanilla") return new();

        if (lower == "fabric")
        {
            var res = await Http.GetAsync($"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}");
            if (!res.IsSuccessStatusCode) return new();
            var arr = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
            var list = new List<string>();
            foreach (var item in arr.EnumerateArray().Take(25))
                if (item.GetProperty("loader").GetProperty("version").GetString() is string v) list.Add(v);
            return list;
        }

        var root = await CfGetAsync($"/minecraft/modloader?version={Uri.EscapeDataString(mcVersion)}&includeAll=true");
        var prefix = $"{lower}-";
        var result = new List<string>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                var name = item.GetProperty("name").GetString() ?? "";
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    result.Add(name[prefix.Length..]);
                if (result.Count >= 30) break;
            }
        }
        return result;
    }

    public static async Task DownloadFileAsync(string url, string destPath, Action<long, long>? onProgress = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        using var res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        res.EnsureSuccessStatusCode();
        var total = res.Content.Headers.ContentLength ?? 0;
        await using var input = await res.Content.ReadAsStreamAsync();
        await using var output = File.Create(destPath);
        var buf = new byte[65536];
        long downloaded = 0;
        int read;
        while ((read = await input.ReadAsync(buf)) > 0)
        {
            await output.WriteAsync(buf.AsMemory(0, read));
            downloaded += read;
            onProgress?.Invoke(downloaded, total);
        }
    }

    private static (string Loader, string? Version) ParseLoader(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return ("vanilla", null);
        var lower = raw.ToLowerInvariant();
        if (lower.StartsWith("neoforge-")) return ("neoforge", raw["neoforge-".Length..]);
        if (lower.StartsWith("forge-")) return ("forge", raw["forge-".Length..]);
        if (lower.StartsWith("fabric-")) return ("fabric", raw["fabric-".Length..]);
        if (lower.StartsWith("quilt-")) return ("quilt", raw["quilt-".Length..]);
        return ("vanilla", null);
    }

    private static readonly string[] IconCandidates =
    {
        "icon.png", "icon.jpg", "icon.jpeg", "icon.webp",
        "logo.png", "logo.jpg", "logo.webp",
        "pack.png", "pack.jpg",
        "instance.png", "thumbnail.png", "thumbnail.jpg"
    };

    public static string? DetectInstanceIcon(string instanceDir, string? extraSearchDir = null)
    {
        if (string.IsNullOrWhiteSpace(instanceDir)) return null;

        // 1. Check standard image files inside instanceDir
        if (Directory.Exists(instanceDir))
        {
            foreach (var name in IconCandidates)
            {
                var p = Path.Combine(instanceDir, name);
                if (File.Exists(p)) return p;
            }
        }

        // 2. Check extraSearchDir (e.g. extracted ZIP root) and copy into instanceDir
        if (!string.IsNullOrWhiteSpace(extraSearchDir) && Directory.Exists(extraSearchDir))
        {
            foreach (var name in IconCandidates)
            {
                var src = Path.Combine(extraSearchDir, name);
                if (File.Exists(src))
                {
                    try
                    {
                        var dest = Path.Combine(instanceDir, $"icon{Path.GetExtension(src)}");
                        File.Copy(src, dest, true);
                        return dest;
                    }
                    catch { return src; }
                }
            }
        }

        // 3. Check CurseForge App minecraftinstance.json (profileImagePath, installedModpack.thumbnailUrl / avatarUrl)
        var cfMeta = Path.Combine(instanceDir, "minecraftinstance.json");
        if (File.Exists(cfMeta))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(cfMeta));
                var root = doc.RootElement;
                if (root.TryGetProperty("profileImagePath", out var pip) && pip.ValueKind == JsonValueKind.String)
                {
                    var img = pip.GetString();
                    if (!string.IsNullOrWhiteSpace(img)) return img;
                }
                if (root.TryGetProperty("installedModpack", out var mp) && mp.ValueKind == JsonValueKind.Object)
                {
                    foreach (var key in new[] { "thumbnailUrl", "avatarUrl", "logoUrl" })
                    {
                        if (mp.TryGetProperty(key, out var tu) && tu.ValueKind == JsonValueKind.String)
                        {
                            var url = tu.GetString();
                            if (!string.IsNullOrWhiteSpace(url)) return url;
                        }
                    }
                }
            }
            catch { }
        }

        // 4. Check MultiMC / Prism instance.cfg iconKey
        var cfgPath = Path.Combine(instanceDir, "instance.cfg");
        if (File.Exists(cfgPath))
        {
            try
            {
                foreach (var line in File.ReadLines(cfgPath))
                {
                    if (line.StartsWith("iconKey=", StringComparison.OrdinalIgnoreCase))
                    {
                        var key = line["iconKey=".Length..].Trim();
                        if (!string.IsNullOrWhiteSpace(key))
                        {
                            foreach (var ext in new[] { ".png", ".jpg", ".webp" })
                            {
                                var p = Path.Combine(instanceDir, key + ext);
                                if (File.Exists(p)) return p;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        return null;
    }

    public record DetectedFolderMeta(string? Name, string? MinecraftVersion, string? Loader, string? LoaderVersion);

    public static DetectedFolderMeta DetectFolderInstanceMetadata(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return new(null, null, null, null);

        string? name = null;
        string? mcVer = null;
        string? loader = null;
        string? loaderVer = null;

        // 1. CurseForge App: minecraftinstance.json
        var cfInstPath = Path.Combine(folderPath, "minecraftinstance.json");
        if (File.Exists(cfInstPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(cfInstPath));
                var r = doc.RootElement;
                if (r.TryGetProperty("name", out var np) && np.ValueKind == JsonValueKind.String)
                    name ??= np.GetString();
                if (r.TryGetProperty("gameVersion", out var gv) && gv.ValueKind == JsonValueKind.String)
                    mcVer ??= gv.GetString();
                if (r.TryGetProperty("baseModLoader", out var bml) && bml.ValueKind == JsonValueKind.Object)
                {
                    if (bml.TryGetProperty("minecraftVersion", out var bmc) && bmc.ValueKind == JsonValueKind.String)
                        mcVer = bmc.GetString() ?? mcVer;
                    if (bml.TryGetProperty("name", out var bn) && bn.ValueKind == JsonValueKind.String)
                    {
                        var (l, lv) = ParseLoader(bn.GetString());
                        loader = l;
                        loaderVer = lv;
                    }
                }
            }
            catch { }
        }

        // 2. CurseForge Modpack: manifest.json
        var manifestPath = Path.Combine(folderPath, "manifest.json");
        if (string.IsNullOrEmpty(mcVer) && File.Exists(manifestPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var r = doc.RootElement;
                if (r.TryGetProperty("name", out var np) && np.ValueKind == JsonValueKind.String)
                    name ??= np.GetString();
                if (r.TryGetProperty("minecraft", out var mc) && mc.ValueKind == JsonValueKind.Object)
                {
                    if (mc.TryGetProperty("version", out var mv) && mv.ValueKind == JsonValueKind.String)
                        mcVer = mv.GetString();
                    if (mc.TryGetProperty("modLoaders", out var mls) && mls.ValueKind == JsonValueKind.Array)
                    {
                        string? raw = null;
                        foreach (var ml in mls.EnumerateArray())
                        {
                            if (ml.TryGetProperty("id", out var idp) && idp.ValueKind == JsonValueKind.String)
                            {
                                raw ??= idp.GetString();
                                if (ml.TryGetProperty("primary", out var pr) && pr.ValueKind == JsonValueKind.True)
                                {
                                    raw = idp.GetString();
                                    break;
                                }
                            }
                        }
                        if (!string.IsNullOrEmpty(raw))
                        {
                            var (l, lv) = ParseLoader(raw);
                            loader = l;
                            loaderVer = lv;
                        }
                    }
                }
            }
            catch { }
        }

        // 3. Prism / MultiMC: mmc-pack.json + instance.cfg
        var mmcPath = Path.Combine(folderPath, "mmc-pack.json");
        if (string.IsNullOrEmpty(mcVer) && File.Exists(mmcPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(mmcPath));
                if (doc.RootElement.TryGetProperty("components", out var comps) && comps.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in comps.EnumerateArray())
                    {
                        var uid = c.TryGetProperty("uid", out var up) ? up.GetString() : null;
                        var ver = c.TryGetProperty("version", out var vp) ? vp.GetString() : null;
                        if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(ver)) continue;
                        if (uid == "net.minecraft") mcVer = ver;
                        else if (uid == "net.minecraftforge") { loader = "forge"; loaderVer = ver; }
                        else if (uid == "net.neoforged") { loader = "neoforge"; loaderVer = ver; }
                        else if (uid == "net.fabricmc.fabric-loader") { loader = "fabric"; loaderVer = ver; }
                        else if (uid == "org.quiltmc.quilt-loader") { loader = "quilt"; loaderVer = ver; }
                    }
                }
            }
            catch { }
        }

        var cfgPath = Path.Combine(folderPath, "instance.cfg");
        if (string.IsNullOrEmpty(name) && File.Exists(cfgPath))
        {
            try
            {
                foreach (var line in File.ReadLines(cfgPath))
                {
                    if (line.StartsWith("name=", StringComparison.OrdinalIgnoreCase))
                    {
                        name = line["name=".Length..].Trim();
                        break;
                    }
                }
            }
            catch { }
        }

        // 4. Modrinth App: profile.json
        var profilePath = Path.Combine(folderPath, "profile.json");
        if (string.IsNullOrEmpty(mcVer) && File.Exists(profilePath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(profilePath));
                var r = doc.RootElement;
                if (r.TryGetProperty("name", out var np) && np.ValueKind == JsonValueKind.String) name ??= np.GetString();
                if (r.TryGetProperty("game_version", out var gv) && gv.ValueKind == JsonValueKind.String) mcVer = gv.GetString();
                if (r.TryGetProperty("loader", out var lp) && lp.ValueKind == JsonValueKind.String) loader = lp.GetString()?.ToLowerInvariant();
                if (r.TryGetProperty("loader_version", out var lvp) && lvp.ValueKind == JsonValueKind.String) loaderVer = lvp.GetString();
            }
            catch { }
        }

        // 5. Fallback: inspect logs/latest.log (first 60 lines)
        var latestLog = Path.Combine(folderPath, "logs", "latest.log");
        if (string.IsNullOrEmpty(mcVer) && File.Exists(latestLog))
        {
            try
            {
                var head = string.Join("\n", File.ReadLines(latestLog).Take(60));
                var fabricMatch = System.Text.RegularExpressions.Regex.Match(head, @"Loading Minecraft\s+([\d\.a-zA-Z\-]+)\s+with\s+(Fabric|Quilt)\s+Loader\s+([\d\.a-zA-Z\-\+]+)");
                if (fabricMatch.Success)
                {
                    mcVer = fabricMatch.Groups[1].Value;
                    loader = fabricMatch.Groups[2].Value.ToLowerInvariant();
                    loaderVer = fabricMatch.Groups[3].Value;
                }
                else
                {
                    var fmlMc = System.Text.RegularExpressions.Regex.Match(head, @"--fml\.mcVersion,\s*([\d\.a-zA-Z\-]+)");
                    if (fmlMc.Success) mcVer = fmlMc.Groups[1].Value;
                    var fmlForge = System.Text.RegularExpressions.Regex.Match(head, @"--fml\.forgeVersion,\s*([\d\.a-zA-Z\-]+)");
                    var fmlNeo = System.Text.RegularExpressions.Regex.Match(head, @"--fml\.neoForgeVersion,\s*([\d\.a-zA-Z\-]+)");
                    if (fmlNeo.Success) { loader = "neoforge"; loaderVer = fmlNeo.Groups[1].Value; }
                    else if (fmlForge.Success) { loader = "forge"; loaderVer = fmlForge.Groups[1].Value; }
                }
            }
            catch { }
        }

        return new(name, mcVer, loader, loaderVer);
    }

    public static async Task<InstanceConfig> InstallFromZipAsync(
        string zipPath,
        string? customName,
        string source,
        long? cfModId,
        long? cfFileId,
        string? iconUrl,
        Action<TaskProgressEvent> emit)
    {
        var settings = StoreService.LoadSettings();
        var id = Guid.NewGuid().ToString();
        var taskId = $"zip-{id[..8]}";
        var defaultName = Path.GetFileNameWithoutExtension(zipPath);

        JsonElement? manifestOpt = null;
        if (source == "curseforge")
        {
            using var archive = ArchiveFactory.Open(zipPath);
            var entry = archive.Entries.FirstOrDefault(e =>
                !e.IsDirectory && string.Equals(e.Key?.Replace('\\', '/'), "manifest.json", StringComparison.OrdinalIgnoreCase));
            if (entry != null)
            {
                using var stream = entry.OpenEntryStream();
                using var reader = new StreamReader(stream);
                manifestOpt = JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement.Clone();
            }
        }

        if (manifestOpt is JsonElement manifest)
        {
            var instName = !string.IsNullOrWhiteSpace(customName)
                ? customName.Trim()
                : (manifest.TryGetProperty("name", out var mn) ? mn.GetString() ?? defaultName : defaultName);
            var mcVer = manifest.GetProperty("minecraft").GetProperty("version").GetString() ?? "1.20.1";
            string? loaderRaw = null;
            if (manifest.GetProperty("minecraft").TryGetProperty("modLoaders", out var mlArr))
            {
                foreach (var l in mlArr.EnumerateArray())
                {
                    loaderRaw ??= l.GetProperty("id").GetString();
                    if (l.TryGetProperty("primary", out var pr) && pr.ValueKind == JsonValueKind.True)
                    {
                        loaderRaw = l.GetProperty("id").GetString();
                        break;
                    }
                }
            }
            var (loader, loaderVer) = ParseLoader(loaderRaw);

            var instDir = Path.Combine(settings.InstancesDir, $"{StoreService.SanitizeName(instName)}-{id[..6]}");
            var modsDir = Path.Combine(instDir, "mods");
            Directory.CreateDirectory(modsDir);

            var filesList = new List<(long ProjectId, long FileId)>();
            if (manifest.TryGetProperty("files", out var fArr))
                foreach (var f in fArr.EnumerateArray())
                    filesList.Add((f.GetProperty("projectID").GetInt64(), f.GetProperty("fileID").GetInt64()));

            emit(new(taskId, instName, id, "running", 10, $"Resolviendo {filesList.Count} mods..."));

            var metaMap = new Dictionary<long, JsonElement>();
            foreach (var chunk in filesList.Chunk(50))
            {
                try
                {
                    var ids = chunk.Select(x => x.FileId).ToArray();
                    var res = await CfPostAsync("/mods/files", new { fileIds = ids });
                    if (res.TryGetProperty("data", out var dArr))
                        foreach (var item in dArr.EnumerateArray())
                            metaMap[item.GetProperty("id").GetInt64()] = item.Clone();
                }
                catch { }
            }

            int completed = 0;
            int total = Math.Max(1, filesList.Count);
            using var sem = new SemaphoreSlim(6);
            await Task.WhenAll(filesList.Select(async entry =>
            {
                await sem.WaitAsync();
                try
                {
                    if (!metaMap.TryGetValue(entry.FileId, out var meta))
                    {
                        try
                        {
                            var single = await CfGetAsync($"/mods/{entry.ProjectId}/files/{entry.FileId}");
                            meta = single.GetProperty("data");
                        }
                        catch { meta = default; }
                    }

                    if (meta.ValueKind == JsonValueKind.Object &&
                        meta.TryGetProperty("downloadUrl", out var du) && du.ValueKind == JsonValueKind.String)
                    {
                        var fn = meta.TryGetProperty("fileName", out var fnp) ? fnp.GetString() ?? $"mod-{entry.FileId}.jar" : $"mod-{entry.FileId}.jar";
                        try { await DownloadFileAsync(du.GetString()!, Path.Combine(modsDir, fn)); } catch { }
                    }
                }
                finally
                {
                    var done = Interlocked.Increment(ref completed);
                    var pct = Math.Min(88, 15 + done * 73 / total);
                    emit(new(taskId, instName, id, "running", pct, $"Descargando mods ({done}/{total})"));
                    sem.Release();
                }
            }));

            emit(new(taskId, instName, id, "running", 92, "Aplicando overrides..."));
            var overridesName = manifest.TryGetProperty("overrides", out var ov) ? ov.GetString() ?? "overrides" : "overrides";
            var tmp = Path.Combine(instDir, ".tmp_extract");
            ExtractArchiveToDirectory(zipPath, tmp);
            var ovSrc = Path.Combine(tmp, overridesName);
            if (Directory.Exists(ovSrc)) StoreService.CopyDirectoryRecursive(ovSrc, instDir);
            var resolvedIcon = !string.IsNullOrWhiteSpace(iconUrl) ? iconUrl : DetectInstanceIcon(instDir, tmp);
            Directory.Delete(tmp, true);

            var inst = new InstanceConfig
            {
                Id = id,
                Name = instName,
                Path = instDir,
                MinecraftVersion = mcVer,
                Loader = loader,
                LoaderVersion = loaderVer,
                IconUrl = resolvedIcon,
                Source = source,
                CurseforgeModId = cfModId,
                CurseforgeFileId = cfFileId,
                ModCount = Directory.Exists(modsDir) ? Directory.GetFiles(modsDir, "*.jar").Length : 0,
                CreatedAt = DateTime.UtcNow.ToString("O"),
                LinkedExternal = false
            };
            StoreService.UpsertInstance(inst);
            emit(new(taskId, instName, id, "done", 100, "Instancia lista"));
            return inst;
        }

        // Raw ZIP / RAR (e.g. compressed instance folder)
        var rawName = !string.IsNullOrWhiteSpace(customName) ? customName.Trim() : defaultName;
        var rawDir = Path.Combine(settings.InstancesDir, $"{StoreService.SanitizeName(rawName)}-{id[..6]}");
        Directory.CreateDirectory(rawDir);
        emit(new(taskId, rawName, id, "running", 50, "Extrayendo archivo..."));
        ExtractArchiveToDirectory(zipPath, rawDir);

        // Flatten single root folder if user zipped/rarred the folder itself
        var topFiles = Directory.GetFiles(rawDir);
        var topDirs = Directory.GetDirectories(rawDir);
        if (topFiles.Length == 0 && topDirs.Length == 1)
        {
            var innerDir = topDirs[0];
            foreach (var f in Directory.GetFiles(innerDir))
                File.Move(f, Path.Combine(rawDir, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(innerDir))
            {
                var destSub = Path.Combine(rawDir, Path.GetFileName(d));
                if (Directory.Exists(destSub)) StoreService.CopyDirectoryRecursive(d, destSub);
                else Directory.Move(d, destSub);
            }
            try { Directory.Delete(innerDir, true); } catch { }
        }

        var detected = DetectFolderInstanceMetadata(rawDir);
        var rawIcon = !string.IsNullOrWhiteSpace(iconUrl) ? iconUrl : DetectInstanceIcon(rawDir);
        var rawModsDir = Path.Combine(rawDir, "mods");
        var modCount = Directory.Exists(rawModsDir) ? Directory.GetFiles(rawModsDir, "*.jar").Length : 0;

        var rawInst = new InstanceConfig
        {
            Id = id,
            Name = !string.IsNullOrWhiteSpace(detected.Name) ? detected.Name : rawName,
            Path = rawDir,
            MinecraftVersion = detected.MinecraftVersion ?? "1.20.1",
            Loader = detected.Loader ?? "vanilla",
            LoaderVersion = detected.LoaderVersion,
            IconUrl = rawIcon,
            ModCount = modCount,
            Source = "zip",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            LinkedExternal = false
        };
        StoreService.UpsertInstance(rawInst);
        emit(new(taskId, rawName, id, "done", 100, "ZIP importado"));
        return rawInst;
    }

    public static async Task<InstanceConfig> InstallClientModpackAsync(
        long modId, long fileId, string modpackName, string? iconUrl, Action<TaskProgressEvent> emit)
    {
        var taskId = $"cf-{modId}-{fileId}";
        emit(new(taskId, modpackName, null, "running", 4, "Descargando paquete del cliente..."));
        var meta = MapFile((await CfGetAsync($"/mods/{modId}/files/{fileId}")).GetProperty("data"));
        if (string.IsNullOrEmpty(meta.DownloadUrl))
            throw new Exception("El autor bloqueó la descarga directa del modpack.");

        var tmpZip = Path.Combine(StoreService.LoadSettings().InstancesDir, ".downloads", $"{modId}-{fileId}.zip");
        await DownloadFileAsync(meta.DownloadUrl, tmpZip);
        try
        {
            return await InstallFromZipAsync(tmpZip, modpackName, "curseforge", modId, fileId, iconUrl, emit);
        }
        finally
        {
            if (File.Exists(tmpZip)) File.Delete(tmpZip);
        }
    }

    public static async Task<CurseForgeFileSummary?> CheckModpackUpdateAsync(InstanceConfig inst)
    {
        if (inst.CurseforgeModId is not long modId) return null;
        var files = await GetModFilesAsync(modId, inst.MinecraftVersion);
        var latest = files.FirstOrDefault(f => !f.IsServerPack && !string.IsNullOrEmpty(f.DownloadUrl));
        if (latest == null) return null;
        if (inst.CurseforgeFileId.HasValue && latest.Id <= inst.CurseforgeFileId.Value) return null;
        return latest;
    }

    public static async Task<InstanceConfig> RestoreCurseForgeModsAsync(InstanceConfig inst, Action<TaskProgressEvent> emit)
    {
        if (inst.CurseforgeModId is not long modId || inst.CurseforgeFileId is not long fileId)
            throw new Exception("Esta instancia no tiene un archivo de CurseForge vinculado.");
        var meta = MapFile((await CfGetAsync($"/mods/{modId}/files/{fileId}")).GetProperty("data"));
        return await UpdateClientModpackAsync(inst, meta, emit);
    }

    public static async Task<InstanceConfig> UpdateClientModpackAsync(
        InstanceConfig inst, CurseForgeFileSummary targetFile, Action<TaskProgressEvent> emit)
    {
        if (inst.CurseforgeModId is not long modId)
            throw new Exception("Esta instancia no está vinculada a CurseForge.");
        if (string.IsNullOrEmpty(targetFile.DownloadUrl))
            throw new Exception("El archivo de actualización no tiene descarga directa.");

        var taskId = $"upd-{inst.Id[..8]}";
        emit(new(taskId, $"Actualizando {inst.Name}", inst.Id, "running", 5, $"Descargando {targetFile.DisplayName}..."));

        var tmpZip = Path.Combine(StoreService.LoadSettings().InstancesDir, ".downloads", $"upd-{modId}-{targetFile.Id}.zip");
        await DownloadFileAsync(targetFile.DownloadUrl, tmpZip);
        try
        {
            JsonElement? manifestOpt = null;
            using (var archive = ZipFile.OpenRead(tmpZip))
            {
                var entry = archive.GetEntry("manifest.json");
                if (entry != null)
                {
                    using var reader = new StreamReader(entry.Open());
                    manifestOpt = JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement.Clone();
                }
            }

            if (manifestOpt is not JsonElement manifest)
                throw new Exception("El paquete de actualización no contiene manifest.json.");

            var mcVer = manifest.GetProperty("minecraft").GetProperty("version").GetString() ?? inst.MinecraftVersion;
            string? loaderRaw = null;
            if (manifest.GetProperty("minecraft").TryGetProperty("modLoaders", out var mlArr))
            {
                foreach (var l in mlArr.EnumerateArray())
                {
                    loaderRaw ??= l.GetProperty("id").GetString();
                    if (l.TryGetProperty("primary", out var pr) && pr.ValueKind == JsonValueKind.True)
                    {
                        loaderRaw = l.GetProperty("id").GetString();
                        break;
                    }
                }
            }
            var (loader, loaderVer) = ParseLoader(loaderRaw);

            var modsDir = Path.Combine(inst.Path, "mods");
            if (Directory.Exists(modsDir))
            {
                foreach (var jar in Directory.EnumerateFiles(modsDir, "*.jar"))
                    try { File.Delete(jar); } catch { }
            }
            Directory.CreateDirectory(modsDir);

            var filesList = new List<(long ProjectId, long FileId)>();
            if (manifest.TryGetProperty("files", out var fArr))
                foreach (var f in fArr.EnumerateArray())
                    filesList.Add((f.GetProperty("projectID").GetInt64(), f.GetProperty("fileID").GetInt64()));

            emit(new(taskId, $"Actualizando {inst.Name}", inst.Id, "running", 12, $"Resolviendo {filesList.Count} mods..."));

            var metaMap = new Dictionary<long, JsonElement>();
            foreach (var chunk in filesList.Chunk(50))
            {
                try
                {
                    var ids = chunk.Select(x => x.FileId).ToArray();
                    var res = await CfPostAsync("/mods/files", new { fileIds = ids });
                    if (res.TryGetProperty("data", out var dArr))
                        foreach (var item in dArr.EnumerateArray())
                            metaMap[item.GetProperty("id").GetInt64()] = item.Clone();
                }
                catch { }
            }

            int completed = 0;
            int total = Math.Max(1, filesList.Count);
            using var sem = new SemaphoreSlim(6);
            await Task.WhenAll(filesList.Select(async entry =>
            {
                await sem.WaitAsync();
                try
                {
                    if (!metaMap.TryGetValue(entry.FileId, out var meta))
                    {
                        try
                        {
                            var single = await CfGetAsync($"/mods/{entry.ProjectId}/files/{entry.FileId}");
                            meta = single.GetProperty("data");
                        }
                        catch { meta = default; }
                    }

                    if (meta.ValueKind == JsonValueKind.Object &&
                        meta.TryGetProperty("downloadUrl", out var du) && du.ValueKind == JsonValueKind.String)
                    {
                        var fn = meta.TryGetProperty("fileName", out var fnp) ? fnp.GetString() ?? $"mod-{entry.FileId}.jar" : $"mod-{entry.FileId}.jar";
                        try { await DownloadFileAsync(du.GetString()!, Path.Combine(modsDir, fn)); } catch { }
                    }
                }
                finally
                {
                    var done = Interlocked.Increment(ref completed);
                    var pct = Math.Min(88, 15 + done * 73 / total);
                    emit(new(taskId, $"Actualizando {inst.Name}", inst.Id, "running", pct, $"Descargando mods ({done}/{total})"));
                    sem.Release();
                }
            }));

            emit(new(taskId, $"Actualizando {inst.Name}", inst.Id, "running", 92, "Aplicando overrides..."));
            var overridesName = manifest.TryGetProperty("overrides", out var ov) ? ov.GetString() ?? "overrides" : "overrides";
            var tmp = Path.Combine(inst.Path, ".tmp_update");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            ZipFile.ExtractToDirectory(tmpZip, tmp, true);
            var ovSrc = Path.Combine(tmp, overridesName);
            if (Directory.Exists(ovSrc)) StoreService.CopyDirectoryRecursive(ovSrc, inst.Path);
            Directory.Delete(tmp, true);

            inst.MinecraftVersion = mcVer;
            inst.Loader = loader;
            inst.LoaderVersion = loaderVer;
            inst.CurseforgeFileId = targetFile.Id;
            inst.ModCount = filesList.Count;
            StoreService.UpsertInstance(inst);

            emit(new(taskId, $"Actualizando {inst.Name}", inst.Id, "done", 100, "Modpack actualizado"));
            return inst;
        }
        finally
        {
            if (File.Exists(tmpZip)) File.Delete(tmpZip);
        }
    }

    public static async Task<string> DownloadServerPackAsync(
        long modId, long serverPackFileId, string modpackName, string? targetDir, Action<TaskProgressEvent> emit)
    {
        var taskId = $"sp-{modId}-{serverPackFileId}";
        emit(new(taskId, $"{modpackName} (Server Pack)", null, "running", 5, "Consultando Server Pack..."));
        var meta = MapFile((await CfGetAsync($"/mods/{modId}/files/{serverPackFileId}")).GetProperty("data"));
        if (string.IsNullOrEmpty(meta.DownloadUrl))
            throw new Exception("El Server Pack no tiene descarga directa habilitada.");

        var baseDir = !string.IsNullOrWhiteSpace(targetDir)
            ? targetDir
            : Path.Combine(StoreService.LoadSettings().ServersDir, StoreService.SanitizeName(modpackName));
        Directory.CreateDirectory(baseDir);
        var zipPath = Path.Combine(baseDir, meta.FileName);

        int lastPct = 5;
        await DownloadFileAsync(meta.DownloadUrl, zipPath, (dl, total) =>
        {
            var pct = total > 0 ? Math.Min(85, 5 + (int)(dl * 80 / total)) : 45;
            if (pct != lastPct)
            {
                lastPct = pct;
                emit(new(taskId, $"{modpackName} (Server Pack)", null, "running", pct, $"Descargando ({dl / 1024 / 1024} MB)..."));
            }
        });

        emit(new(taskId, $"{modpackName} (Server Pack)", null, "running", 92, "Extrayendo Server Pack..."));
        var outDir = Path.Combine(baseDir, StoreService.SanitizeName(Path.GetFileNameWithoutExtension(meta.FileName)));
        ExtractArchiveToDirectory(zipPath, outDir);
        emit(new(taskId, $"{modpackName} (Server Pack)", null, "done", 100, "Server Pack listo"));
        return outDir;
    }

    private static void ExtractArchiveToDirectory(string archivePath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        using var archive = ArchiveFactory.Open(archivePath);
        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
        {
            try
            {
                entry.WriteToDirectory(destDir, new ExtractionOptions
                {
                    ExtractFullPath = true,
                    Overwrite = true
                });
            }
            catch { }
        }
    }
}

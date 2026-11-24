using System.Text.Json.Serialization;

namespace KuroLauncher.Models;

public class MinecraftAccount
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "offline";
    [JsonPropertyName("accessToken")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("refreshToken")] public string? RefreshToken { get; set; }
    [JsonPropertyName("expiresAt")] public long? ExpiresAt { get; set; }
}

public class LauncherSettings
{
    [JsonPropertyName("javaPath")] public string JavaPath { get; set; } = "java";
    [JsonPropertyName("minRamMb")] public int MinRamMb { get; set; } = 2048;
    [JsonPropertyName("maxRamMb")] public int MaxRamMb { get; set; } = 6144;
    [JsonPropertyName("instancesDir")] public string InstancesDir { get; set; } = "";
    [JsonPropertyName("serversDir")] public string ServersDir { get; set; } = "";
    [JsonPropertyName("curseforgeApiKey")] public string? CurseforgeApiKey { get; set; }
    [JsonPropertyName("backgroundImage")] public string? BackgroundImage { get; set; }
    [JsonPropertyName("backgroundBrightness")] public double BackgroundBrightness { get; set; } = 0.55;
    [JsonPropertyName("backgroundBlur")] public int BackgroundBlur { get; set; } = 6;
    [JsonPropertyName("instancesViewMode")] public string InstancesViewMode { get; set; } = "grid";

    public LauncherSettings Clone() => new()
    {
        JavaPath = JavaPath,
        MinRamMb = MinRamMb,
        MaxRamMb = MaxRamMb,
        InstancesDir = InstancesDir,
        ServersDir = ServersDir,
        CurseforgeApiKey = CurseforgeApiKey,
        BackgroundImage = BackgroundImage,
        BackgroundBrightness = BackgroundBrightness,
        BackgroundBlur = BackgroundBlur,
        InstancesViewMode = InstancesViewMode,
    };

    public bool ValueEquals(LauncherSettings other) =>
        JavaPath == other.JavaPath &&
        MinRamMb == other.MinRamMb &&
        MaxRamMb == other.MaxRamMb &&
        InstancesDir == other.InstancesDir &&
        ServersDir == other.ServersDir &&
        (BackgroundImage ?? "") == (other.BackgroundImage ?? "") &&
        Math.Abs(BackgroundBrightness - other.BackgroundBrightness) < 0.005 &&
        BackgroundBlur == other.BackgroundBlur &&
        InstancesViewMode == other.InstancesViewMode;
}

public class InstanceConfig
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("minecraftVersion")] public string MinecraftVersion { get; set; } = "1.20.1";
    [JsonPropertyName("loader")] public string Loader { get; set; } = "vanilla";
    [JsonPropertyName("loaderVersion")] public string? LoaderVersion { get; set; }
    [JsonPropertyName("iconUrl")] public string? IconUrl { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "manual";
    [JsonPropertyName("curseforgeModId")] public long? CurseforgeModId { get; set; }
    [JsonPropertyName("curseforgeFileId")] public long? CurseforgeFileId { get; set; }
    [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("lastPlayed")] public string? LastPlayed { get; set; }
    [JsonPropertyName("modCount")] public int? ModCount { get; set; }
    [JsonPropertyName("linkedExternal")] public bool? LinkedExternal { get; set; }
    [JsonPropertyName("minRamMb")] public int? MinRamMb { get; set; }
    [JsonPropertyName("maxRamMb")] public int? MaxRamMb { get; set; }
    [JsonPropertyName("javaPath")] public string? JavaPath { get; set; }
    [JsonPropertyName("jvmArgs")] public string? JvmArgs { get; set; }
    [JsonPropertyName("windowWidth")] public int? WindowWidth { get; set; }
    [JsonPropertyName("windowHeight")] public int? WindowHeight { get; set; }
    [JsonPropertyName("fullscreen")] public bool? Fullscreen { get; set; }
}

public class CurseForgeFileSummary
{
    public long Id { get; set; }
    public string DisplayName { get; set; } = "";
    public string FileName { get; set; } = "";
    public string FileDate { get; set; } = "";
    public long FileLength { get; set; }
    public string? DownloadUrl { get; set; }
    public List<string> GameVersions { get; set; } = new();
    public bool IsServerPack { get; set; }
    public long? ServerPackFileId { get; set; }
    public bool HasServerPack => ServerPackFileId.HasValue;
    public string VersionsLine => string.Join(" · ", GameVersions.Take(5));
    public string SizeLabel => FileLength >= 1024 * 1024
        ? $"{FileLength / (1024.0 * 1024.0):0.1} MB"
        : $"{Math.Max(1, FileLength / 1024)} KB";
}

public class CurseForgeModpack
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Summary { get; set; } = "";
    public long DownloadCount { get; set; }
    public string? LogoUrl { get; set; }
    public List<string> Authors { get; set; } = new();
    public string? WebsiteUrl { get; set; }
    public List<CurseForgeFileSummary> LatestFiles { get; set; } = new();

    public string AuthorLabel => Authors.Count > 0 ? $"por {Authors[0]}" : "CurseForge";
    public string DownloadsLabel => DownloadCount >= 1_000_000
        ? $"{DownloadCount / 1_000_000.0:0.1}M descargas"
        : $"{Math.Max(1, DownloadCount / 1000)}K descargas";
    public bool HasServerPack => LatestFiles.Any(f => f.ServerPackFileId.HasValue);
}

public class DeviceCodePrompt
{
    public string UserCode { get; set; } = "";
    public string VerificationUri { get; set; } = "";
    public string DeviceCode { get; set; } = "";
    public int Interval { get; set; } = 5;
}

public record TaskProgressEvent(string TaskId, string Title, string? InstanceId, string Status, int Percent, string Message);
public record ConsoleLogEvent(string Level, string Message, string Timestamp);

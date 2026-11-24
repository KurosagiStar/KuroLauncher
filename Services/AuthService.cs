using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KuroLauncher.Models;

namespace KuroLauncher.Services;

public static class AuthService
{
    private const string DeviceClientId = "c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb";
    private static readonly HttpClient Http = new();

    public static async Task<DeviceCodePrompt> StartDeviceCodeAsync()
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = DeviceClientId,
            ["scope"] = "XboxLive.signin offline_access"
        });
        var res = await Http.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode", form);
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

        if (!json.TryGetProperty("user_code", out var uc))
            throw new Exception($"Error iniciando Device Code: {json}");

        var prompt = new DeviceCodePrompt
        {
            UserCode = uc.GetString() ?? "",
            VerificationUri = json.TryGetProperty("verification_uri", out var vu)
                ? vu.GetString() ?? "https://www.microsoft.com/link"
                : "https://www.microsoft.com/link",
            DeviceCode = json.GetProperty("device_code").GetString() ?? "",
            Interval = json.TryGetProperty("interval", out var iv) ? iv.GetInt32() : 5
        };

        StoreService.OpenUrl(prompt.VerificationUri);
        return prompt;
    }

    public static async Task<MinecraftAccount> PollDeviceCodeAsync(DeviceCodePrompt prompt, CancellationToken ct)
    {
        for (int i = 0; i < 120; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(3, prompt.Interval)), ct);
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = DeviceClientId,
                ["device_code"] = prompt.DeviceCode
            });
            var res = await Http.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token", form, ct);
            var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement;

            if (json.TryGetProperty("error", out var err))
            {
                var code = err.GetString();
                if (code is "authorization_pending" or "slow_down") continue;
                if (code == "authorization_declined") throw new Exception("Inicio de sesión cancelado.");
                if (code == "expired_token") throw new Exception("El código expiró. Intenta nuevamente.");
                throw new Exception($"Error OAuth Microsoft: {code}");
            }

            if (json.TryGetProperty("access_token", out var at))
            {
                var refresh = json.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
                return await CompleteXboxMcChainAsync(at.GetString()!, refresh);
            }
        }
        throw new Exception("Tiempo de espera agotado.");
    }

    public static async Task<MinecraftAccount?> EnsureFreshAccountAsync()
    {
        var acc = StoreService.LoadAccount();
        if (acc == null || acc.Type == "offline") return acc;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (acc.ExpiresAt.HasValue && now < acc.ExpiresAt.Value - 10 * 60 * 1000)
            return acc;

        if (!string.IsNullOrEmpty(acc.RefreshToken))
        {
            try
            {
                var form = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = DeviceClientId,
                    ["refresh_token"] = acc.RefreshToken,
                    ["grant_type"] = "refresh_token",
                    ["scope"] = "XboxLive.signin offline_access"
                });
                var res = await Http.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token", form);
                var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
                if (json.TryGetProperty("access_token", out var at))
                {
                    var nextRefresh = json.TryGetProperty("refresh_token", out var rt)
                        ? rt.GetString()
                        : acc.RefreshToken;
                    return await CompleteXboxMcChainAsync(at.GetString()!, nextRefresh);
                }
            }
            catch { }
        }
        return acc;
    }

    private static async Task<MinecraftAccount> CompleteXboxMcChainAsync(string msAccess, string? refreshToken)
    {
        var xblPayload = JsonSerializer.Serialize(new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = $"d={msAccess}"
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        });
        var xblRes = await Http.PostAsync("https://user.auth.xboxlive.com/user/authenticate",
            new StringContent(xblPayload, Encoding.UTF8, "application/json"));
        var xblJson = JsonDocument.Parse(await xblRes.Content.ReadAsStringAsync()).RootElement;
        var xblToken = xblJson.GetProperty("Token").GetString()
            ?? throw new Exception("Error autenticando con Xbox Live.");
        var uhs = xblJson.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString()
            ?? throw new Exception("No se obtuvo UHS de Xbox Live.");

        var xstsPayload = JsonSerializer.Serialize(new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xblToken } },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT"
        });
        var xstsRes = await Http.PostAsync("https://xsts.auth.xboxlive.com/xsts/authorize",
            new StringContent(xstsPayload, Encoding.UTF8, "application/json"));
        var xstsJson = JsonDocument.Parse(await xstsRes.Content.ReadAsStringAsync()).RootElement;
        if (!xstsJson.TryGetProperty("Token", out var xstsTokProp))
            throw new Exception("Esta cuenta Microsoft no tiene perfil de Xbox válido.");
        var xstsToken = xstsTokProp.GetString()!;

        var mcPayload = JsonSerializer.Serialize(new { identityToken = $"XBL3.0 x={uhs};{xstsToken}" });
        var mcRes = await Http.PostAsync("https://api.minecraftservices.com/authentication/login_with_xbox",
            new StringContent(mcPayload, Encoding.UTF8, "application/json"));
        var mcJson = JsonDocument.Parse(await mcRes.Content.ReadAsStringAsync()).RootElement;
        var mcAccess = mcJson.GetProperty("access_token").GetString()
            ?? throw new Exception("No se obtuvo token de Minecraft Services.");

        using var profReq = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        profReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcAccess);
        var profRes = await Http.SendAsync(profReq);
        var profJson = JsonDocument.Parse(await profRes.Content.ReadAsStringAsync()).RootElement;
        if (!profJson.TryGetProperty("id", out var idProp))
            throw new Exception("Esta cuenta Microsoft no posee Minecraft Java Edition.");

        var account = new MinecraftAccount
        {
            Id = idProp.GetString()!,
            Name = profJson.GetProperty("name").GetString()!,
            Type = "microsoft",
            AccessToken = mcAccess,
            RefreshToken = refreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 20 * 60 * 60 * 1000
        };
        StoreService.SaveAccount(account);
        return account;
    }

    public static MinecraftAccount LoginOffline(string username)
    {
        var clean = new string(username.Trim().Where(c => char.IsLetterOrDigit(c) || c == '_').Take(16).ToArray());
        if (string.IsNullOrEmpty(clean)) clean = "Player";
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes($"OfflinePlayer:{clean}"))).ToLowerInvariant();
        var uuid = $"{hash[..8]}-{hash[8..12]}-{hash[12..16]}-{hash[16..20]}-{hash[20..32]}";

        var account = new MinecraftAccount
        {
            Id = uuid,
            Name = clean,
            Type = "offline",
            AccessToken = "offline-token"
        };
        StoreService.SaveAccount(account);
        return account;
    }
}

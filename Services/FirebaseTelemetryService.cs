using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlareVpn.Services;

/// <summary>
/// Optional, anonymous Firebase telemetry. Only sends data after explicit opt-in.
/// The web API key identifies the Firebase project; database rules and Auth tokens
/// provide authorization. No service-account credential is shipped in the app.
/// </summary>
public sealed class FirebaseTelemetryService : IDisposable
{
    private const string ApiKey = "AIzaSyBGsqWBMGg5DxfmzuvAyypJ_LqYFLp9ZtI";
    private const string ProjectId = "invertible-pipe-452821-h7";
    private const string Collection = "VBNGS";
    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes(ProjectId + ":VPNGS");
    public static string AppVersion { get; } = typeof(FirebaseTelemetryService).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion
        ?? typeof(FirebaseTelemetryService).Assembly.GetName().Version?.ToString()
        ?? "unknown";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private readonly UserProfile _profile;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FirebaseTokens? _tokens;
    private string? _sessionId;
    private DateTime _sessionStartedUtc;
    private bool _disposed;

    private sealed record FirebaseTokens(string UserId, string IdToken, string RefreshToken, DateTime ExpiresAtUtc);
    private sealed record AuthResponse(string UserId, string IdToken, string RefreshToken, int ExpiresInSeconds);
    public sealed class UserBannedException(string reason) : Exception(reason);
    public static FirebaseTelemetryService? Current { get; private set; }

    public FirebaseTelemetryService(UserProfile profile)
    {
        _profile = profile;
        Current = this;
    }

    public async Task StartSessionAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var tokens = await GetTokensAsync(ct);
            var now = DateTime.UtcNow;
            var currentProfileId = UserProfileService.ProfileDocumentId(_profile.InstallId);

            // Read the admin-controlled ban flag before writing session activity.
            using var existingProfile = await GetDocumentAsync(tokens, ProfilePath(tokens.UserId, currentProfileId), ct);
            if (TryGetBoolean(existingProfile, "isBanned", out var isBanned) && isBanned)
            {
                var reason = TryGetString(existingProfile, "banReason") ?? "تم إيقاف هذا الحساب بواسطة الإدارة.";
                throw new UserBannedException(reason);
            }

            // Close a session left open by a crash/forced process termination using
            // its last persisted heartbeat, so time is not overstated.
            if (!string.IsNullOrWhiteSpace(_profile.ActiveSessionId) && _profile.ActiveSessionStartedAtUtc.HasValue)
            {
                var oldStart = _profile.ActiveSessionStartedAtUtc.Value;
                var oldEnd = _profile.LastHeartbeatAtUtc ?? oldStart;
                var interruptedProfileId = _profile.ActiveProfileId ?? currentProfileId;
                await PatchDocumentAsync(tokens, SessionPath(tokens.UserId, interruptedProfileId, _profile.ActiveSessionId),
                    Fields(
                        ("endedAtUtc", Timestamp(oldEnd)),
                        ("durationSeconds", Integer(Math.Max(0, (long)(oldEnd - oldStart).TotalSeconds))),
                        ("endReason", String("unexpected_exit"))), ct);
            }

            var profileFields = Fields(
                ("displayName", String(_profile.DisplayName)),
                ("installId", String(_profile.InstallId)),
                ("firebaseUid", String(tokens.UserId)),
                ("profileId", String(currentProfileId)),
                ("appVersion", String(AppVersion)),
                ("isBanned", Boolean(false)),
                ("firstSeenAtUtc", Timestamp(_profile.CreatedAtUtc)),
                ("lastSeenAtUtc", Timestamp(now)));
            await PatchDocumentAsync(tokens, ProfilePath(tokens.UserId, currentProfileId), profileFields, ct);

            _sessionId = Guid.NewGuid().ToString("N");
            _sessionStartedUtc = now;
            await PatchDocumentAsync(tokens, SessionPath(tokens.UserId, currentProfileId, _sessionId),
                Fields(("startedAtUtc", Timestamp(now)), ("lastHeartbeatAtUtc", Timestamp(now)), ("appVersion", String(AppVersion))), ct);
            _profile.FirebaseUid = tokens.UserId;
            _profile.ActiveSessionId = _sessionId;
            _profile.ActiveProfileId = currentProfileId;
            _profile.ActiveSessionStartedAtUtc = now;
            _profile.LastHeartbeatAtUtc = now;
            UserProfileService.Save(_profile);
            Console.WriteLine($"[Firebase] Session started for profile '{_profile.DisplayName}' (anonymous ID {tokens.UserId}).");
        }
        catch (UserBannedException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Console.WriteLine($"[Firebase][WARN] Could not start telemetry session; the app will continue locally: {ex.GetBaseException().Message}");
        }
        finally { _gate.Release(); }
    }

    public async Task HeartbeatAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_sessionId)) return;
        await _gate.WaitAsync(ct);
        try
        {
            var tokens = await GetTokensAsync(ct);
            var now = DateTime.UtcNow;
            var profileId = UserProfileService.ProfileDocumentId(_profile.InstallId);
            await PatchDocumentAsync(tokens, SessionPath(tokens.UserId, profileId, _sessionId),
                Fields(("lastHeartbeatAtUtc", Timestamp(now))), ct);
            await PatchDocumentAsync(tokens, ProfilePath(tokens.UserId, profileId),
                Fields(("lastSeenAtUtc", Timestamp(now))), ct);
            _profile.LastHeartbeatAtUtc = now;
            UserProfileService.Save(_profile);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Console.WriteLine($"[Firebase][WARN] Session heartbeat failed: {ex.GetBaseException().Message}");
        }
        finally { _gate.Release(); }
    }

    public async Task EndSessionAsync(string reason = "normal_exit", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_sessionId)) return;
        await _gate.WaitAsync(ct);
        try
        {
            var tokens = await GetTokensAsync(ct);
            var ended = DateTime.UtcNow;
            var profileId = _profile.ActiveProfileId ?? UserProfileService.ProfileDocumentId(_profile.InstallId);
            await PatchDocumentAsync(tokens, SessionPath(tokens.UserId, profileId, _sessionId),
                Fields(
                    ("endedAtUtc", Timestamp(ended)),
                    ("lastHeartbeatAtUtc", Timestamp(ended)),
                    ("durationSeconds", Integer(Math.Max(0, (long)(ended - _sessionStartedUtc).TotalSeconds))),
                    ("endReason", String(reason))), ct);
            await PatchDocumentAsync(tokens, ProfilePath(tokens.UserId, profileId), Fields(("lastSeenAtUtc", Timestamp(ended))), ct);
            ClearLocalSession();
            Console.WriteLine($"[Firebase] Session ended; duration={(ended - _sessionStartedUtc).TotalSeconds:0}s.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Console.WriteLine($"[Firebase][WARN] Could not close telemetry session: {ex.GetBaseException().Message}");
        }
        finally { _gate.Release(); }
    }

    public async Task ReportExceptionAsync(Exception exception, string context, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_sessionId)) return;
        try
        {
            var tokens = await GetTokensAsync(ct);
            var material = (exception.GetType().FullName ?? "Exception") + "\n" + (exception.StackTrace ?? "");
            var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..20].ToLowerInvariant();
            var eventId = Guid.NewGuid().ToString("N");
            var profileId = UserProfileService.ProfileDocumentId(_profile.InstallId);
            await PatchDocumentAsync(tokens, $"{Collection}/{tokens.UserId}/profiles/{profileId}/errors/{eventId}",
                Fields(
                    ("occurredAtUtc", Timestamp(DateTime.UtcNow)),
                    ("context", String(context)),
                    ("errorType", String(exception.GetType().Name)),
                    ("signature", String(signature)),
                    ("appVersion", String(AppVersion))), ct);
            Console.WriteLine($"[Firebase] Uploaded sanitized error event {eventId} ({exception.GetType().Name}, {signature}).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Console.WriteLine($"[Firebase][WARN] Could not upload error event: {ex.GetBaseException().Message}");
        }
    }

    private async Task<FirebaseTokens> GetTokensAsync(CancellationToken ct)
    {
        if (_tokens is { } cached && cached.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(2)) return cached;
        var refreshToken = LoadRefreshToken();
        AuthResponse auth;
        var recreatedIdentity = false;
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            try
            {
                using var response = await _http.PostAsync(
                    $"https://securetoken.googleapis.com/v1/token?key={ApiKey}",
                    new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken
                    }), ct);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var root = json.RootElement;
                auth = new AuthResponse(
                    root.GetProperty("user_id").GetString()!, root.GetProperty("id_token").GetString()!,
                    root.GetProperty("refresh_token").GetString()!, int.Parse(root.GetProperty("expires_in").GetString()!, CultureInfo.InvariantCulture));
            }
            catch (HttpRequestException) { auth = await CreateAnonymousUserAsync(ct); recreatedIdentity = true; }
            catch (JsonException) { auth = await CreateAnonymousUserAsync(ct); recreatedIdentity = true; }
        }
        else auth = await CreateAnonymousUserAsync(ct);

        if (recreatedIdentity && !string.IsNullOrWhiteSpace(_profile.FirebaseUid))
        {
            _profile.ActiveSessionId = null;
            _profile.ActiveProfileId = null;
            _profile.ActiveSessionStartedAtUtc = null;
            _profile.LastHeartbeatAtUtc = null;
        }
        _profile.FirebaseUid = auth.UserId;
        UserProfileService.Save(_profile);
        SaveRefreshToken(auth.RefreshToken);
        _tokens = new FirebaseTokens(auth.UserId, auth.IdToken, auth.RefreshToken,
            DateTime.UtcNow.AddSeconds(Math.Max(60, auth.ExpiresInSeconds)));
        return _tokens;
    }

    private async Task<AuthResponse> CreateAnonymousUserAsync(CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(
            $"https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={ApiKey}",
            new { returnSecureToken = true }, ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;
        return new AuthResponse(root.GetProperty("localId").GetString()!, root.GetProperty("idToken").GetString()!,
            root.GetProperty("refreshToken").GetString()!, int.Parse(root.GetProperty("expiresIn").GetString()!, CultureInfo.InvariantCulture));
    }

    private async Task PatchDocumentAsync(FirebaseTokens tokens, string documentPath, JsonObject fields, CancellationToken ct)
    {
        var masks = string.Join("&", fields.Select(pair => "updateMask.fieldPaths=" + Uri.EscapeDataString(pair.Key)));
        var uri = $"https://firestore.googleapis.com/v1/projects/{ProjectId}/databases/(default)/documents/{documentPath}?{masks}";
        using var request = new HttpRequestMessage(HttpMethod.Patch, uri)
        {
            Content = new StringContent(new JsonObject { ["fields"] = fields }.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.IdToken);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Firestore HTTP {(int)response.StatusCode}: {detail}");
        }
    }

    private async Task<JsonDocument?> GetDocumentAsync(FirebaseTokens tokens, string documentPath, CancellationToken ct)
    {
        var uri = $"https://firestore.googleapis.com/v1/projects/{ProjectId}/databases/(default)/documents/{documentPath}";
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.IdToken);
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Firestore profile read HTTP {(int)response.StatusCode}: {detail}");
        }
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static bool TryGetBoolean(JsonDocument? document, string fieldName, out bool value)
    {
        value = false;
        return document?.RootElement.TryGetProperty("fields", out var fields) == true
            && fields.TryGetProperty(fieldName, out var field)
            && field.TryGetProperty("booleanValue", out var booleanValue)
            && ReadBoolean(booleanValue, out value);
    }

    private static bool ReadBoolean(JsonElement element, out bool value)
    {
        value = element.ValueKind == JsonValueKind.True;
        return element.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    private static string? TryGetString(JsonDocument? document, string fieldName)
    {
        if (document?.RootElement.TryGetProperty("fields", out var fields) == true
            && fields.TryGetProperty(fieldName, out var field)
            && field.TryGetProperty("stringValue", out var stringValue))
            return stringValue.GetString();
        return null;
    }

    private static string ProfilePath(string uid, string profileId) => $"{Collection}/{uid}/profiles/{profileId}";
    private static string SessionPath(string uid, string profileId, string sessionId) =>
        $"{ProfilePath(uid, profileId)}/sessions/{sessionId}";

    private static JsonObject Fields(params (string Name, JsonObject Value)[] values)
    {
        var fields = new JsonObject();
        foreach (var (name, value) in values) fields[name] = value;
        return fields;
    }

    private static JsonObject String(string value) => new() { ["stringValue"] = value };
    private static JsonObject Boolean(bool value) => new() { ["booleanValue"] = value };
    private static JsonObject Integer(long value) => new() { ["integerValue"] = value.ToString(CultureInfo.InvariantCulture) };
    private static JsonObject Timestamp(DateTime value) => new() { ["timestampValue"] = value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) };

    private static string? LoadRefreshToken()
    {
        try
        {
            var bytes = File.ReadAllBytes(UserProfileService.AuthTokenPath);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, DpapiEntropy, DataProtectionScope.CurrentUser));
        }
        catch { return null; }
    }

    private static void SaveRefreshToken(string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UserProfileService.AuthTokenPath)!);
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), DpapiEntropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(UserProfileService.AuthTokenPath, protectedBytes);
    }

    private void ClearLocalSession()
    {
        _sessionId = null;
        _profile.ActiveSessionId = null;
        _profile.ActiveProfileId = null;
        _profile.ActiveSessionStartedAtUtc = null;
        _profile.LastHeartbeatAtUtc = null;
        UserProfileService.Save(_profile);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ReferenceEquals(Current, this)) Current = null;
        _http.Dispose();
        _gate.Dispose();
    }
}

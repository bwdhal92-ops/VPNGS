using System.IO;
using System.Text.Json;

namespace FlareVpn.Services;

public sealed class UserProfile
{
    public string DisplayName { get; set; } = "";
    public string InstallId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string FirebaseUid { get; set; } = "";
    public string? ActiveSessionId { get; set; }
    public string? ActiveProfileId { get; set; }
    public DateTime? ActiveSessionStartedAtUtc { get; set; }
    public DateTime? LastHeartbeatAtUtc { get; set; }
}

public static class UserProfileService
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VPNGS");
    private static readonly string ProfilePath = Path.Combine(DirectoryPath, "user-profile.json");

    public static UserProfile? Load()
    {
        try
        {
            if (!File.Exists(ProfilePath)) return null;
            var profile = JsonSerializer.Deserialize<UserProfile>(File.ReadAllText(ProfilePath));
            return string.IsNullOrWhiteSpace(profile?.DisplayName) ? null : profile;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Profile][WARN] Could not read local profile: {ex.GetBaseException().Message}");
            return null;
        }
    }

    public static void Save(UserProfile profile)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temp = ProfilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(profile));
        File.Move(temp, ProfilePath, overwrite: true);
    }

    public static string AuthTokenPath => Path.Combine(DirectoryPath, "firebase-auth.dpapi");

    public static string ProfileDocumentId(string displayName)
    {
        var normalized = string.Join(' ', displayName.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant()[..32];
    }
}

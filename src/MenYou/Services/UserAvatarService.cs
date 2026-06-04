using System.Runtime.Versioning;
using System.Security.Principal;
using Avalonia.Media.Imaging;
using Microsoft.Win32;

namespace MenYou.Services;

[SupportedOSPlatform("windows")]
public sealed class UserAvatarService : IUserAvatarService
{
    public AvatarResult LoadAvatar()
    {
        foreach (var (path, isDefault) in CandidatePaths())
        {
            try
            {
                if (File.Exists(path))
                    return new AvatarResult(new Bitmap(path), isDefault);
            }
            catch
            {
                // Skip unreadable / unsupported files and try the next candidate.
            }
        }
        return new AvatarResult(null, false);
    }

    private static IEnumerable<(string Path, bool IsDefault)> CandidatePaths()
    {
        var user = Environment.UserName;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        // 1. Authoritative source on Win 10/11: the AccountPicture registry
        //    points at the current user's chosen picture variants under
        //    %PUBLIC%\AccountPictures\<SID>\{guid}-Image<size>.jpg. Try the
        //    largest variants first so the orb stays crisp on hi-DPI.
        foreach (var path in RegistryAccountPicturePaths())
            yield return (path, false);

        // 2. Custom account pictures written into the user's roaming
        //    AccountPictures folder (Microsoft account profile pictures
        //    sometimes land here too, encoded as PNG variants).
        var accountPicturesDir = Path.Combine(appData, "Microsoft", "Windows", "AccountPictures");
        if (Directory.Exists(accountPicturesDir))
        {
            var pngs = Directory.EnumerateFiles(accountPicturesDir, "*.png")
                .Select(p => (Path: p, Size: SafeLength(p)))
                .Where(t => t.Size > 0)
                .OrderByDescending(t => t.Size);
            foreach (var (path, _) in pngs)
                yield return (path, false);
        }

        // 3. Per-user picture saved alongside the system default silhouettes
        //    (e.g. %ProgramData%\Microsoft\User Account Pictures\<user>.png).
        var perUser = Path.Combine(programData, "Microsoft", "User Account Pictures", user + ".png");
        yield return (perUser, false);

        // 4. Default Windows silhouette — present on every Windows install.
        //    Marked IsDefault=true so the view can opt out of the dark-mode
        //    invert/brighten pipeline for real user photos.
        var defaults = new[] { "user-192.png", "user.png", "user-48.png", "user-40.png", "user-32.png" };
        foreach (var name in defaults)
            yield return (Path.Combine(programData, "Microsoft", "User Account Pictures", name), true);
    }

    private static IEnumerable<string> RegistryAccountPicturePaths()
    {
        string? sid;
        try
        {
            sid = WindowsIdentity.GetCurrent().User?.Value;
        }
        catch
        {
            yield break;
        }
        if (string.IsNullOrEmpty(sid)) yield break;

        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\{sid}");
        if (key is null) yield break;

        // The key holds ImageNNN values for many sizes (32, 40, 48, 64, 96,
        // 192, 208, 240, 424, 448, 1080). Walk them largest-first so the
        // first existing file is the sharpest available.
        var sizes = new[] { 1080, 448, 424, 240, 208, 192, 96, 64, 48, 40, 32 };
        foreach (var size in sizes)
        {
            if (key.GetValue($"Image{size}") is string path && !string.IsNullOrWhiteSpace(path))
                yield return path;
        }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }
}

using System.Runtime.Versioning;
using Microsoft.Win32;

namespace MenYou.Platform.Windows;

/// Removes any "Pin to MenYou" entries that previous MenYou builds wrote to
/// the per-user shell registry. Called on every startup so machines that
/// installed the earlier version self-heal back to a clean state. All
/// operations target HKCU so no admin rights are needed.
[SupportedOSPlatform("windows")]
internal static class ContextMenuRegistration
{
    private const string MenuKey = "PinToMenYou";

    /// File-type ProgIDs the entry used to live under.
    private static readonly string[] ProgIds = { "lnkfile", "exefile" };

    public static void UnregisterCurrentUser()
    {
        foreach (var progId in ProgIds)
        {
            try
            {
                using var shellRoot = Registry.CurrentUser.OpenSubKey(
                    $@"Software\Classes\{progId}\shell", writable: true);
                shellRoot?.DeleteSubKeyTree(MenuKey, throwOnMissingSubKey: false);
            }
            catch
            {
                // Best-effort cleanup — if the key doesn't exist or we can't
                // open it, there's nothing to do.
            }
        }
    }
}

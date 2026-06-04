using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace MenYou.Platform.Windows;

[SupportedOSPlatform("windows")]
internal static class ShellLinkReader
{
    public sealed record ShellLinkInfo(
        string? TargetPath,
        string? Arguments,
        string? WorkingDirectory,
        string? Description,
        string? IconPath,
        int IconIndex);

    public static ShellLinkInfo? Read(string lnkPath)
    {
        if (!File.Exists(lnkPath)) return null;

        NativeMethods.IShellLinkW? link = null;
        NativeMethods.IPersistFile? persist = null;
        try
        {
            link = (NativeMethods.IShellLinkW)new NativeMethods.ShellLinkCoClass();
            persist = (NativeMethods.IPersistFile)link;
            persist.Load(lnkPath, 0);

            var sbPath = new StringBuilder(1024);
            link.GetPath(sbPath, sbPath.Capacity, IntPtr.Zero, 0);

            var sbArgs = new StringBuilder(1024);
            link.GetArguments(sbArgs, sbArgs.Capacity);

            var sbDir = new StringBuilder(1024);
            link.GetWorkingDirectory(sbDir, sbDir.Capacity);

            var sbDesc = new StringBuilder(1024);
            link.GetDescription(sbDesc, sbDesc.Capacity);

            var sbIcon = new StringBuilder(1024);
            link.GetIconLocation(sbIcon, sbIcon.Capacity, out var iconIndex);

            return new ShellLinkInfo(
                Nullify(sbPath.ToString()),
                Nullify(sbArgs.ToString()),
                Nullify(sbDir.ToString()),
                Nullify(sbDesc.ToString()),
                Nullify(sbIcon.ToString()),
                iconIndex);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (persist != null) Marshal.FinalReleaseComObject(persist);
            if (link != null && link != persist) Marshal.FinalReleaseComObject(link);
        }
    }

    private static string? Nullify(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

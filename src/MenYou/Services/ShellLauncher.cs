using System.Diagnostics;
using MenYou.Models;

namespace MenYou.Services;

public sealed class ShellLauncher : IShellLauncher
{
    private readonly IRecentItemsService _recent;

    public event Action? Launched;

    public ShellLauncher(IRecentItemsService recent) => _recent = recent;

    public void Launch(AppEntry entry)
    {
        // UWP / packaged apps launch via explorer.exe shell:AppsFolder\<AUMID>.
        // ShellExecute on the bare AUMID doesn't work for unpackaged
        // callers; routing through Explorer's namespace handler does.
        if (entry.Kind == AppEntryKind.PackagedApp && !string.IsNullOrEmpty(entry.Aumid))
        {
            try
            {
                System.Diagnostics.Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"shell:AppsFolder\\{entry.Aumid}",
                    UseShellExecute = true,
                });
                _recent.RecordLaunch(entry.Id);
                Launched?.Invoke();
            }
            catch { }
            return;
        }

        // Prefer .lnk through the shell — it preserves working directory and
        // any "Run as admin" flags the user already set on the shortcut.
        var target = entry.SourceLnkPath ?? entry.TargetPath;
        if (string.IsNullOrWhiteSpace(target)) return;
        Launch(target, entry.SourceLnkPath is not null ? null : entry.Arguments,
            entry.SourceLnkPath is not null ? null : entry.WorkingDirectory);
        _recent.RecordLaunch(entry.Id);
        // Launch(string,...) already fired Launched once. Don't double-fire
        // — the host hides the menu on the first invocation and a second
        // call is just noise on the event channel.
    }

    public void Launch(string path, string? args = null, string? workingDirectory = null)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                Arguments = args ?? string.Empty,
                WorkingDirectory = workingDirectory ?? string.Empty,
                UseShellExecute = true
            };
            Process.Start(psi);
            Launched?.Invoke();
        }
        catch
        {
            // best-effort launch — no event on failure so the menu stays
            // visible if the launch actually didn't happen.
        }
    }

    public void LaunchAsAdmin(AppEntry entry)
    {
        // UWP / packaged apps can't be elevated via the standard runas
        // verb — the AppContainer model doesn't honor it. Fall back to
        // a normal launch in that case; the user gets the regular tile
        // instead of an error dialog.
        if (entry.Kind == AppEntryKind.PackagedApp)
        {
            Launch(entry);
            return;
        }

        var target = entry.SourceLnkPath ?? entry.TargetPath;
        if (string.IsNullOrWhiteSpace(target)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                Arguments = entry.SourceLnkPath is not null ? string.Empty : (entry.Arguments ?? string.Empty),
                WorkingDirectory = entry.SourceLnkPath is not null ? string.Empty : (entry.WorkingDirectory ?? string.Empty),
                UseShellExecute = true,
                Verb = "runas",
            });
            _recent.RecordLaunch(entry.Id);
            Launched?.Invoke();
        }
        catch
        {
            // User declined UAC or the target can't be elevated. Stay
            // silent — the menu stays open so they can pick again.
        }
    }

    public void OpenSpecialFolder(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        if (string.IsNullOrEmpty(path)) return;
        Launch(path);
    }

    public void NotifyLaunched() => Launched?.Invoke();
}

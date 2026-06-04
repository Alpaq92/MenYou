using MenYou.Models;
using MenYou.Platform.Windows;

namespace MenYou.Services;

public sealed class PinService : IPinService
{
    private readonly ISettingsService _settings;
    private bool _lastCanModify;
    public event Action? Changed;

    public PinService(ISettingsService settings)
    {
        _settings = settings;
        _lastCanModify = CanModify;
        // When MirrorWindowsStart is toggled in Settings the bound
        // AppItemViewModels need to refresh CanModifyPin so their
        // Pin/Unpin context items appear or disappear. We piggyback on
        // the existing Changed event by firing it when CanModify flips.
        settings.Changed += () =>
        {
            if (_lastCanModify == CanModify) return;
            _lastCanModify = CanModify;
            Changed?.Invoke();
        };
    }

    public bool CanModify => !_settings.Current.MirrorWindowsStart;

    /// Hybrid pinning: the Pin/Unpin verb is always available. The user
    /// can pin any app and unpin any pinned one in both manual and mirror
    /// modes — manual pins are recorded in UserSettings.ManualPins so they
    /// survive the mirror's per-sync ReplaceAll instead of being wiped.
    public bool CanUnpin(string appId) => true;

    public async Task EnsureSeededAsync(IAppDiscoveryService discovery)
    {
        if (_settings.Current.PinnedSeeded) return;

        var taskbarDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");

        if (!System.IO.Directory.Exists(taskbarDir))
        {
            _settings.Current.PinnedSeeded = true;
            _settings.Save();
            return;
        }

        var apps = await discovery.GetAllAppsAsync();
        var pinned = false;

        foreach (var lnk in System.IO.Directory.EnumerateFiles(taskbarDir, "*.lnk"))
        {
            var info = ShellLinkReader.Read(lnk);
            // .lnk files for shell namespace items (File Explorer, This PC,
            // etc.) have a null TargetPath because they point to a PIDL
            // rather than a filesystem path. Match by display name only in
            // that case.
            var lnkName = System.IO.Path.GetFileNameWithoutExtension(lnk);
            var match = apps.FirstOrDefault(a =>
                   (!string.IsNullOrEmpty(info?.TargetPath) &&
                       string.Equals(a.TargetPath, info!.TargetPath, StringComparison.OrdinalIgnoreCase))
                || string.Equals(a.SourceLnkPath, lnk,              StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.DisplayName,   lnkName,          StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.AlternativeName, lnkName,        StringComparison.OrdinalIgnoreCase));
            if (match is null) continue;
            if (IsPinned(match.Id)) continue;

            var maxOrder = _settings.Current.Pinned.Count > 0
                ? _settings.Current.Pinned.Max(p => p.Order)
                : 0;
            _settings.Current.Pinned.Add(new PinnedItem(match.Id, maxOrder + 1));
            pinned = true;
        }

        _settings.Current.PinnedSeeded = true;
        _settings.Save();
        if (pinned) Changed?.Invoke();
    }

    public bool IsPinned(string appId) =>
        _settings.Current.Pinned.Any(p =>
            string.Equals(p.AppId, appId, StringComparison.OrdinalIgnoreCase));

    public void Pin(string appId)
    {
        var changed = false;

        // Record the manual intent so it persists across mirror syncs.
        if (!_settings.Current.ManualPins.Contains(appId, StringComparer.OrdinalIgnoreCase))
        {
            _settings.Current.ManualPins.Add(appId);
            changed = true;
        }

        // Pinning overrides a prior mirror-unpin (exclusion) of the same id.
        if (_settings.Current.MirrorExclusions.RemoveAll(id =>
                string.Equals(id, appId, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            changed = true;
        }

        // Show it now if it isn't already in the visible list.
        if (!IsPinned(appId))
        {
            var maxOrder = _settings.Current.Pinned.Count > 0
                ? _settings.Current.Pinned.Max(p => p.Order)
                : 0;
            _settings.Current.Pinned.Add(new PinnedItem(appId, maxOrder + 1));
            changed = true;
        }

        if (!changed) return;
        _settings.Save();
        Changed?.Invoke();
    }

    public void Unpin(string appId)
    {
        var changed = false;

        // Forget the manual-pin record so ReplaceAll won't re-append it.
        if (_settings.Current.ManualPins.RemoveAll(id =>
                string.Equals(id, appId, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            changed = true;
        }

        // In mirror mode also exclude it, so a Windows-mirrored copy stays
        // hidden across syncs. The Windows Start menu is untouched, and the
        // exclusion auto-prunes once Windows no longer pins the app (it's a
        // no-op for manual-only pins, which the mirror never lists anyway).
        if (_settings.Current.MirrorWindowsStart
            && !_settings.Current.MirrorExclusions
                .Contains(appId, StringComparer.OrdinalIgnoreCase))
        {
            _settings.Current.MirrorExclusions.Add(appId);
            changed = true;
        }

        if (_settings.Current.Pinned.RemoveAll(p =>
                string.Equals(p.AppId, appId, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            changed = true;
        }

        if (!changed) return;
        _settings.Save();
        Changed?.Invoke();
    }

    public void Toggle(string appId)
    {
        // Symmetric in both modes now: pin if it isn't pinned, unpin if it
        // is. Pin() records the manual intent so it survives mirror syncs.
        if (IsPinned(appId)) Unpin(appId);
        else Pin(appId);
    }

    public void ReplaceAll(IReadOnlyList<string> appIdsInOrder)
    {
        // Hybrid merge: the mirrored Windows pins first (in their order),
        // then the user's manual pins that aren't already present. The
        // dedup guarantees an app pinned in BOTH Windows and MenYou shows
        // exactly once, and the manual pins survive every mirror sync
        // (without this they'd be wiped by the wholesale replace below).
        var merged = new List<string>(appIdsInOrder);
        foreach (var id in _settings.Current.ManualPins)
        {
            if (!merged.Contains(id, StringComparer.OrdinalIgnoreCase))
                merged.Add(id);
        }

        // Skip the write entirely if nothing changed — avoids a settings.json
        // touch and a spurious Changed event firing every time start2.bin
        // gets rewritten for a non-pin reason (theme tweak, housekeeping).
        var current = _settings.Current.Pinned
            .OrderBy(p => p.Order)
            .Select(p => p.AppId)
            .ToList();
        if (current.SequenceEqual(merged, StringComparer.OrdinalIgnoreCase)) return;

        _settings.Current.Pinned.Clear();
        var order = 1;
        foreach (var id in merged)
            _settings.Current.Pinned.Add(new PinnedItem(id, order++));
        _settings.Save();
        Changed?.Invoke();
    }
}

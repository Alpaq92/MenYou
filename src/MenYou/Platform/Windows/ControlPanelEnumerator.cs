using System.Runtime.Versioning;

namespace MenYou.Platform.Windows;

/// Enumerates Windows' "All Tasks" namespace (the so-called GodMode folder
/// at <c>::{ED7BA470-8E54-465E-825C-99712043E01C}</c>) via the Shell.Application
/// COM object. Returns ~200 task-level Control Panel entries with their
/// localized display names — same source Open-Shell uses to populate its
/// settings-search results.
///
/// Launching: each entry's <see cref="Item.Path"/> is a Shell-IDL string of
/// the form <c>::{Control Panel CLSID}\0\::{All Tasks CLSID}\{Task GUID}</c>
/// which Explorer dispatches to the right Control Panel applet when passed
/// as a command-line argument.
[SupportedOSPlatform("windows")]
internal static class ControlPanelEnumerator
{
    public sealed record Item(string Name, string ShellPath);

    private const string AllTasksNamespace = "shell:::{ED7BA470-8E54-465E-825C-99712043E01C}";

    private static IReadOnlyList<Item>? _cached;
    private static readonly object _gate = new();

    public static IReadOnlyList<Item> Enumerate()
    {
        if (_cached is not null) return _cached;
        lock (_gate)
        {
            if (_cached is not null) return _cached;
            _cached = LoadFresh();
            return _cached;
        }
    }

    /// Launches a Control Panel "All Tasks" entry by re-navigating the
    /// namespace and invoking the item's default verb. Passing the item's
    /// shell-IDL Path as an explorer.exe argument doesn't actually
    /// dispatch the task — Explorer falls back to opening Documents.
    /// InvokeVerb is what the shell address bar uses internally.
    public static bool LaunchTask(string itemName)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return false;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic? folder = shell.NameSpace(AllTasksNamespace);
            if (folder is null) return false;
            dynamic items = folder.Items();
            int count = items.Count;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic item = items.Item(i);
                    string name = item.Name ?? string.Empty;
                    if (!string.Equals(name, itemName, StringComparison.Ordinal)) continue;
                    item.InvokeVerb("open");
                    return true;
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    private static List<Item> LoadFresh()
    {
        var list = new List<Item>();
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return list;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic? folder = shell.NameSpace(AllTasksNamespace);
            if (folder is null) return list;

            dynamic items = folder.Items();
            int count = items.Count;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic item = items.Item(i);
                    string name = item.Name ?? string.Empty;
                    string path = item.Path ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
                    list.Add(new Item(name, path));
                }
                catch
                {
                    // Some items may throw on property access (security or shell glitch);
                    // skip them and keep enumerating.
                }
            }
        }
        catch
        {
            // Shell.Application unavailable (server SKU? sandboxed?) — just leave
            // the list empty; SearchService falls back to its built-in commands.
        }
        return list;
    }
}

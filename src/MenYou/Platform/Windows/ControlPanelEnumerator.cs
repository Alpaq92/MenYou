using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MenYou.Platform.Windows;

/// Enumerates Windows' "All Tasks" namespace (the so-called GodMode folder
/// at <c>::{ED7BA470-8E54-465E-825C-99712043E01C}</c>) via the Shell.Application
/// COM object. Returns ~200 task-level Control Panel entries with their
/// localized display names — same source Open-Shell uses to populate its
/// settings-search results.
///
/// Launching: each entry's <see cref="Item.ShellPath"/> is a Shell-IDL string
/// of the form <c>::{Control Panel CLSID}\0\::{All Tasks CLSID}\{Task GUID}</c>
/// which Explorer dispatches to the right Control Panel applet when passed
/// as a command-line argument.
///
/// The Shell.Application surface is reached through the explicit
/// <see cref="IShellDispatch"/> / <see cref="IShellFolderView"/> /
/// <see cref="IShellFolderItems"/> / <see cref="IShellFolderItem"/> COM
/// interfaces below rather than C# <c>dynamic</c>. <c>dynamic</c> over a COM
/// object dispatches through IDispatch (not managed reflection, so it would
/// actually run under trimming), but it still pulls the C# runtime binder +
/// DLR (Microsoft.CSharp / System.Linq.Expressions) into the payload and
/// raises IL2026 (RequiresUnreferencedCode) warnings that block a clean
/// PublishTrimmed. These late-bound IDispatch interfaces call the same shell
/// members by name (GetIDsOfNames + Invoke) with no DLR dependency.
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
            var items = OpenAllTasksItems();
            if (items is null) return false;
            var count = items.Count;
            for (var i = 0; i < count; i++)
            {
                try
                {
                    if (items.Item(i) is not IShellFolderItem item) continue;
                    var name = item.Name ?? string.Empty;
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
            var items = OpenAllTasksItems();
            if (items is null) return list;

            var count = items.Count;
            for (var i = 0; i < count; i++)
            {
                try
                {
                    if (items.Item(i) is not IShellFolderItem item) continue;
                    var name = item.Name ?? string.Empty;
                    var path = item.Path ?? string.Empty;
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

    /// Creates Shell.Application and returns the FolderItems collection for the
    /// "All Tasks" namespace, or null if any hop fails / QIs to the wrong type.
    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "Activator.CreateInstance on the Shell.Application COM ProgID does " +
            "CoCreateInstance on a native object; no managed members are reflected, so trimming " +
            "is unaffected.")]
    private static IShellFolderItems? OpenAllTasksItems()
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null) return null;
        var shellObj = Activator.CreateInstance(shellType);
        if (shellObj is not IShellDispatch shell) return null;
        if (shell.NameSpace(AllTasksNamespace) is not IShellFolderView folder) return null;
        return folder.Items() as IShellFolderItems;
    }
}

// --- Shell Automation IDispatch interfaces (shldisp.h / Shell32 TLB) --------
// InterfaceIsIDispatch: calls resolve by member NAME via GetIDsOfNames + Invoke,
// so only the members MenYou uses need declaring and vtable order is irrelevant
// (unlike a dual-interface layout, which would need every preceding slot stubbed
// in exact order). These GUIDs and member names have been stable since the shell
// automation model shipped. Each return is left as `object` (IDispatch) and
// cast with `as` in the caller so a QueryInterface miss degrades gracefully.

[ComImport, Guid("D8F015C0-C278-11CE-A49E-444553540000"),
 InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IShellDispatch
{
    [return: MarshalAs(UnmanagedType.IDispatch)]
    object? NameSpace([MarshalAs(UnmanagedType.Struct)] object vDir);
}

/// The Shell Automation "Folder" dispinterface (IID BBCBDE60-…). Named
/// IShellFolderView here to avoid colliding with the shell's unmanaged
/// <c>Folder</c>/<c>IShellFolder</c> names.
[ComImport, Guid("BBCBDE60-C3FF-11CE-8350-444553540000"),
 InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IShellFolderView
{
    [return: MarshalAs(UnmanagedType.IDispatch)]
    object? Items();
}

/// The Shell Automation "FolderItems" dispinterface (IID 744129E0-…).
[ComImport, Guid("744129E0-CBE5-11CE-8350-444553540000"),
 InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IShellFolderItems
{
    int Count { get; }

    [return: MarshalAs(UnmanagedType.IDispatch)]
    object? Item([MarshalAs(UnmanagedType.Struct)] object index);
}

/// The Shell Automation "FolderItem" dispinterface (IID FAC32C80-…).
[ComImport, Guid("FAC32C80-CBE4-11CE-8350-444553540000"),
 InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IShellFolderItem
{
    string? Name { [return: MarshalAs(UnmanagedType.BStr)] get; }

    string? Path { [return: MarshalAs(UnmanagedType.BStr)] get; }

    void InvokeVerb([MarshalAs(UnmanagedType.Struct)] object vVerb);
}

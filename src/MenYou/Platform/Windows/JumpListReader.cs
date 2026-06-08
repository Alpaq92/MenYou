using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MenYou.Platform.Windows;

/// Returns per-app recent destinations from the Windows JumpList. Mirrors
/// the Open-Shell approach (Src/StartMenu/StartMenuDLL/JumpLists.cpp).
///
/// Two pieces of undocumented shell COM:
///
/// 1. <c>CLSID_AutomaticDestinationList = {f0ae1542-…-b09144ba}</c>
///    — the class that actually exposes the recent/frequent/pinned
///    JumpList for an arbitrary AppUserModelID. Distinct from the SDK's
///    <c>CLSID_DestinationList</c> (`IApplicationDocumentLists`), which
///    only covers the custom-tasks half and isn't registered on every
///    Win 11 build. Initialize with the AUMID; the object internally
///    resolves the matching <c>.automaticDestinations-ms</c> file, so
///    we never compute the file-name CRC ourselves.
///    Two IID variants exist: pre-Win10 build 10547, and 10547+ where
///    <c>GetList</c> grew a flags parameter (we pass 1).
///
/// 2. <c>IApplicationResolver::GetAppIDForShortcut</c> — derives the
///    *implicit* AUMID for a Win32 shortcut without explicit AUMID
///    property. Explorer uses the same path internally when writing
///    recent docs, so we need it to match what Windows hashed.
[SupportedOSPlatform("windows")]
internal static class JumpListReader
{
    public sealed record Destination(string Path, string DisplayName);

    /// App-published JumpList Task (e.g. Firefox's "Open new tab",
    /// "Open new private window"). Title may need
    /// <see cref="SHLoadIndirectString"/> resolution if it's an
    /// indirect-string ref. Arguments are the CLI args the shell should
    /// pass to <see cref="Target"/>.
    public sealed record JumpTask(string Title, string Target, string Arguments, bool IsSeparator);

    /// Resolves the implicit AUMID / key the shell uses to store an app's
    /// JumpList (Recent + Tasks): an explicit AUMID wins, else the id derived
    /// from a <c>.lnk</c> via IApplicationResolver, else the raw target path.
    /// Shared by the search results and every app context menu so they key off
    /// the same destination list. Returns "" when nothing is resolvable.
    public static string ResolveKey(string? aumid, string? lnkPath, string? targetPath)
    {
        if (!string.IsNullOrEmpty(aumid)) return aumid!;
        if (!string.IsNullOrEmpty(lnkPath) &&
            lnkPath!.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var resolved = GetAppIdForShortcut(lnkPath);
            if (!string.IsNullOrEmpty(resolved)) return resolved!;
            // Per this method's contract, the final fallback is the raw target
            // path (a stable JumpList key) — not the .lnk path.
            return !string.IsNullOrEmpty(targetPath) ? targetPath : lnkPath;
        }
        return targetPath ?? string.Empty;
    }

    /// Listed in Open-Shell's GetJumplist switch: 0=Pinned, 1=Recent, 2=Frequent.
    public static IReadOnlyList<Destination> ReadRecent(string aumid, int max = 8)
    {
        if (string.IsNullOrWhiteSpace(aumid) || max <= 0) return Array.Empty<Destination>();
        try
        {
            return ReadViaAutomaticDestinationList(aumid, listType: 1, max);
        }
        catch
        {
            return Array.Empty<Destination>();
        }
    }

    /// Returns the app-published Tasks (custom destinations) for the
    /// given AUMID — Firefox's "Open new tab" / "New private window"
    /// strip and similar. Uses CLSID_DestinationList with the private
    /// reader IDestinationList vtable (same as Open-Shell). Returns
    /// empty when the app publishes none, or when the COM class isn't
    /// registered.
    public static IReadOnlyList<JumpTask> ReadTasks(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return Array.Empty<JumpTask>();
        try
        {
            return ReadTasksInternal(aumid);
        }
        catch
        {
            return Array.Empty<JumpTask>();
        }
    }

    private static IReadOnlyList<JumpTask> ReadTasksInternal(string aumid)
    {
        var type = Type.GetTypeFromCLSID(CLSID_DestinationList);
        if (type is null) return Array.Empty<JumpTask>();
        var unk = Activator.CreateInstance(type);
        if (unk is null) return Array.Empty<JumpTask>();
        try
        {
            // QI for one of the version-specific reader vtables. Newer
            // 10b shape first; fall back through the chain.
            IDestinationList10b? list10b = null;
            IDestinationList10a? list10a = null;
            IDestinationList? list = null;
            try { list10b = (IDestinationList10b)unk; } catch (InvalidCastException) { }
            if (list10b is null)
            {
                try { list10a = (IDestinationList10a)unk; } catch (InvalidCastException) { }
                if (list10a is null)
                {
                    try { list = (IDestinationList)unk; } catch (InvalidCastException) { }
                }
            }

            int hr = list10b is not null ? list10b.SetApplicationID(aumid)
                  : list10a is not null ? list10a.SetApplicationID(aumid)
                  : list      is not null ? list.SetApplicationID(aumid)
                  : -1;
            if (hr != 0) return Array.Empty<JumpTask>();

            uint count = 0;
            hr = list10b is not null ? list10b.GetCategoryCount(out count)
              : list10a is not null ? list10a.GetCategoryCount(out count)
              : list!.GetCategoryCount(out count);
            if (hr != 0) return Array.Empty<JumpTask>();

            // Walk categories looking for type==2 (Tasks).
            int taskIndex = -1;
            for (uint i = 0; i < count; i++)
            {
                APPDESTCATEGORY cat = default;
                hr = list10b is not null ? list10b.GetCategory(i, 1, ref cat)
                  : list10a is not null ? list10a.GetCategory(i, 1, ref cat)
                  : list!.GetCategory(i, 1, ref cat);
                if (hr != 0) continue;
                if (cat.type == 2) { taskIndex = (int)i; break; }
            }
            if (taskIndex < 0) return Array.Empty<JumpTask>();

            var iid = typeof(IObjectArray).GUID;
            object? collObj;
            hr = list10b is not null
                ? list10b.EnumerateCategoryDestinations((uint)taskIndex, ref iid, out collObj)
                : list10a is not null
                    ? list10a.EnumerateCategoryDestinations((uint)taskIndex, ref iid, out collObj)
                    : list!.EnumerateCategoryDestinations((uint)taskIndex, ref iid, out collObj);
            if (hr != 0 || collObj is not IObjectArray array) return Array.Empty<JumpTask>();

            try
            {
                array.GetCount(out var nItems);
                var results = new List<JumpTask>((int)nItems);
                var iidLink = typeof(IShellLinkW).GUID;
                for (uint k = 0; k < nItems; k++)
                {
                    array.GetAt(k, ref iidLink, out var linkObj);
                    if (linkObj is not IShellLinkW link) continue;
                    try
                    {
                        // Each task is a shell-link with PKEY_Title (often a
                        // SHLoadIndirectString reference like
                        // "@firefox.exe,-1010") plus PKEY_Link_Arguments.
                        string title = "", target = "", arguments = "";
                        bool sep = false;
                        var pkeyTitleId = PKEY_Title;
                        var pkeyArgs = PKEY_Link_Arguments;
                        var pkeySep = PKEY_AppUserModel_IsDestListSeparator;
                        if (linkObj is IPropertyStore pStore)
                        {
                            if (pStore.GetValue(ref pkeySep, out var pvSep) == 0)
                            {
                                if (pvSep.vt == VT_BOOL && pvSep.value != IntPtr.Zero) sep = true;
                                PropVariantClear(ref pvSep);
                            }
                            if (!sep && pStore.GetValue(ref pkeyTitleId, out var pvTitle) == 0)
                            {
                                if (pvTitle.vt == VT_LPWSTR || pvTitle.vt == VT_BSTR)
                                    title = Marshal.PtrToStringUni(pvTitle.value) ?? "";
                                PropVariantClear(ref pvTitle);
                            }
                            if (!sep && pStore.GetValue(ref pkeyArgs, out var pvArgs) == 0)
                            {
                                if (pvArgs.vt == VT_LPWSTR || pvArgs.vt == VT_BSTR)
                                    arguments = Marshal.PtrToStringUni(pvArgs.value) ?? "";
                                PropVariantClear(ref pvArgs);
                            }
                        }

                        if (sep)
                        {
                            results.Add(new JumpTask("", "", "", true));
                            continue;
                        }

                        // SHLoadIndirectString resolves "@module,-id" refs to
                        // their localized strings. Plain titles pass through.
                        if (!string.IsNullOrEmpty(title) && title.StartsWith("@"))
                        {
                            var buf = new System.Text.StringBuilder(512);
                            if (SHLoadIndirectString(title, buf, buf.Capacity, IntPtr.Zero) == 0)
                                title = buf.ToString();
                        }

                        // IShellLink::GetPath / GetArguments fill in the target.
                        var path = new System.Text.StringBuilder(260);
                        var data = default(WIN32_FIND_DATAW);
                        link.GetPath(path, path.Capacity, ref data, 0);
                        target = path.ToString();
                        if (string.IsNullOrEmpty(arguments))
                        {
                            var args = new System.Text.StringBuilder(1024);
                            link.GetArguments(args, args.Capacity);
                            arguments = args.ToString();
                        }

                        if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(target)) continue;
                        results.Add(new JumpTask(title, target, arguments, false));
                    }
                    finally { Marshal.ReleaseComObject(link); }
                }
                return results;
            }
            finally { Marshal.ReleaseComObject(array); }
        }
        finally
        {
            Marshal.ReleaseComObject(unk);
        }
    }

    /// Looks up the AUMID Windows associated with a .lnk shortcut.
    /// First the explicit PropertyStore property; on miss, the
    /// undocumented IApplicationResolver — same fallback Open-Shell uses
    /// (see ItemManager.cpp:1939-2013).
    public static string? GetAppIdForShortcut(string lnkPath)
    {
        if (string.IsNullOrWhiteSpace(lnkPath) || !System.IO.File.Exists(lnkPath))
            return null;
        var explicitId = TryGetAumidFromPropertyStore(lnkPath);
        if (!string.IsNullOrEmpty(explicitId)) return explicitId;
        return TryGetAumidFromAppResolver(lnkPath);
    }

    // ── IAutomaticDestinationList -----------------------------------------

    private static IReadOnlyList<Destination> ReadViaAutomaticDestinationList(string aumid, int listType, int max)
    {
        var type = Type.GetTypeFromCLSID(CLSID_AutomaticDestinationList);
        if (type is null) return Array.Empty<Destination>();
        var unk = Activator.CreateInstance(type);
        if (unk is null) return Array.Empty<Destination>();
        try
        {
            // Try the newer (Win10 10547+) interface first, where GetList takes
            // an extra flags arg. Fall back to the older shape.
            IAutomaticDestinationList10b? newer = null;
            IAutomaticDestinationList? older = null;
            try { newer = (IAutomaticDestinationList10b)unk; }
            catch (InvalidCastException) { }
            if (newer is null)
            {
                try { older = (IAutomaticDestinationList)unk; }
                catch (InvalidCastException) { }
            }
            if (newer is null && older is null) return Array.Empty<Destination>();

            int hr = newer is not null
                ? newer.Initialize(aumid, null, null)
                : older!.Initialize(aumid, null, null);
            if (hr != 0) return Array.Empty<Destination>();

            var iid = typeof(IObjectCollection).GUID;
            object? collObj;
            // Over-fetch, then cap to `max` filesystem results in the loop below.
            // The automatic-destination list can carry a non-filesystem entry (a
            // category / pinned slot) that we skip when reading, yet it still
            // counts against GetList's maxCount — so asking for exactly `max`
            // returns max-1 *files* (e.g. cap 5 showed 4). A small buffer plus
            // the loop cap yields exactly `max` files when that many exist.
            uint fetch = (uint)(max + 16);
            if (newer is not null)
            {
                hr = newer.GetList(listType, fetch, 1u, ref iid, out collObj);
            }
            else
            {
                hr = older!.GetList(listType, fetch, ref iid, out collObj);
            }
            if (hr != 0 || collObj is not IObjectArray array) return Array.Empty<Destination>();

            try
            {
                array.GetCount(out var count);
                var results = new List<Destination>((int)count);
                var siIid = typeof(IShellItem).GUID;
                for (uint i = 0; i < count && results.Count < max; i++)
                {
                    array.GetAt(i, ref siIid, out var itemObj);
                    if (itemObj is not IShellItem item) continue;
                    try
                    {
                        if (item.GetDisplayName(SIGDN.FILESYSPATH, out var p) != 0 || p is null) continue;
                        // Use the raw filename (with extension). SIGDN_NORMALDISPLAY
                        // honours the "hide extensions for known file types"
                        // shell setting and would strip .doc/.txt/etc., which
                        // we want visible.
                        results.Add(new Destination(p, System.IO.Path.GetFileName(p)));
                    }
                    finally { Marshal.ReleaseComObject(item); }
                }
                return results;
            }
            finally { Marshal.ReleaseComObject(array); }
        }
        finally
        {
            Marshal.ReleaseComObject(unk);
        }
    }

    // ── AUMID resolution: PKEY_AppUserModel_ID then IApplicationResolver --

    private static string? TryGetAumidFromPropertyStore(string lnkPath)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreFromParsingName(lnkPath, IntPtr.Zero, 0, ref iid, out var store) != 0
                || store is null) return null;
            try
            {
                var key = PKEY_AppUserModel_ID;
                if (store.GetValue(ref key, out var pv) != 0) return null;
                try
                {
                    if (pv.vt != VT_LPWSTR && pv.vt != VT_BSTR) return null;
                    return Marshal.PtrToStringUni(pv.value);
                }
                finally { PropVariantClear(ref pv); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }
        catch { return null; }
    }

    private static string? TryGetAumidFromAppResolver(string lnkPath)
    {
        // Build an IShellItem from the .lnk path, then ask
        // IApplicationResolver to compute its AUMID. Same path
        // ItemManager.cpp:2002-2012 uses.
        try
        {
            var siid = typeof(IShellItem).GUID;
            if (SHCreateItemFromParsingName(lnkPath, IntPtr.Zero, ref siid, out IShellItem? item) != 0
                || item is null)
                return null;
            try
            {
                var rType = Type.GetTypeFromCLSID(CLSID_ApplicationResolver);
                if (rType is null) return null;
                var unk = Activator.CreateInstance(rType);
                if (unk is null) return null;
                try
                {
                    if (unk is not IApplicationResolver resolver) return null;
                    if (resolver.GetAppIDForShortcut(item, out var aumidPtr) != 0 || aumidPtr == IntPtr.Zero)
                        return null;
                    try { return Marshal.PtrToStringUni(aumidPtr); }
                    finally { Marshal.FreeCoTaskMem(aumidPtr); }
                }
                finally { Marshal.ReleaseComObject(unk); }
            }
            finally { Marshal.ReleaseComObject(item); }
        }
        catch { return null; }
    }

    // ── COM declarations --------------------------------------------------

    // The undocumented "automatic destinations" provider. Open-Shell:
    // JumpLists.cpp:15.
    private static readonly Guid CLSID_AutomaticDestinationList =
        new("F0AE1542-F497-484B-A175-A20DB09144BA");

    // SDK-defined "destination list" coclass, used here for its private
    // IDestinationList reader vtable (Open-Shell does the same — JumpLists.cpp
    // GetCustomList).
    private static readonly Guid CLSID_DestinationList =
        new("77F10CF0-3DB5-4966-B520-B7C54FD35ED6");

    [StructLayout(LayoutKind.Sequential)]
    private struct APPDESTCATEGORY
    {
        public int type;       // 0=custom group, 1=standard (frequent/recent), 2=Tasks
        public IntPtr name;    // wchar_t* for custom; subType union for standard
        public int count;
        public int p0, p1, p2, p3, p4, p5, p6, p7, p8, p9;
    }

    [ComImport, Guid("03F1EED2-8676-430B-ABE1-765C1D8FE147"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDestinationList
    {
        [PreserveSig] int SetMinItems();
        [PreserveSig] int SetApplicationID([MarshalAs(UnmanagedType.LPWStr)] string aumid);
        [PreserveSig] int GetSlotCount();
        [PreserveSig] int GetCategoryCount(out uint count);
        [PreserveSig] int GetCategory(uint index, int flags, ref APPDESTCATEGORY cat);
        [PreserveSig] int DeleteCategory();
        [PreserveSig] int EnumerateCategoryDestinations(uint index, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int RemoveDestination();
        [PreserveSig] int ResolveDestination();
    }

    [ComImport, Guid("FEBD543D-1F7B-4B38-940B-5933BD2CB21B"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDestinationList10a
    {
        [PreserveSig] int SetMinItems();
        [PreserveSig] int SetApplicationID([MarshalAs(UnmanagedType.LPWStr)] string aumid);
        [PreserveSig] int GetSlotCount();
        [PreserveSig] int GetCategoryCount(out uint count);
        [PreserveSig] int GetCategory(uint index, int flags, ref APPDESTCATEGORY cat);
        [PreserveSig] int DeleteCategory();
        [PreserveSig] int EnumerateCategoryDestinations(uint index, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int RemoveDestination();
        [PreserveSig] int ResolveDestination();
    }

    [ComImport, Guid("507101CD-F6AD-46C8-8E20-EEB9E6BAC47F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDestinationList10b
    {
        [PreserveSig] int SetMinItems();
        [PreserveSig] int SetApplicationID([MarshalAs(UnmanagedType.LPWStr)] string aumid);
        [PreserveSig] int GetSlotCount();
        [PreserveSig] int GetCategoryCount(out uint count);
        [PreserveSig] int GetCategory(uint index, int flags, ref APPDESTCATEGORY cat);
        [PreserveSig] int DeleteCategory();
        [PreserveSig] int EnumerateCategoryDestinations(uint index, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int RemoveDestination();
        [PreserveSig] int ResolveDestination();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public long ftCreationTime;
        public long ftLastAccessTime;
        public long ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath(System.Text.StringBuilder pszFile, int cch,
            ref WIN32_FIND_DATAW pfd, uint fFlags);
        [PreserveSig] int GetIDList(out IntPtr ppidl);
        [PreserveSig] int SetIDList(IntPtr pidl);
        [PreserveSig] int GetDescription(System.Text.StringBuilder pszName, int cch);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        [PreserveSig] int GetWorkingDirectory(System.Text.StringBuilder pszDir, int cch);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        [PreserveSig] int GetArguments(System.Text.StringBuilder pszArgs, int cch);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        [PreserveSig] int GetHotkey(out ushort pwHotkey);
        [PreserveSig] int SetHotkey(ushort wHotkey);
        [PreserveSig] int GetShowCmd(out int piShowCmd);
        [PreserveSig] int SetShowCmd(int iShowCmd);
        [PreserveSig] int GetIconLocation(System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        [PreserveSig] int Resolve(IntPtr hwnd, uint fFlags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    private const ushort VT_BOOL = 11;

    private static PROPERTYKEY PKEY_Title = new()
    {
        fmtid = new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"),
        pid = 2
    };
    private static PROPERTYKEY PKEY_Link_Arguments = new()
    {
        fmtid = new Guid("436F2667-14E2-4FEB-B30A-146C53B5B674"),
        pid = 100
    };
    private static PROPERTYKEY PKEY_AppUserModel_IsDestListSeparator = new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 6
    };

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(
        [MarshalAs(UnmanagedType.LPWStr)] string pszSource,
        System.Text.StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);

    [ComImport, Guid("BC10DCE3-62F2-4BC6-AF37-DB46ED7873C4"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAutomaticDestinationList
    {
        [PreserveSig] int Initialize(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? lnkPath,
            [MarshalAs(UnmanagedType.LPWStr)] string? reserved);
        [PreserveSig] int HasList(out int pHasList);
        [PreserveSig] int GetList(int listType, uint maxCount,
            ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        // We don't need the rest of the vtable for our read-only use.
    }

    // Win10 build 10547+ — GetList grew a `flags` parameter. Open-Shell
    // passes 1 (JumpLists.cpp:151).
    [ComImport, Guid("E9C5EF8D-FD41-4F72-BA87-EB03BAD5817C"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAutomaticDestinationList10b
    {
        [PreserveSig] int Initialize(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? lnkPath,
            [MarshalAs(UnmanagedType.LPWStr)] string? reserved);
        [PreserveSig] int HasList(out int pHasList);
        [PreserveSig] int GetList(int listType, uint maxCount, uint flags,
            ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    [ComImport, Guid("5632B1A4-E38A-400A-928A-D4CD63230295"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectCollection
    {
        // We treat it as IObjectArray for read access.
    }

    [ComImport, Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        void GetCount(out uint pcObjects);
        void GetAt(uint uiIndex, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    private enum SIGDN : uint
    {
        NORMALDISPLAY = 0,
        FILESYSPATH = 0x80058000,
        DESKTOPABSOLUTEPARSING = 0x80028000,
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(SIGDN sigdnName,
            [MarshalAs(UnmanagedType.LPWStr)] out string? ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    // IApplicationResolver — undocumented. Open-Shell ItemManager.cpp:43-52.
    private static readonly Guid CLSID_ApplicationResolver =
        new("660B90C8-73A9-4B58-8CAE-355B7F55341B");

    [ComImport, Guid("DE25675A-72DE-44B4-9373-05170450C140"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationResolver
    {
        // Only GetAppIDForShortcut is needed. The earlier vtable slots
        // are stubbed so Marshal can lay out the right offsets.
        [PreserveSig] int Slot0();
        [PreserveSig] int Slot1();
        [PreserveSig] int GetAppIDForShortcut(IShellItem psi, out IntPtr ppszAppID);
        // (Other Win8+ slots not needed for our use.)
    }

    // PropertyStore + PROPVARIANT

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort r1;
        public ushort r2;
        public ushort r3;
        public IntPtr value;
        public IntPtr value2;
    }

    private const ushort VT_LPWSTR = 31;
    private const ushort VT_BSTR = 8;

    private static PROPERTYKEY PKEY_AppUserModel_ID = new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5
    };

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetPropertyStoreFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc, uint flags, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem? ppv);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);
}

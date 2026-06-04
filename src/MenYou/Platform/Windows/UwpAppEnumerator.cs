using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MenYou.Platform.Windows;

/// Enumerates every launchable app the Win 11 shell knows about (UWP and
/// AUMID-registered Win32 combined) by walking the <c>shell:AppsFolder</c>
/// namespace directly via Win32 COM. This is exactly what
/// <c>Get-StartApps</c> does internally, but without the powershell.exe
/// spawn or any cross-process encoding fragility — strings come back as
/// UTF-16 from the shell and stay UTF-16 through .NET.
///
/// The shell folder <c>shell:AppsFolder</c> is the "Applications"
/// virtual folder containing one IShellItem per launchable app. For each
/// item we ask for:
///   * the localized display name (resolved by the shell from the
///     package's .pri resources or the .lnk's LocalizedString block);
///   * the AUMID via the <c>System.AppUserModel.ID</c> property.
///
/// Runs on a dedicated STA thread — most shell APIs prefer (and a few
/// require) STA, and the .NET thread pool is MTA by default.
[SupportedOSPlatform("windows")]
internal static class UwpAppEnumerator
{
    public sealed record UwpApp(string Name, string Aumid);

    private static readonly Guid IID_IShellItem      = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static readonly Guid IID_IEnumShellItems = new("70629033-e363-4a28-a567-0db78006e6d7");
    private static readonly Guid BHID_EnumItems      = new("94f60519-2850-4924-aa5a-d15e84868039");

    /// System.AppUserModel.ID — the property the shell sets to the AUMID
    /// on every shell:AppsFolder item (both packaged and Win32). Same
    /// PROPERTYKEY shellprop.h documents.
    private static readonly PROPERTYKEY PKEY_AppUserModel_ID = new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid   = 5,
    };

    public static Task<IReadOnlyList<UwpApp>> EnumerateAsync(CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<UwpApp>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(Enumerate(ct)); }
            catch { tcs.SetResult(Array.Empty<UwpApp>()); }
        })
        {
            IsBackground = true,
            Name = "MenYou.UwpAppEnumerator",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static List<UwpApp> Enumerate(CancellationToken ct)
    {
        var result = new List<UwpApp>(128);

        var iidShellItem = IID_IShellItem;
        if (SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero,
                ref iidShellItem, out var rootObj) != 0 || rootObj is null)
        {
            return result;
        }

        var root = (IShellItem)rootObj;
        try
        {
            var bhid = BHID_EnumItems;
            var iidEnum = IID_IEnumShellItems;
            if (root.BindToHandler(IntPtr.Zero, ref bhid, ref iidEnum, out var enumObj) != 0
                || enumObj is null)
            {
                return result;
            }

            var enumerator = (IEnumShellItems)enumObj;
            try
            {
                var items = new IShellItem[1];
                while (!ct.IsCancellationRequested
                       && enumerator.Next(1, items, out var fetched) == 0
                       && fetched == 1
                       && items[0] is not null)
                {
                    var item = items[0]!;
                    try
                    {
                        if (item.GetDisplayName(SIGDN_NORMALDISPLAY, out var name) != 0)
                            continue;
                        if (string.IsNullOrEmpty(name)) continue;

                        // QI to IShellItem2 to read the AUMID property.
                        // Casting a ComImport interface implicitly calls
                        // QueryInterface — returns null if the item
                        // doesn't expose IShellItem2 (rare for
                        // shell:AppsFolder entries).
                        if (item is not IShellItem2 item2) continue;

                        var key = PKEY_AppUserModel_ID;
                        if (item2.GetString(ref key, out var aumid) != 0) continue;
                        if (string.IsNullOrEmpty(aumid)) continue;

                        result.Add(new UwpApp(name!, aumid!));
                    }
                    catch
                    {
                        // Skip this entry — one bad item shouldn't kill
                        // the whole enumeration.
                    }
                    finally
                    {
                        if (items[0] is not null)
                            Marshal.ReleaseComObject(items[0]);
                        items[0] = null!;
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(root);
        }

        return result;
    }

    private const uint SIGDN_NORMALDISPLAY = 0;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc,
        [In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);
        [PreserveSig] int GetParent(out IShellItem? ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName,
            [MarshalAs(UnmanagedType.LPWStr)] out string? ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    /// IShellItem2 extends IShellItem — vtable layout requires
    /// re-declaring every base method in order before the new ones.
    [ComImport, Guid("7E9FB0D3-919F-4307-AB2E-9B1860310C93"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);
        [PreserveSig] int GetParent(out IShellItem? ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName,
            [MarshalAs(UnmanagedType.LPWStr)] out string? ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);

        // IShellItem2 additions — only GetString is used here, the rest
        // are required for vtable layout only.
        [PreserveSig] int GetPropertyStore(uint flags, [In] ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);
        [PreserveSig] int GetPropertyStoreWithCreateObject(uint flags, IntPtr punk,
            [In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);
        [PreserveSig] int GetPropertyStoreForKeys(IntPtr rgKeys, uint cKeys, uint flags,
            [In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);
        [PreserveSig] int GetPropertyDescriptionList([In] ref PROPERTYKEY keyType,
            [In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);
        [PreserveSig] int Update(IntPtr pbc);
        [PreserveSig] int GetProperty([In] ref PROPERTYKEY key, IntPtr ppropvar);
        [PreserveSig] int GetCLSID([In] ref PROPERTYKEY key, out Guid pclsid);
        [PreserveSig] int GetFileTime([In] ref PROPERTYKEY key, out long pft);
        [PreserveSig] int GetInt32([In] ref PROPERTYKEY key, out int pi);
        [PreserveSig] int GetString([In] ref PROPERTYKEY key,
            [MarshalAs(UnmanagedType.LPWStr)] out string? ppsz);
        [PreserveSig] int GetUInt32([In] ref PROPERTYKEY key, out uint pui);
        [PreserveSig] int GetUInt64([In] ref PROPERTYKEY key, out ulong pull);
        [PreserveSig] int GetBool([In] ref PROPERTYKEY key, out int pf);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig] int Next(uint celt,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IShellItem[] rgelt,
            out uint pceltFetched);
        [PreserveSig] int Skip(uint celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumShellItems ppenum);
    }
}

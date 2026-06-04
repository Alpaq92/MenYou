using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AvBitmap = Avalonia.Media.Imaging.Bitmap;

namespace MenYou.Platform.Windows;

[SupportedOSPlatform("windows")]
internal static class IconExtractor
{
    public static AvBitmap? ExtractAvaloniaBitmap(string path, int iconIndex = 0, bool large = true)
    {
        using var icon = ExtractGdiIcon(path, iconIndex, large);
        return icon is null ? null : IconToAvaloniaBitmap(icon);
    }

    public static AvBitmap? ExtractForFile(string path)
    {
        var info = default(NativeMethods.SHFILEINFO);
        var flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON;
        var attr = File.Exists(path) || Directory.Exists(path) ? 0 : NativeMethods.FILE_ATTRIBUTE_NORMAL;
        if (attr != 0) flags |= NativeMethods.SHGFI_USEFILEATTRIBUTES;

        var hr = NativeMethods.SHGetFileInfo(path, attr, ref info, (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(), flags);
        if (hr == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

        try
        {
            using var icon = Icon.FromHandle(info.hIcon);
            return IconToAvaloniaBitmap(icon);
        }
        finally
        {
            NativeMethods.DestroyIcon(info.hIcon);
        }
    }

    /// Extracts a high-resolution shell icon for the given file. The
    /// preferred path is <see cref="IShellItemImageFactory"/> which lets
    /// the shell pick the best embedded variant (typically the 256×256
    /// one shipped in modern .exe / .ico resources) and resize to the
    /// requested target. Falls back to the system image list and then
    /// <see cref="ExtractForFile"/> on failure.
    public static AvBitmap? ExtractLargeForFile(string path, bool jumbo = false)
    {
        var targetSize = jumbo ? 256 : 48;
        return ExtractViaShellItemImageFactory(path, targetSize)
            ?? ExtractLargeViaImageList(path, jumbo)
            ?? ExtractForFile(path);
    }

    private static AvBitmap? ExtractViaShellItemImageFactory(string path, int size)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        // Ask SHCreateItemFromParsingName directly for IShellItemImageFactory
        // — the shell QI's internally so we don't have to.
        var iid = IID_IShellItemImageFactory;
        if (SHCreateItemFromParsingName2(path, IntPtr.Zero, ref iid, out IShellItemImageFactory? factory) != 0
            || factory is null)
            return null;
        try
        {
            var sz = new SIZE { cx = size, cy = size };
            // SIIGBF_ICONONLY forces the icon path (no thumbnail), and
            // BIGGERSIZEOK lets the shell return a larger cached variant
            // we then downscale ourselves.
            const uint SIIGBF_BIGGERSIZEOK = 0x1;
            const uint SIIGBF_ICONONLY     = 0x4;
            if (factory.GetImage(sz, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out var hbmp) != 0
                || hbmp == IntPtr.Zero)
                return null;
            try { return HBitmapToAvaloniaBitmap(hbmp); }
            finally { DeleteObject(hbmp); }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHCreateItemFromParsingName")]
    private static extern int SHCreateItemFromParsingName2(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? ppv);

    private static AvBitmap? ExtractLargeViaImageList(string path, bool jumbo)
    {
        var info = default(NativeMethods.SHFILEINFO);
        var flags = NativeMethods.SHGFI_SYSICONINDEX;
        var attr = File.Exists(path) || Directory.Exists(path) ? 0 : NativeMethods.FILE_ATTRIBUTE_NORMAL;
        if (attr != 0) flags |= NativeMethods.SHGFI_USEFILEATTRIBUTES;
        var hr = NativeMethods.SHGetFileInfo(path, attr, ref info,
            (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(), flags);
        if (hr == IntPtr.Zero) return null;
        return GetIconFromSystemImageList(info.iIcon, jumbo);
    }

    private static AvBitmap? HBitmapToAvaloniaBitmap(IntPtr hbmp)
    {
        // The shell HBITMAP is a 32 BPP DIB with proper BGRA premultiplied
        // alpha. System.Drawing.Image.FromHbitmap returns Format32bppRgb,
        // dropping alpha — the "transparent" pixels become solid black on
        // any non-white backdrop. Copy raw DIB bits directly into an
        // Avalonia WriteableBitmap to keep alpha intact.
        var info = default(BITMAP);
        if (GetObject(hbmp, Marshal.SizeOf<BITMAP>(), ref info) == 0) return null;
        if (info.bmBitsPixel != 32 || info.bmBits == IntPtr.Zero) return null;
        var w = info.bmWidth;
        var h = Math.Abs(info.bmHeight);
        var topDown = info.bmHeight < 0;
        var srcStride = info.bmWidthBytes;
        var bmp = new Avalonia.Media.Imaging.WriteableBitmap(
            new Avalonia.PixelSize(w, h),
            new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Premul);
        using (var fb = bmp.Lock())
        {
            unsafe
            {
                for (int y = 0; y < h; y++)
                {
                    var srcRow = (byte*)info.bmBits + (topDown ? y : (h - 1 - y)) * srcStride;
                    var dstRow = (byte*)fb.Address + y * fb.RowBytes;
                    Buffer.MemoryCopy(srcRow, dstRow, fb.RowBytes,
                        Math.Min(srcStride, fb.RowBytes));
                }
            }
        }
        return bmp;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, uint flags, out IntPtr phbm);
    }

    private static Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc,
        ref Guid riid, out IntPtr ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, ref BITMAP lpvObject);

    private static AvBitmap? GetIconFromSystemImageList(int index, bool jumbo)
    {
        var listKind = jumbo ? SHIL_JUMBO : SHIL_EXTRALARGE;
        var iid = IID_IImageList;
        if (SHGetImageList(listKind, ref iid, out var list) != 0 || list is null) return null;
        try
        {
            if (list.GetIcon(index, ILD_TRANSPARENT, out var hIcon) != 0 || hIcon == IntPtr.Zero)
                return null;
            try
            {
                using var icon = Icon.FromHandle(hIcon);
                return IconToAvaloniaBitmap(icon);
            }
            finally { NativeMethods.DestroyIcon(hIcon); }
        }
        finally
        {
            Marshal.ReleaseComObject(list);
        }
    }

    private const int SHIL_LARGE = 0;
    private const int SHIL_SMALL = 1;
    private const int SHIL_EXTRALARGE = 2;
    private const int SHIL_JUMBO = 4;
    private const int ILD_TRANSPARENT = 0x00000001;
    private static readonly Guid IID_IImageList =
        new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, out int pi);
        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, out int pi);
        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
        [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, out int pi);
        [PreserveSig] int Draw(IntPtr pimldp);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

    /// Extracts a UWP / packaged-app icon by parsing
    /// <c>shell:AppsFolder\&lt;Aumid&gt;</c> to a PIDL and asking the shell
    /// for the icon associated with that namespace item. This is the same
    /// path Explorer uses, so we get the proper app tile (not a generic
    /// .exe icon).
    public static AvBitmap? ExtractForAumid(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return null;
        var parseName = $"shell:AppsFolder\\{aumid}";
        // Prefer the SHGetFileInfo HICON (ExtractForShellNamespace): it
        // returns the app's tile icon at the NATIVE 32 px shell-icon size,
        // which renders 1:1 crisp in MenYou's 32 px tiles. Only fall back to
        // IShellItemImageFactory::GetImage (48 px PNG asset, downscaled —
        // slightly softer) for the few packages where the HICON path returns
        // null: notably WebExperienceHost ("Get Started"), which otherwise
        // dropped the Start-menu Place to a plain folder icon. (Final
        // per-action fallbacks live upstream in RightPanelViewModel.)
        return ExtractForShellNamespace(parseName)
            ?? ExtractViaShellItemImageFactory(parseName, 48);
    }

    /// True when a packaged-app AUMID resolves to a real shell item —
    /// i.e. the app is installed for this user. Used to decide whether to
    /// launch a packaged app directly or fall back to an alternative
    /// (e.g. launch the Phone Link tile vs. open the mobile-devices
    /// Settings page when Phone Link isn't installed). Cheap: just a
    /// parse-name resolve, no icon work.
    public static bool AppExists(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return false;
        var hr = SHParseDisplayName($"shell:AppsFolder\\{aumid}", IntPtr.Zero, out var pidl, 0, out _);
        if (hr != 0 || pidl == IntPtr.Zero) return false;
        Marshal.FreeCoTaskMem(pidl);
        return true;
    }

    /// Resolves a shell display-name (e.g. <c>shell:::{GUID}</c>,
    /// <c>shell:AppsFolder\&lt;Aumid&gt;</c>, or <c>::{GUID}</c>) to a
    /// PIDL via SHParseDisplayName, then asks the shell for the icon
    /// it'd render for that namespace item. This is the canonical
    /// "Explorer's icon for X" path — much more reliable than guessing
    /// resource indices into shell32.dll / imageres.dll because the
    /// shell handles its own per-build icon resource changes.
    ///
    /// Use cases (with the GUID for each):
    ///   "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}" — This PC
    ///   "::{208D2C60-3AEA-1069-A2D7-08002B30309D}" — Network
    ///   "::{26EE0668-A00A-44D7-9371-BEB064C98683}" — Control Panel
    ///   "::{645FF040-5081-101B-9F08-00AA002F954E}" — Recycle Bin
    public static AvBitmap? ExtractForShellNamespace(string parseName)
    {
        if (string.IsNullOrWhiteSpace(parseName)) return null;

        var hr = SHParseDisplayName(parseName, IntPtr.Zero, out var pidl, 0, out _);
        if (hr != 0 || pidl == IntPtr.Zero) return null;

        try
        {
            var info = default(NativeMethods.SHFILEINFO);
            var result = SHGetFileInfo_Pidl(pidl, 0, ref info,
                (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
                NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON | SHGFI_PIDL);
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try
            {
                using var icon = Icon.FromHandle(info.hIcon);
                return IconToAvaloniaBitmap(icon);
            }
            finally
            {
                NativeMethods.DestroyIcon(info.hIcon);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// Looks up one of the OS stock icons via SHGetStockIconInfo —
    /// guaranteed to match what Explorer renders for that conceptual
    /// item, regardless of which DLL the icon happens to live in on
    /// this build. Pass an SHSTOCKICONID value; the most useful ones
    /// for MenYou's Places list:
    ///   SIID_FOLDER         (3)   — generic folder
    ///   SIID_FOLDEROPEN     (4)   — open folder
    ///   SIID_SETTINGS       (106) — Settings (modern gear)
    /// Returns null when the API rejects the SIID (it does for IDs
    /// outside SIID_MAX_ICONS) or when the icon handle can't be wrapped.
    public static AvBitmap? ExtractStockIcon(int siid)
    {
        const uint SHGSI_ICON      = 0x0000_0100;
        const uint SHGSI_LARGEICON = 0x0000_0000;

        var info = new SHSTOCKICONINFO
        {
            cbSize = (uint)Marshal.SizeOf<SHSTOCKICONINFO>()
        };
        var hr = SHGetStockIconInfo(siid, SHGSI_ICON | SHGSI_LARGEICON, ref info);
        if (hr != 0 || info.hIcon == IntPtr.Zero) return null;
        try
        {
            using var icon = Icon.FromHandle(info.hIcon);
            return IconToAvaloniaBitmap(icon);
        }
        finally
        {
            NativeMethods.DestroyIcon(info.hIcon);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHSTOCKICONINFO
    {
        public uint cbSize;
        public IntPtr hIcon;
        public int iSysImageIndex;
        public int iIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szPath;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetStockIconInfo(int siid, uint uFlags, ref SHSTOCKICONINFO psii);

    private const uint SHGFI_PIDL = 0x000000008;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl,
        uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
    private static extern IntPtr SHGetFileInfo_Pidl(IntPtr pidl, uint dwFileAttributes,
        ref NativeMethods.SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    private static Icon? ExtractGdiIcon(string path, int iconIndex, bool large)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var handles = new IntPtr[1];
        var phLarge = large ? handles : null;
        var phSmall = large ? null : handles;
        var count = NativeMethods.ExtractIconEx(path, iconIndex, phLarge, phSmall, 1);
        if (count == 0 || handles[0] == IntPtr.Zero) return null;
        try
        {
            return (Icon)Icon.FromHandle(handles[0]).Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handles[0]);
        }
    }

    private static AvBitmap IconToAvaloniaBitmap(Icon icon)
    {
        using var gdi = icon.ToBitmap();
        using var ms = new MemoryStream();
        gdi.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        return new AvBitmap(ms);
    }
}

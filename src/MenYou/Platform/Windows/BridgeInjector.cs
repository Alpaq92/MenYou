using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace MenYou.Platform.Windows;

/// Loads the native MenYou.Bridge.dll (a shadow copy — see
/// <see cref="ResolveBridgePath"/>) into our process, then maps it into
/// Explorer.exe by installing a thread-targeted <c>WH_GETMESSAGE</c> hook on
/// Explorer's UI thread, OWNED BY THIS process. The hook proc
/// (<c>MenYouGetMsgProc</c>) catches the lone Win-tap inside Explorer, neuters
/// it so the system Start menu doesn't open, and posts <c>WM_COPYDATA</c> back
/// to our <see cref="CopyDataListener"/> to open MenYou.
///
/// Installing the hook from MenYou — rather than letting the DLL self-install
/// one from inside Explorer — is deliberate: Windows removes a process's hooks
/// when it exits or is killed, so the DLL unloads from Explorer on shutdown and
/// is never left pinned to its file (which previously wedged in-place upgrades).
///
/// If the bridge DLL is missing or injection fails for any reason (bitness
/// mismatch, locked-down machine), <see cref="Inject"/> returns false so the
/// caller can fall back to the out-of-process WinEvent monitor.
[SupportedOSPlatform("windows")]
internal sealed class BridgeInjector : IDisposable
{
    private const int WH_GETMESSAGE = 3;
    private const uint WM_NULL = 0;
    private const string BridgeDllName = "MenYou.Bridge.dll";
    private const string HookProcName = "MenYouGetMsgProc";

    private IntPtr _hModule;
    private IntPtr _hHook;
    private bool _disposed;

    /// Returns true if the bridge DLL was found, loaded, and successfully
    /// hooked into Explorer's UI thread.
    public bool Inject()
    {
        if (_hHook != IntPtr.Zero) return true; // already injected
        var dllPath = ResolveBridgePath();
        if (dllPath is null)
        {
            HookTrace.Log("BridgeInjector: bridge DLL not found alongside exe");
            return false;
        }

        _hModule = LoadLibraryW(dllPath);
        if (_hModule == IntPtr.Zero)
        {
            HookTrace.Log($"BridgeInjector: LoadLibrary failed err={Marshal.GetLastWin32Error()} path={dllPath}");
            return false;
        }

        var procAddr = GetProcAddress(_hModule, HookProcName);
        if (procAddr == IntPtr.Zero)
        {
            HookTrace.Log($"BridgeInjector: export '{HookProcName}' not found");
            FreeLibrary(_hModule);
            _hModule = IntPtr.Zero;
            return false;
        }

        var trayHwnd = FindWindowW("Shell_TrayWnd", null);
        if (trayHwnd == IntPtr.Zero)
        {
            HookTrace.Log("BridgeInjector: Shell_TrayWnd not found — Explorer may be down");
            FreeLibrary(_hModule);
            _hModule = IntPtr.Zero;
            return false;
        }

        var threadId = GetWindowThreadProcessId(trayHwnd, out var pid);
        if (threadId == 0)
        {
            HookTrace.Log("BridgeInjector: GetWindowThreadProcessId returned 0");
            FreeLibrary(_hModule);
            _hModule = IntPtr.Zero;
            return false;
        }

        _hHook = SetWindowsHookExW(WH_GETMESSAGE, procAddr, _hModule, threadId);
        if (_hHook == IntPtr.Zero)
        {
            HookTrace.Log($"BridgeInjector: SetWindowsHookEx failed err={Marshal.GetLastWin32Error()}");
            FreeLibrary(_hModule);
            _hModule = IntPtr.Zero;
            return false;
        }

        HookTrace.Log($"BridgeInjector: injected into Explorer pid={pid} thread={threadId}");
        // Nudge Explorer's message queue so the DLL maps in immediately rather
        // than waiting for the next message. POST (not Send) a WM_NULL no-op: a
        // WH_GETMESSAGE hook only fires for messages pulled from the queue, so a
        // posted message is what triggers the first callback and forces the map.
        PostMessageW(trayHwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hHook);
            _hHook = IntPtr.Zero;
        }
        if (_hModule != IntPtr.Zero)
        {
            FreeLibrary(_hModule);
            _hModule = IntPtr.Zero;
        }
        HookTrace.Log("BridgeInjector: disposed");
    }

    /// Resolves the native bridge to inject — but deliberately NOT the copy
    /// sitting in the install directory. <see cref="SetWindowsHookExW"/> maps
    /// whichever file we hand it into <c>explorer.exe</c>, which then keeps an
    /// open handle on that file for as long as the hook lives. If we injected
    /// the install-dir copy, an in-place upgrade would find Explorer locking a
    /// file under the install folder, and the installer's Restart Manager would
    /// try to close (i.e. kill) Explorer to free it — hanging the upgrade and
    /// tearing down the shell. (See the CloseApplicationsFilter note in
    /// installer/inno/menyou.iss for the matching installer-side guard.)
    ///
    /// So we shadow-copy the DLL into a per-user runtime folder OUTSIDE the
    /// install dir and inject that instead: the installer can then always
    /// overwrite the install-dir copy freely — it only ever needs to bounce
    /// MenYou.exe. Windows removes our hook automatically when MenYou exits, so
    /// the shadow copy is released and pruned on a later launch. Any failure
    /// falls back to the install-dir path, so the bridge still works in
    /// locked-down environments — at worst restoring the previous behaviour.
    private static string? ResolveBridgePath()
    {
        var installed = Path.Combine(AppContext.BaseDirectory, BridgeDllName);
        if (!File.Exists(installed)) return null;
        return ShadowCopy(installed) ?? installed;
    }

    /// Copies <paramref name="installed"/> into
    /// <c>%LOCALAPPDATA%\MenYou\bridge\MenYou.Bridge.&lt;hash&gt;.dll</c> and
    /// returns that path. The content hash in the name means identical bytes
    /// reuse the same file — so a second instance, or Explorer still holding it
    /// from a prior run, never blocks the copy — while a new build lands under a
    /// new name. Returns null on any failure so the caller falls back to the
    /// install copy.
    private static string? ShadowCopy(string installed)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MenYou", "bridge");
            Directory.CreateDirectory(dir);

            var srcLen = new FileInfo(installed).Length;
            string hash;
            using (var fs = File.OpenRead(installed))
                hash = Convert.ToHexString(SHA256.HashData(fs))[..12];

            var target = Path.Combine(dir, $"MenYou.Bridge.{hash}.dll");
            // Reuse only a COMPLETE copy. A truncated file from an interrupted
            // copy would share the (content-hashed) name yet fail to LoadLibrary,
            // so re-create it unless its length already matches the source.
            if (!File.Exists(target) || new FileInfo(target).Length != srcLen)
            {
                // Write to a unique temp, then atomically move into place, so the
                // final name only ever exists fully written — even across a crash
                // mid-copy or a second instance copying concurrently.
                var tmp = Path.Combine(dir, $"MenYou.Bridge.{hash}.{Guid.NewGuid():N}.tmp");
                try
                {
                    File.Copy(installed, tmp, overwrite: true);
                    File.Move(tmp, target, overwrite: true);
                }
                finally
                {
                    if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* ignore */ } }
                }
            }

            PruneOldCopies(dir, keep: Path.GetFileName(target));
            HookTrace.Log($"BridgeInjector: injecting shadow copy {target}");
            return target;
        }
        catch (Exception ex)
        {
            HookTrace.Log($"BridgeInjector: shadow-copy failed ({ex.GetType().Name}: {ex.Message}); using install-dir DLL");
            return null;
        }
    }

    /// Best-effort removal of shadow copies from earlier builds. Ones still
    /// mapped into Explorer (from a prior MenYou not yet replaced) can't be
    /// deleted and are simply skipped — a later launch reclaims them.
    private static void PruneOldCopies(string dir, string keep)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "MenYou.Bridge.*.dll"))
        {
            if (string.Equals(Path.GetFileName(file), keep, StringComparison.OrdinalIgnoreCase))
                continue;
            try { File.Delete(file); } catch { /* locked or already gone — ignore */ }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string lpLibFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true,
        ExactSpelling = true, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, IntPtr lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
}

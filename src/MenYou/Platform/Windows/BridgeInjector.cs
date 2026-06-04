using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MenYou.Platform.Windows;

/// Loads the native MenYou.Bridge.dll into our process, then asks Windows to
/// also map it into Explorer.exe by installing a thread-local
/// <c>WH_CALLWNDPROC</c> hook on Explorer's UI thread. From inside Explorer
/// the bridge subclasses the real Start button and posts <c>WM_COPYDATA</c>
/// back to our <see cref="CopyDataListener"/>.
///
/// If the bridge DLL is missing or injection fails for any reason (bitness
/// mismatch, locked-down machine), <see cref="Inject"/> returns false so the
/// caller can fall back to the out-of-process WinEvent monitor.
[SupportedOSPlatform("windows")]
internal sealed class BridgeInjector : IDisposable
{
    private const int WH_CALLWNDPROC = 4;
    private const string BridgeDllName = "MenYou.Bridge.dll";
    private const string HookProcName = "MenYouHookProc";

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

        _hHook = SetWindowsHookExW(WH_CALLWNDPROC, procAddr, _hModule, threadId);
        if (_hHook == IntPtr.Zero)
        {
            HookTrace.Log($"BridgeInjector: SetWindowsHookEx failed err={Marshal.GetLastWin32Error()}");
            FreeLibrary(_hModule);
            _hModule = IntPtr.Zero;
            return false;
        }

        HookTrace.Log($"BridgeInjector: injected into Explorer pid={pid} thread={threadId}");
        // Nudge Explorer's message queue so the DLL maps in immediately rather
        // than on the next stray message. SendMessage with WM_NULL is a no-op
        // but forces a hook callback into the target thread.
        SendMessageTimeout(trayHwnd, 0, IntPtr.Zero, IntPtr.Zero, 0, 100, out _);
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

    private static string? ResolveBridgePath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidate = Path.Combine(baseDir, BridgeDllName);
        return File.Exists(candidate) ? candidate : null;
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
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);
}

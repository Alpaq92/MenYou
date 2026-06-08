// MenYou.Bridge.dll — injected into Explorer.exe via SetWindowsHookEx from
// MenYou.exe. Two responsibilities right now:
//   1. Idle foothold that proves the injection plumbing works.
//   2. Optional message-spy that logs interesting Win32 messages on
//      Explorer's UI thread, so we can figure out which message a third-party
//      tool (Open-Shell) uses to detect the Win-tap on Win 11 24H2.
//
// To enable the spy, create a marker file %TEMP%\menyou-bridge-spy.flag
// before MenYou launches. Otherwise the bridge stays quiet.
//
// Always logs to %TEMP%\menyou-bridge.log.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdint.h>

#pragma comment(lib, "user32.lib")
#pragma comment(lib, "kernel32.lib")

static const wchar_t* kListenerClass = L"MenYouBridgeListener";

enum BridgeNotifyId : ULONG_PTR {
    BridgeNotify_StartClicked = 1,
    BridgeNotify_WinKey       = 2,
};

static volatile LONG g_initialized = 0;
static bool      g_isExplorer   = false;
static bool      g_spyEnabled   = false;

// Win-tap state machine. Manipulated only from Explorer's UI thread inside
// the WH_GETMESSAGE hook proc, so no atomics are strictly needed — but we
// keep them volatile in case the OS interleaves with our hook unexpectedly.
static volatile LONG g_winHeld     = 0;
static volatile LONG g_winCompound = 0;
static const WPARAM  VK_LWIN_W     = 0x5B;
static const WPARAM  VK_RWIN_W     = 0x5C;

static void Trace(const char* fmt, ...) {
    char path[MAX_PATH];
    if (GetEnvironmentVariableA("TEMP", path, sizeof(path)) == 0) return;
    strcat_s(path, sizeof(path), "\\menyou-bridge.log");

    char line[1024];
    SYSTEMTIME st; GetLocalTime(&st);
    int n = sprintf_s(line, sizeof(line), "%02u:%02u:%02u.%03u [%lu] ",
        st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, GetCurrentProcessId());
    va_list ap; va_start(ap, fmt);
    vsprintf_s(line + n, sizeof(line) - n, fmt, ap);
    va_end(ap);

    HANDLE h = CreateFileA(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;
    DWORD written = 0;
    WriteFile(h, line, (DWORD)strlen(line), &written, nullptr);
    const char nl[] = "\r\n";
    WriteFile(h, nl, 2, &written, nullptr);
    CloseHandle(h);
}

static bool ProcessNameIsExplorer() {
    wchar_t path[MAX_PATH] = {0};
    if (GetModuleFileNameW(nullptr, path, MAX_PATH) == 0) return false;
    const wchar_t* slash = wcsrchr(path, L'\\');
    const wchar_t* name = slash ? slash + 1 : path;
    return _wcsicmp(name, L"explorer.exe") == 0;
}

static void NotifyListener(ULONG_PTR id) {
    // Message-only listener lives under HWND_MESSAGE; plain FindWindow can't
    // see those windows, so we go through FindWindowEx with HWND_MESSAGE as
    // the parent.
    HWND listener = FindWindowExW(HWND_MESSAGE, nullptr, kListenerClass, nullptr);
    if (!listener) {
        Trace("notify id=%llu - no listener", (unsigned long long)id);
        return;
    }
    COPYDATASTRUCT cds = {};
    cds.dwData = id;
    cds.cbData = 0;
    cds.lpData = nullptr;
    DWORD_PTR result = 0;
    SendMessageTimeoutW(listener, WM_COPYDATA, 0, (LPARAM)&cds,
        SMTO_ABORTIFHUNG | SMTO_BLOCK, 200, &result);
}

static bool SpyMarkerPresent() {
    char path[MAX_PATH];
    if (GetEnvironmentVariableA("TEMP", path, sizeof(path)) == 0) return false;
    strcat_s(path, sizeof(path), "\\menyou-bridge-spy.flag");
    return GetFileAttributesA(path) != INVALID_FILE_ATTRIBUTES;
}

// ---- WH_GETMESSAGE spy ---------------------------------------------------
// Logs the messages Open-Shell is known to care about: WM_HOTKEY, anything
// from WM_USER (0x0400) up to WM_APP+0xFF, and WM_SYSCOMMAND. Filters out
// the firehose of mouse-move / paint / timer noise.

static const wchar_t* ClassNameOf(HWND hwnd) {
    static wchar_t buf[64];
    buf[0] = 0;
    GetClassNameW(hwnd, buf, sizeof(buf) / sizeof(buf[0]));
    return buf;
}

static bool IsWinKey(WPARAM vk) { return vk == VK_LWIN_W || vk == VK_RWIN_W; }

// WH_GETMESSAGE hook proc, run on Explorer's UI thread inside Explorer.exe.
// On Win 11 24H2 the lone-Win-tap routes plain WM_KEYDOWN / WM_KEYUP with
// VK_LWIN to Explorer's "Windows.UI.Input.InputSite.WindowClass" hwnd —
// confirmed by spying on Open-Shell's running install. Open-Shell catches
// the same WM_KEYUP and replaces the message body so the system shell's
// Start-menu detector never fires. We do the same; on a real lone-tap we
// also post WM_COPYDATA to MenYou so the managed launcher pops the menu.
//
// Win+letter combinations stay intact because we mark the press as compound
// the moment any other key arrives during Win-hold and skip the neuter step.
//
// This proc is EXPORTED and installed by MenYou.exe itself (BridgeInjector),
// targeting Explorer's UI thread. Because MenYou owns the hook, Windows removes
// it — and unloads this DLL from Explorer — when MenYou exits OR is killed. An
// earlier version self-installed the hook from inside Explorer (owned by
// Explorer's own thread), so the DLL stayed pinned to its file after MenYou
// closed and blocked in-place upgrades. Don't reintroduce that.
extern "C" __declspec(dllexport)
LRESULT CALLBACK MenYouGetMsgProc(int code, WPARAM wParam, LPARAM lParam) {
    if (code >= 0 && g_isExplorer && InterlockedExchange(&g_initialized, 1) == 0)
        Trace("MenYouGetMsgProc: first call inside Explorer pid=%lu (spy=%d)",
              GetCurrentProcessId(), g_spyEnabled ? 1 : 0);
    if (code < 0 || wParam == PM_NOREMOVE) return CallNextHookEx(nullptr, code, wParam, lParam);
    auto* msg = reinterpret_cast<MSG*>(lParam);
    if (!msg) return CallNextHookEx(nullptr, code, wParam, lParam);

    UINT m = msg->message;
    bool keyDown = (m == WM_KEYDOWN || m == WM_SYSKEYDOWN);
    bool keyUp   = (m == WM_KEYUP   || m == WM_SYSKEYUP);

    if (keyDown && IsWinKey(msg->wParam)) {
        if (!g_winHeld) {
            g_winHeld = 1;
            g_winCompound = 0;
            if (g_spyEnabled)
                Trace("WinTap: down vk=0x%llX", (unsigned long long)msg->wParam);
        }
        return CallNextHookEx(nullptr, code, wParam, lParam);
    }

    if (keyUp && IsWinKey(msg->wParam)) {
        bool wasLone = (g_winHeld != 0) && (g_winCompound == 0);
        g_winHeld = 0;
        if (wasLone) {
            // Neuter so the system shell's lone-tap detector sees nothing.
            // The receiving WindowProc gets WM_NULL, which it ignores.
            msg->message = WM_NULL;
            msg->wParam = 0;
            msg->lParam = 0;
            Trace("WinTap: lone tap intercepted — message neutered, notifying MenYou");
            NotifyListener(BridgeNotify_WinKey);
        } else if (g_spyEnabled) {
            Trace("WinTap: up vk=0x%llX (compound, passing through)", (unsigned long long)msg->wParam);
        }
        return CallNextHookEx(nullptr, code, wParam, lParam);
    }

    if ((keyDown || keyUp) && g_winHeld) {
        // Any non-Win key during the hold makes this a compound press
        // (Win+E, Win+R, etc.); never intercept those.
        if (!g_winCompound) {
            g_winCompound = 1;
            if (g_spyEnabled)
                Trace("WinTap: compound — non-Win vk=0x%llX during Win-hold",
                    (unsigned long long)msg->wParam);
        }
    }

    return CallNextHookEx(nullptr, code, wParam, lParam);
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID /*reserved*/)
{
    switch (reason) {
        case DLL_PROCESS_ATTACH:
            DisableThreadLibraryCalls(hModule);
            g_isExplorer = ProcessNameIsExplorer();
            g_spyEnabled = SpyMarkerPresent();
            Trace("DLL_PROCESS_ATTACH isExplorer=%d spyEnabled=%d",
                  g_isExplorer ? 1 : 0, g_spyEnabled ? 1 : 0);
            break;
        case DLL_PROCESS_DETACH:
            Trace("DLL_PROCESS_DETACH");
            break;
    }
    return TRUE;
}

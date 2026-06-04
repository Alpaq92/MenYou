using System.Runtime.Versioning;
using MenYou.Platform.Windows;

namespace MenYou.Services;

[SupportedOSPlatform("windows")]
public sealed class Win32PowerService : IPowerService
{
    public void Shutdown() => NativeMethods.ExitWindowsEx(
        NativeMethods.EWX_SHUTDOWN | NativeMethods.EWX_POWEROFF | NativeMethods.EWX_FORCEIFHUNG,
        NativeMethods.SHUTDOWN_REASON_PLANNED);

    public void Restart() => NativeMethods.ExitWindowsEx(
        NativeMethods.EWX_REBOOT | NativeMethods.EWX_FORCEIFHUNG,
        NativeMethods.SHUTDOWN_REASON_PLANNED);

    public void SignOut() => NativeMethods.ExitWindowsEx(
        NativeMethods.EWX_LOGOFF | NativeMethods.EWX_FORCEIFHUNG,
        NativeMethods.SHUTDOWN_REASON_PLANNED);

    public void Lock() => NativeMethods.LockWorkStation();

    public void Sleep() => NativeMethods.SetSuspendState(false, false, false);
}

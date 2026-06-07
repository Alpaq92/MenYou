using System.Runtime.Versioning;
using MenYou.Platform.Windows;

namespace MenYou.Services;

[SupportedOSPlatform("windows")]
public sealed class Win32PowerService : IPowerService
{
    public void Shutdown()
    {
        // Shutdown/reboot need SE_SHUTDOWN enabled or ExitWindowsEx no-ops.
        NativeMethods.EnableShutdownPrivilege();
        NativeMethods.ExitWindowsEx(
            NativeMethods.EWX_SHUTDOWN | NativeMethods.EWX_POWEROFF | NativeMethods.EWX_FORCEIFHUNG,
            NativeMethods.SHUTDOWN_REASON_PLANNED);
    }

    public void Restart()
    {
        NativeMethods.EnableShutdownPrivilege();
        NativeMethods.ExitWindowsEx(
            NativeMethods.EWX_REBOOT | NativeMethods.EWX_FORCEIFHUNG,
            NativeMethods.SHUTDOWN_REASON_PLANNED);
    }

    // Sign-out (logoff) and Lock don't require SE_SHUTDOWN, so they already
    // worked — left as-is.
    public void SignOut() => NativeMethods.ExitWindowsEx(
        NativeMethods.EWX_LOGOFF | NativeMethods.EWX_FORCEIFHUNG,
        NativeMethods.SHUTDOWN_REASON_PLANNED);

    public void Lock() => NativeMethods.LockWorkStation();

    public void Sleep()
    {
        // SetSuspendState also requires SE_SHUTDOWN.
        NativeMethods.EnableShutdownPrivilege();
        NativeMethods.SetSuspendState(false, false, false);
    }
}

#nullable disable
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Feil.Core;

static class PlatformUtilities
{
    public static void SetExecutable(string path, bool value)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        const UnixFileMode ModeExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

        var mode = File.GetUnixFileMode(path);
        var hasExecuteMask = (mode & ModeExecute) == ModeExecute;
        if (hasExecuteMask != value)
        {
            File.SetUnixFileMode(path, value
                ? mode | ModeExecute
                : mode & ~ModeExecute);
        }
    }
}

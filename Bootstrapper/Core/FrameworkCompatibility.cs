using System;
using System.Runtime.Versioning;

[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace ILauncher.Bootstrapper
{
    internal static class FrameworkCompatibility
    {
        internal static void Initialize()
        {
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
        }
    }
}

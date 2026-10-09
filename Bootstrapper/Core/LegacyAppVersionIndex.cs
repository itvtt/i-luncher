using System;

namespace ILauncher.Bootstrapper
{
    /// <summary>
    /// Compatibility versions for packages in immutable catalog v0.2.17
    /// (catalog SHA256 541f82d57d4b546d7c039a75310da2952302f629985098300872dfe0235ca400).
    /// Values were read from the main managed DLLs only after each package matched its catalog SHA256.
    /// The DLLs report ProductVersion 1.0.0+7892841c7f27577fd4d2c773a2bed0482e07913e and
    /// FileVersion/AssemblyVersion 1.0.0.0; the catalog-facing SemVer is 1.0.0.
    /// </summary>
    internal static class LegacyAppVersionIndex
    {
        internal static bool TryResolve(string appId, string packageSha256, out string version)
        {
            version = null;
            if (String.IsNullOrEmpty(appId) || String.IsNullOrEmpty(packageSha256)) return false;

            // All five verified payloads have the same app SemVer; the informational commit suffix is build metadata.
            if (String.Equals(packageSha256, "296821d47fce35206c22864049d462fd4668cfd87c50d6f3ed9b585c9d0a6a15", StringComparison.OrdinalIgnoreCase) &&
                String.Equals(appId, "deskcal", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(packageSha256, "a33556ed6aec2e33886a60226ac348ad496dca50139ab408acbb4b3458d5c191", StringComparison.OrdinalIgnoreCase) &&
                String.Equals(appId, "disk-atlas", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(packageSha256, "5bdc39a26f3e3af0c8191bea2e0e31b1b05aa93fff832fe272851ab03f12cc04", StringComparison.OrdinalIgnoreCase) &&
                String.Equals(appId, "folder-compare", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(packageSha256, "ae2f2dbefe82422c9bade9fd45d2dd340f31940c845a0d7b2ed6f24ea4cab469", StringComparison.OrdinalIgnoreCase) &&
                String.Equals(appId, "terminalnotes", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(packageSha256, "91620ff736a48fe6f4547c464742a48fb1619f2cb14df6f7938def712deb7c3f", StringComparison.OrdinalIgnoreCase) &&
                String.Equals(appId, "swift-copy", StringComparison.OrdinalIgnoreCase))
            {
                version = "1.0.0";
                return true;
            }
            return false;
        }
    }
}

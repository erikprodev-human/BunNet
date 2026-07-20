using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace BunNet
{
    /// <summary>
    /// Findet die Bun-Executable. Reihenfolge:
    ///   1. Explizit gesetzter Pfad (<see cref="BunServerOptions.BunExecutable"/> enthält Verzeichnistrenner)
    ///   2. PATH-Umgebungsvariable
    ///   3. Bekannte Installationsorte (~/.bun/bin, Homebrew, winget, scoop)
    /// Wird Bun nirgends gefunden, gibt es eine sprechende Fehlermeldung mit dem
    /// Hinweis, den Pfad vor dem Start per <c>options.BunExecutable</c> zu setzen.
    /// </summary>
    internal static class BunLocator
    {
        public static string Resolve(string configured)
        {
            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

            // 1. Expliziter Pfad: unverändert übernehmen, aber früh und klar scheitern.
            if (configured.IndexOf('/') >= 0 || configured.IndexOf('\\') >= 0)
            {
                if (File.Exists(configured)) return Path.GetFullPath(configured);
                throw new FileNotFoundException(
                    "Die in BunServerOptions.BunExecutable angegebene Bun-Executable existiert nicht: " +
                    configured, configured);
            }

            // 2. PATH durchsuchen.
            string? fromPath = SearchEnvironmentPath(configured, isWindows);
            if (fromPath != null) return fromPath;

            // 3. Bekannte Installationsorte durchsuchen.
            foreach (string candidate in WellKnownLocations(configured, isWindows))
            {
                if (File.Exists(candidate)) return candidate;
            }

            throw new FileNotFoundException(
                "Bun wurde nicht gefunden ('" + configured + "' weder im PATH noch an bekannten " +
                "Installationsorten). Entweder Bun installieren (https://bun.sh) oder den Pfad " +
                "vor dem Start explizit setzen: options.BunExecutable = \"/pfad/zu/bun\";");
        }

        private static string? SearchEnvironmentPath(string fileName, bool isWindows)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string entry in path.Split(Path.PathSeparator))
            {
                if (entry.Length == 0) continue;
                string candidate;
                try { candidate = Path.Combine(entry.Trim(), fileName); }
                catch (ArgumentException) { continue; } // ungültige Zeichen im PATH-Eintrag
                if (File.Exists(candidate)) return candidate;
                if (isWindows && File.Exists(candidate + ".exe")) return candidate + ".exe";
            }
            return null;
        }

        private static IEnumerable<string> WellKnownLocations(string fileName, bool isWindows)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            List<string> locations = new List<string>();

            if (isWindows)
            {
                string exe = fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ".exe";
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                locations.Add(Path.Combine(home, ".bun", "bin", exe));                       // offizieller Installer
                locations.Add(Path.Combine(localAppData, "Microsoft", "WinGet", "Links", exe)); // winget
                locations.Add(Path.Combine(home, "scoop", "shims", exe));                    // scoop
            }
            else
            {
                locations.Add(Path.Combine(home, ".bun", "bin", fileName)); // offizieller Installer
                locations.Add("/opt/homebrew/bin/" + fileName);             // Homebrew (Apple Silicon)
                locations.Add("/usr/local/bin/" + fileName);                // Homebrew (Intel) / manuell
                locations.Add("/usr/bin/" + fileName);                      // Paketmanager
            }

            return locations;
        }
    }
}

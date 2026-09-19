using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace PadForge.Engine.Common.Logitech
{
    /// <summary>
    /// Finds <c>LogitechGkey.dll</c> (issue #454).
    ///
    /// <para>Registry first, default install path second, which is the order
    /// Mumble's <c>GKey.cpp</c> has used in production for years. The registry
    /// matters for two reasons: it finds an install that is not in Program
    /// Files, and it answers "is this SDK on this machine" as a fact instead
    /// of a guess about which Logitech software the user runs.</para>
    ///
    /// <para>The SDK ships with Logitech Gaming Software. G HUB, which
    /// replaced it, carries legacy libraries for lighting, ARX and steering
    /// wheels, and no public source shows it carrying a G-key one. Rather than
    /// encode that as a rule, this looks for the library and reports what it
    /// found, so a machine that does have it works whatever put it there.</para>
    /// </summary>
    public static class LogitechGKeyCatalog
    {
        /// <summary>The SDK's registered CLSID. Its ServerBinary value is the
        /// DLL's path.</summary>
        public const string ClassId = "{7bded654-f278-4977-a20f-6e72a0d07859}";

        private const string ServerBinary = "ServerBinary";

        /// <summary>Why no library was found, for the status line.</summary>
        public enum Reason
        {
            Found = 0,
            NoRegistryEntry,
            RegistryPathMissing,
            NotInstalled,
        }

        /// <summary>
        /// The library's path, or null with a reason.
        ///
        /// <para>A registry value pointing at a file that is no longer there
        /// is reported as its own case, because it means the Logitech software
        /// was uninstalled and left the class behind, which is a different
        /// thing for a user to fix than never having installed it.</para>
        /// </summary>
        public static string Find(out Reason reason)
        {
            bool sawRegistry = false;
            bool registryPathMissing = false;

            foreach (string path in RegistryCandidates())
            {
                sawRegistry = true;
                if (File.Exists(path))
                {
                    reason = Reason.Found;
                    return path;
                }
                registryPathMissing = true;
            }

            foreach (string path in DefaultCandidates())
            {
                if (!File.Exists(path)) continue;
                reason = Reason.Found;
                return path;
            }

            reason = registryPathMissing ? Reason.RegistryPathMissing
                   : sawRegistry ? Reason.NoRegistryEntry
                   : Reason.NotInstalled;
            return null;
        }

        /// <summary>
        /// Paths the registered class points at.
        ///
        /// <para>Both views are read rather than only the one matching this
        /// process. A 64-bit process reads the class under Wow6432Node because
        /// that is where a 32-bit registration lands, and the SDK registers
        /// itself there. Reading both costs one key open and covers a machine
        /// that has only the other.</para>
        /// </summary>
        private static IEnumerable<string> RegistryCandidates()
        {
            foreach (string subKey in new[]
                     {
                         @"Wow6432Node\CLSID\" + ClassId + @"\" + ServerBinary,
                         @"CLSID\" + ClassId + @"\" + ServerBinary,
                     })
            {
                string value = null;
                try
                {
                    using var key = Registry.ClassesRoot.OpenSubKey(subKey);
                    value = key?.GetValue(string.Empty) as string;
                }
                catch (Exception)
                {
                    // A registry the process cannot read is the same outcome
                    // as one with nothing in it: try the next candidate.
                }
                if (!string.IsNullOrWhiteSpace(value)) yield return value.Trim();
            }
        }

        /// <summary>Where the installer puts it when nothing redirected it.</summary>
        private static IEnumerable<string> DefaultCandidates()
        {
            foreach (var folder in new[]
                     {
                         Environment.SpecialFolder.ProgramFiles,
                         Environment.SpecialFolder.ProgramFilesX86,
                     })
            {
                string root;
                try { root = Environment.GetFolderPath(folder); }
                catch (Exception) { continue; }
                if (string.IsNullOrEmpty(root)) continue;

                // The SDK ships both builds. Ours has to match the process, and
                // PadForge is x64, so that one is tried first.
                yield return Path.Combine(root, "Logitech Gaming Software", "SDK", "G-key", "x64", "LogitechGkey.dll");
                yield return Path.Combine(root, "Logitech Gaming Software", "SDK", "G-key", "x86", "LogitechGkey.dll");
            }
        }
    }
}

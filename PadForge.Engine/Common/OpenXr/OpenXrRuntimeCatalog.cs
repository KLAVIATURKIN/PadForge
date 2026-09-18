using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PadForge.Engine.Common.OpenXr
{
    /// <summary>One OpenXR runtime the machine has, as its manifest describes it.</summary>
    public sealed class OpenXrRuntimeEntry
    {
        /// <summary>Path of the .json manifest.</summary>
        public string ManifestPath { get; init; }

        /// <summary>Name the manifest gives, or the manifest's file name when
        /// it gives none. Runtimes are not required to carry a name, and an
        /// unnamed one still has to be selectable.</summary>
        public string Name { get; init; }

        /// <summary>Absolute path of the runtime library.</summary>
        public string LibraryPath { get; init; }

        /// <summary>True when this is the machine's ActiveRuntime.</summary>
        public bool IsSystemDefault { get; init; }

        public bool LibraryExists => !string.IsNullOrEmpty(LibraryPath) && File.Exists(LibraryPath);

        public override string ToString() => $"{Name} ({ManifestPath})";
    }

    /// <summary>
    /// Finds the OpenXR runtimes installed on the machine by reading the
    /// manifests Khronos registers under HKLM (issue #403).
    ///
    /// <para>Reading only. The machine's ActiveRuntime belongs to whatever
    /// the user set it to, and a background input client has no business
    /// changing which runtime every other application loads. Selection here
    /// means choosing which manifest THIS process negotiates with.</para>
    /// </summary>
    public static class OpenXrRuntimeCatalog
    {
        private const string KhronosKey = @"SOFTWARE\Khronos\OpenXR\1";

        /// <summary>Parses a runtime manifest. Returns null when the document
        /// is not one, rather than throwing, because the registry can point
        /// at a file an uninstall left behind.</summary>
        public static OpenXrRuntimeEntry TryParseManifest(string manifestPath, string json, bool isDefault = false)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("runtime", out var runtime)) return null;
                if (!runtime.TryGetProperty("library_path", out var lib)) return null;
                string library = lib.GetString();
                if (string.IsNullOrWhiteSpace(library)) return null;

                // The manifest may name the library relative to its own folder,
                // which is how both Virtual Desktop and SteamVR write it.
                if (!Path.IsPathRooted(library))
                {
                    string dir = Path.GetDirectoryName(manifestPath);
                    if (!string.IsNullOrEmpty(dir))
                        library = Path.GetFullPath(Path.Combine(dir, library));
                }

                string name = runtime.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name))
                    name = Path.GetFileNameWithoutExtension(manifestPath);

                return new OpenXrRuntimeEntry
                {
                    ManifestPath = manifestPath,
                    Name = name,
                    LibraryPath = library,
                    IsSystemDefault = isDefault,
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Every runtime the registry lists, the system default
        /// first. Never throws: a machine with no VR software at all is an
        /// ordinary case, and it reads as an empty list.</summary>
        public static IReadOnlyList<OpenXrRuntimeEntry> Discover()
        {
            var found = new List<OpenXrRuntimeEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string active = ReadActiveRuntimePath();
            if (!string.IsNullOrEmpty(active))
            {
                var entry = Load(active, isDefault: true);
                if (entry != null && seen.Add(entry.ManifestPath)) found.Add(entry);
            }

            foreach (string path in ReadAvailableRuntimePaths())
            {
                var entry = Load(path, isDefault: false);
                if (entry != null && seen.Add(entry.ManifestPath)) found.Add(entry);
            }

            return found;
        }

        private static OpenXrRuntimeEntry Load(string manifestPath, bool isDefault)
        {
            try
            {
                if (!File.Exists(manifestPath)) return null;
                return TryParseManifest(manifestPath, File.ReadAllText(manifestPath), isDefault);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static string ReadActiveRuntimePath()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(KhronosKey);
                return key?.GetValue("ActiveRuntime") as string;
            }
            catch (Exception) { return null; }
        }

        private static IEnumerable<string> ReadAvailableRuntimePaths()
        {
            var paths = new List<string>();
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    KhronosKey + @"\AvailableRuntimes");
                if (key == null) return paths;
                foreach (string name in key.GetValueNames())
                {
                    // The value name is the manifest path and the data is a
                    // DWORD where 0 means enabled, which is the inversion the
                    // Khronos loader documents.
                    object data = key.GetValue(name);
                    if (data is int disabled && disabled != 0) continue;
                    if (!string.IsNullOrWhiteSpace(name)) paths.Add(name);
                }
            }
            catch (Exception) { }
            return paths;
        }
    }
}

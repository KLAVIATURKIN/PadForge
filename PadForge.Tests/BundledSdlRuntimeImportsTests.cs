using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// SDL3.dll comes from the fork as a binary, and which Visual C++ runtime
    /// DLLs it imports depends on how the fork was built. The x64 one imports
    /// msvcp140.dll and vcruntime140_1.dll for the Elite paddle reader, which
    /// is C++. The ARM64 one delivered for 4.5.1 had that reader switched off
    /// and imported neither, so the ARM64 build stopped carrying them.
    ///
    /// <para>The fork then built the reader for ARM64 (a1416320e2), and the
    /// ARM64 SDL3.dll imports msvcp140.dll again. Taken without the DLL
    /// beside it, SDL3.dll fails to load on a clean ARM64 machine and every
    /// controller is dead, with nothing at build time to say why. So each
    /// SDL3.dll is read for the runtime DLLs it names, and each one named
    /// has to be in the same architecture's VisualCpp folder. This test
    /// was written ahead of that delivery and failed on it, which is how
    /// the ARM64 msvcp140.dll came to be bundled in the same commit.</para>
    /// </summary>
    public class BundledSdlRuntimeImportsTests
    {
        private static readonly string[] RuntimeDlls = { "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" };

        public static IEnumerable<object[]> Architectures()
        {
            string sdl = Path.Combine(ResourcesRoot(), "SDL3");
            foreach (string dir in Directory.GetDirectories(sdl))
                if (File.Exists(Path.Combine(dir, "SDL3.dll")))
                    yield return new object[] { Path.GetFileName(dir) };
        }

        [Theory]
        [MemberData(nameof(Architectures))]
        public void EveryRuntimeDllSdlImportsIsBundledForThatArchitecture(string arch)
        {
            var named = RuntimeDllsNamedBy(File.ReadAllBytes(Path.Combine(ResourcesRoot(), "SDL3", arch, "SDL3.dll")));
            // vcruntime140.dll is imported by every MSVC build, so an empty
            // answer means the scan is broken, not that SDL needs nothing.
            Assert.Contains("vcruntime140.dll", named);

            var missing = named.Where(dll => !File.Exists(Path.Combine(ResourcesRoot(), "VisualCpp", arch, dll))).ToList();
            Assert.True(missing.Count == 0,
                "Resources/SDL3/" + arch + "/SDL3.dll imports " + string.Join(", ", missing)
                + ", and Resources/VisualCpp/" + arch + " does not carry it.");
        }

        [Fact]
        public void BothArchitecturesAreChecked()
        {
            var archs = Architectures().Select(a => (string)a[0]).ToList();
            Assert.Contains("x64", archs);
            Assert.Contains("arm64", archs);
        }

        /// <summary>The scan has to be able to see a name, or the theory
        /// above passes on anything. The x64 SDL3.dll is known to import all
        /// three (dumpbin /dependents, recorded in the project file).</summary>
        [Fact]
        public void TheScanSeesTheThreeNamesTheX64SdlImports()
        {
            var named = RuntimeDllsNamedBy(File.ReadAllBytes(Path.Combine(ResourcesRoot(), "SDL3", "x64", "SDL3.dll")));
            Assert.Equal(RuntimeDlls.OrderBy(n => n), named.OrderBy(n => n));
        }

        /// <summary>Import names are plain ASCII in the image, in whatever
        /// case the linker wrote them (MSVCP140.dll, VCRUNTIME140.dll).</summary>
        private static List<string> RuntimeDllsNamedBy(byte[] image)
        {
            string text = Encoding.Latin1.GetString(image);
            return RuntimeDlls.Where(dll => text.IndexOf(dll, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }

        private static string ResourcesRoot()
        {
            string sdl = AuditDelta20260823Tests.FindRepoFile(
                Path.Combine("PadForge.App", "Resources", "SDL3", "x64", "SDL3.dll"));
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sdl), "..", ".."));
        }
    }
}

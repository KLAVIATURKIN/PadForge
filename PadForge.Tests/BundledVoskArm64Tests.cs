using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The ARM64 build's libvosk.dll. Vosk publishes no Windows ARM64 binary,
    /// so this one is built from upstream's own unshipped recipe by
    /// tools/build-libvosk-arm64.sh, with every source pinned.
    ///
    /// <para>The bench is x64 and cannot load it, so what can be read from
    /// its bytes is pinned instead: that it answers every call the managed
    /// Vosk binding makes, and that it needs no DLL the ARM64 build does not
    /// have. Its machine type is covered with every other bundled image by
    /// <see cref="NativeBinaryArchitectureTests"/>.</para>
    /// </summary>
    public class BundledVoskArm64Tests
    {
        private static byte[] NativeLibrary()
            => File.ReadAllBytes(AuditDelta20260823Tests.FindRepoFile(
                Path.Combine("PadForge.App", "Resources", "Vosk", "arm64", "libvosk.dll")));

        private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

        /// <summary>The build is not byte-reproducible: two runs of the
        /// script on one machine gave files that differ in about 87,000
        /// bytes. So the file in the repository is pinned, and whoever
        /// rebuilds it changes this line on purpose, with the two checks
        /// below passing on the new file.</summary>
        [Fact]
        public void TheBundledFileIsTheOneThatWasChecked()
        {
            Assert.Equal("A38680BA5C6BDFEED489A51D5946F606A9B3C830D8407D1CB4753E483A5C2ACC",
                         Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(NativeLibrary())));
        }

        /// <summary>The managed binding names each native function it calls,
        /// and those names sit in its metadata as plain text. A newer Vosk
        /// package that calls one more function than this DLL exports would
        /// load, run, and throw EntryPointNotFoundException at that one call,
        /// on ARM64 only. Read from the binding the app actually ships.</summary>
        [Fact]
        public void ItExportsEveryFunctionTheManagedBindingCalls()
        {
            string binding = Path.Combine(AppContext.BaseDirectory, "Vosk.dll");
            Assert.True(File.Exists(binding), "Vosk.dll is not beside the test assembly");

            var called = Regex.Matches(Latin1(File.ReadAllBytes(binding)), "vosk_[a-z_0-9]+")
                .Select(m => m.Value).Distinct().OrderBy(n => n).ToList();
            var exported = Regex.Matches(Latin1(NativeLibrary()), "vosk_[a-z_0-9]+")
                .Select(m => m.Value).ToHashSet();

            // The walk proves nothing if it found nothing to check.
            Assert.True(called.Count >= 20, "only " + called.Count + " native names found in the binding");
            Assert.Contains("vosk_recognizer_new_grm", called);
            Assert.DoesNotContain(called, n => !exported.Contains(n));
        }

        /// <summary>The x64 libvosk.dll needs three MinGW runtime DLLs beside
        /// it. This one links its C++ runtime statically, so it may import
        /// KERNEL32 and the Universal C Runtime that is part of Windows, and
        /// nothing else. A rebuild without -static imports libc++.dll and
        /// libunwind.dll, which the ARM64 build does not carry, and the
        /// recognizer would fail to load on every ARM64 machine.</summary>
        [Fact]
        public void ItNeedsNoCompanionDll()
        {
            var named = Regex.Matches(Latin1(NativeLibrary()), @"[A-Za-z0-9][A-Za-z0-9_.\-]{2,60}\.dll",
                                      RegexOptions.IgnoreCase)
                .Select(m => m.Value.ToLowerInvariant()).Distinct().ToList();

            Assert.Contains("kernel32.dll", named);
            var unexpected = named.Where(n => n != "kernel32.dll"
                                           && n != "libvosk.dll"
                                           && !n.StartsWith("api-ms-win-crt-", StringComparison.Ordinal)).ToList();
            Assert.Empty(unexpected);
        }
    }
}

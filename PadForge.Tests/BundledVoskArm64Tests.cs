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
    /// have. Both are read from the tables the Windows loader reads
    /// (<see cref="PeImage"/>), not from a search of the file's text. Its
    /// machine type is covered with every other bundled image by
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
        /// rebuilds it changes this line on purpose, with the checks below
        /// passing on the new file.</summary>
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
        /// on ARM64 only. Read from the binding the app actually ships, and
        /// checked against the DLL's export table.</summary>
        [Fact]
        public void ItExportsEveryFunctionTheManagedBindingCalls()
        {
            string binding = Path.Combine(AppContext.BaseDirectory, "Vosk.dll");
            Assert.True(File.Exists(binding), "Vosk.dll is not beside the test assembly");

            var called = Regex.Matches(Latin1(File.ReadAllBytes(binding)), "vosk_[a-z_0-9]+")
                .Select(m => m.Value).Distinct().OrderBy(n => n).ToList();
            var exported = new PeImage(NativeLibrary()).ExportedNames().ToHashSet();

            // The walk proves nothing if it found nothing to check.
            Assert.True(called.Count >= 20, "only " + called.Count + " native names found in the binding");
            Assert.Contains("vosk_recognizer_new_grm", called);
            Assert.DoesNotContain(called, n => !exported.Contains(n));
        }

        /// <summary>The build script writes the export list from the C API in
        /// vosk_api.h, which declares 35 functions at 0.3.38. More would mean
        /// something besides the API leaked out, fewer that the script's
        /// reading of the header lost a declaration.</summary>
        [Fact]
        public void TheExportTableIsTheCApiOfVosk0338()
        {
            var names = new PeImage(NativeLibrary()).ExportedNames();
            Assert.Equal(35, names.Count);
            Assert.Equal(35, names.Distinct().Count());
            Assert.All(names, n => Assert.StartsWith("vosk_", n));
        }

        /// <summary>The check above is only worth having if it reads the
        /// TABLE. A copy with its export directory taken away still holds
        /// all 35 names as text, which is what a search of the file sees,
        /// and it exports nothing.</summary>
        [Fact]
        public void ACopyWithNoExportDirectoryExportsNothing_ThoughEveryNameIsStillInIt()
        {
            byte[] copy = NativeLibrary();
            uint entry = new PeImage(copy).DataDirectoryFileOffset(0);
            Array.Clear(copy, (int)entry, 8);

            Assert.Empty(new PeImage(copy).ExportedNames());
            Assert.Equal(35, Regex.Matches(Latin1(copy), "vosk_[a-z_0-9]+")
                .Select(m => m.Value).Distinct().Count());
        }

        /// <summary>A name is only an export if its ordinal leads to an
        /// address. One that points past the address table is a broken image,
        /// and it is refused, not counted.</summary>
        [Fact]
        public void ANameWhoseOrdinalLeadsNowhereIsRefused()
        {
            byte[] copy = NativeLibrary();
            uint ordinals = new PeImage(copy).ExportOrdinalTableFileOffset();
            copy[ordinals] = 0xFF;
            copy[ordinals + 1] = 0xFF;

            Assert.Throws<InvalidDataException>(() => new PeImage(copy).ExportedNames());
        }

        /// <summary>An ordinal inside the table can still lead to an address
        /// that is in no section, and an address like that is no export.</summary>
        [Fact]
        public void AnExportWhoseAddressIsInNoSectionIsRefused()
        {
            byte[] copy = NativeLibrary();
            uint addresses = new PeImage(copy).ExportAddressTableFileOffset();
            for (int i = 0; i < 4; i++) copy[addresses + i] = 0xFF;

            Assert.Throws<InvalidDataException>(() => new PeImage(copy).ExportedNames());
        }

        /// <summary>
        /// A section header can be forged so that adding its raw offset to an
        /// RVA wraps around 32 bits and lands back on real bytes. The forgery
        /// here is aimed: the section that holds the export table is moved
        /// down by D in memory and its raw offset set to 2^32 + raw - D, so
        /// in 32 bits every one of its RVAs still maps to the byte it always
        /// did, and a reader that adds in 32 bits returns all 35 names from
        /// a header no loader would accept. Added up without wrapping, the
        /// offset is past the end of the file.
        /// </summary>
        [Fact]
        public void ASectionWhoseRawOffsetWrapsBackOntoTheTableIsRefused()
        {
            byte[] copy = NativeLibrary();
            var image = new PeImage(copy);
            uint exportRva = BitConverter.ToUInt32(copy, (int)image.DataDirectoryFileOffset(0));

            int header = -1;
            uint rva = 0, rawSize = 0, raw = 0;
            for (int s = 0; s < image.SectionCount; s++)
            {
                int h = (int)image.SectionHeaderFileOffset(s);
                uint sRva = BitConverter.ToUInt32(copy, h + 12), sRawSize = BitConverter.ToUInt32(copy, h + 16);
                if (exportRva < sRva || exportRva - sRva >= sRawSize) continue;
                header = h; rva = sRva; rawSize = sRawSize; raw = BitConverter.ToUInt32(copy, h + 20);
            }
            Assert.True(header >= 0, "no section holds the export directory");

            uint d = raw + 1;
            Assert.True(rva >= d, "this image leaves no room to move the section down");
            uint forgedRva = rva - d;
            uint forgedRaw = unchecked((uint)(0x1_0000_0000UL + raw - d));
            BitConverter.GetBytes(forgedRva).CopyTo(copy, header + 12);
            BitConverter.GetBytes(rawSize + d).CopyTo(copy, header + 16);
            BitConverter.GetBytes(forgedRaw).CopyTo(copy, header + 20);

            // What the forgery is for: in 32 bits the table is exactly where
            // it was, so nothing but the arithmetic stands in the way.
            uint whereItWas = raw + (exportRva - rva);
            Assert.Equal(whereItWas, unchecked(forgedRaw + (exportRva - forgedRva)));

            Assert.Throws<InvalidDataException>(() => new PeImage(copy).ExportedNames());
        }

        /// <summary>The import table ends at a descriptor of zeros, and that
        /// descriptor has to come before the directory's own size runs out.
        /// A directory cut down to one descriptor has no end.</summary>
        [Fact]
        public void AnImportTableThatDoesNotEndInsideItsDirectoryIsRefused()
        {
            byte[] copy = NativeLibrary();
            uint entry = new PeImage(copy).DataDirectoryFileOffset(1);
            BitConverter.GetBytes(20u).CopyTo(copy, (int)entry + 4);

            Assert.Throws<InvalidDataException>(() => new PeImage(copy).ImportedDlls());
        }

        /// <summary>The companion check below asserts there is no
        /// delay-import table, which is only worth asserting if one would be
        /// seen.</summary>
        [Fact]
        public void ADelayImportTableIsSeen()
        {
            byte[] copy = NativeLibrary();
            uint entry = new PeImage(copy).DataDirectoryFileOffset(13);
            BitConverter.GetBytes(0x1000u).CopyTo(copy, (int)entry);
            BitConverter.GetBytes(0x40u).CopyTo(copy, (int)entry + 4);

            Assert.False(new PeImage(NativeLibrary()).HasDelayImports);
            Assert.True(new PeImage(copy).HasDelayImports);
        }

        /// <summary>The x64 libvosk.dll needs three MinGW runtime DLLs beside
        /// it. This one links its C++ runtime statically, so it may import
        /// KERNEL32 and the Universal C Runtime that is part of Windows, and
        /// nothing else. A rebuild without -static imports libc++.dll and
        /// libunwind.dll, which the ARM64 build does not carry, and the
        /// recognizer would fail to load on every ARM64 machine. A DLL named
        /// for delay loading is a companion too, and the import table does
        /// not show it, so there must be no delay-import table at all.</summary>
        [Fact]
        public void ItNeedsNoCompanionDll()
        {
            var image = new PeImage(NativeLibrary());
            var named = image.ImportedDlls().Select(n => n.ToLowerInvariant()).Distinct().ToList();

            Assert.Contains("kernel32.dll", named);
            var unexpected = named.Where(n => n != "kernel32.dll"
                                           && !n.StartsWith("api-ms-win-crt-", StringComparison.Ordinal)).ToList();
            Assert.Empty(unexpected);
            Assert.False(image.HasDelayImports, "libvosk.dll has a delay-import table, and nothing here reads it");
        }
    }
}

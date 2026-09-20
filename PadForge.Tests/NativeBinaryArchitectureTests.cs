using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// ARM64 (4.5.1, preliminary). Every native binary PadForge bundles sits
    /// in a folder named for its architecture, and the build picks the folder
    /// by name. Nothing checks that the file inside is what the folder says.
    ///
    /// <para>Microsoft's own ARM64 redist folder carries a
    /// vcruntime140_1.dll that is an x64 image, put there for emulated x64
    /// code, and copying that folder wholesale put an x64 DLL into the ARM64
    /// build on the first attempt. The process would have carried a file
    /// nothing in it could load, and only reading the PE header of the
    /// output caught it.</para>
    ///
    /// <para>So the header is read here for every bundled image, and the
    /// machine field has to match the folder. It runs on any bench, because
    /// it reads bytes rather than loading anything.</para>
    /// </summary>
    public class NativeBinaryArchitectureTests
    {
        private const ushort MachineX64 = 0x8664;
        private const ushort MachineArm64 = 0xAA64;

        public static IEnumerable<object[]> BundledImages()
        {
            string res = Path.Combine(Root(), "PadForge.App", "Resources");
            foreach (string file in Directory.EnumerateFiles(res, "*.*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext != ".dll" && ext != ".sys") continue;
                string folder = Path.GetFileName(Path.GetDirectoryName(file));
                ushort? want =
                    folder.Equals("x64", StringComparison.OrdinalIgnoreCase) ? MachineX64
                    : folder.Equals("arm64", StringComparison.OrdinalIgnoreCase) ? MachineArm64
                    : (ushort?)null;
                // A binary outside an architecture folder makes no claim to
                // check (HIDMaestro.Core.dll is one AnyCPU assembly by design).
                if (want == null) continue;
                yield return new object[] { Path.GetRelativePath(res, file), want.Value };
            }
        }

        [Theory]
        [MemberData(nameof(BundledImages))]
        public void TheImageIsTheArchitectureItsFolderSays(string relativePath, ushort expectedMachine)
        {
            string path = Path.Combine(Root(), "PadForge.App", "Resources", relativePath);
            Assert.Equal(Name(expectedMachine), Name(ReadMachine(path)));
        }

        /// <summary>The walk above proves nothing if it finds nothing, and a
        /// renamed folder would make it find nothing and pass. Both
        /// architectures have to be represented for the check to mean
        /// anything.</summary>
        [Fact]
        public void BothArchitecturesAreActuallyCovered()
        {
            var all = BundledImages().Select(o => (ushort)o[1]).ToList();
            Assert.Contains(MachineX64, all);
            Assert.Contains(MachineArm64, all);
        }

        /// <summary>The reader has to be able to tell the two apart, or the
        /// theory above would pass on anything. The x64 and ARM64 copies of
        /// the same driver are the cleanest pair to show it on.</summary>
        [Fact]
        public void TheReaderSeparatesX64FromArm64()
        {
            string dir = Path.Combine(Root(), "PadForge.App", "Resources", "BthPS3", "BthPS3");
            Assert.Equal(MachineX64, ReadMachine(Path.Combine(dir, "x64", "BthPS3.sys")));
            Assert.Equal(MachineArm64, ReadMachine(Path.Combine(dir, "ARM64", "BthPS3.sys")));
        }

        /// <summary>IMAGE_FILE_HEADER.Machine, reached through e_lfanew at
        /// 0x3C. Four bytes of "PE\0\0" sit between that offset and the
        /// field.</summary>
        private static ushort ReadMachine(string path)
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            Assert.Equal(0x5A4D, br.ReadUInt16());   // "MZ"
            fs.Position = 0x3C;
            int pe = br.ReadInt32();
            fs.Position = pe;
            Assert.Equal(0x00004550u, br.ReadUInt32()); // "PE\0\0"
            return br.ReadUInt16();
        }

        private static string Name(ushort machine) => machine switch
        {
            MachineX64 => "x64",
            MachineArm64 => "ARM64",
            0x014C => "x86",
            _ => "0x" + machine.ToString("X4"),
        };

        private static string Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// SDL loads libusb at run time by a file name that is compiled into
    /// SDL3.dll, and PadForge bundles a file for it to find. The two have to
    /// agree, and nothing else checks that they do.
    ///
    /// <para>They stopped agreeing in 4.5.0. The fork's build reads the name
    /// off the libusb import library with dumpbin. A Visual Studio update
    /// removed the dumpbin its build cache pointed at, the lookup failed with
    /// a warning, and the fallback was the import library's own file name.
    /// Seven consecutive x64 deliveries, September 10 to 15, 2026, asked
    /// Windows for libusb-1.0.lib. PadForge bundles libusb-1.0.dll, so libusb
    /// never loaded. The fork's Switch 2 driver starts a wired Switch 2 Pro,
    /// Joy-Con 2 or Switch 2 GameCube controller with a command sequence sent
    /// through libusb and returns false without it, so none of them worked,
    /// and the GameCube adapter, which SDL reaches through libusb alone, was
    /// never seen. The DLL loaded, exported everything and passed every other
    /// check.</para>
    ///
    /// <para>The fork now stops its configure on a name that is not a DLL.
    /// This is the same contract checked from PadForge's side, on the
    /// delivered bytes, for every architecture: each libusb name inside a
    /// bundled SDL3.dll must be a file bundled beside it.</para>
    /// </summary>
    public class BundledSdlLibusbNameTests
    {
        private static readonly Regex LibusbName = new Regex(@"libusb-1\.0\.[a-z]{1,8}", RegexOptions.CultureInvariant);

        public static IEnumerable<object[]> BundledSdlFolders()
        {
            string sdl = Path.Combine(Root(), "PadForge.App", "Resources", "SDL3");
            foreach (string dir in Directory.EnumerateDirectories(sdl))
            {
                if (File.Exists(Path.Combine(dir, "SDL3.dll")))
                    yield return new object[] { Path.GetFileName(dir) };
            }
        }

        [Theory]
        [MemberData(nameof(BundledSdlFolders))]
        public void TheLibusbNameInsideSdlIsAFileBundledBesideIt(string arch)
        {
            string dir = Path.Combine(Root(), "PadForge.App", "Resources", "SDL3", arch);
            var names = NamesIn(Path.Combine(dir, "SDL3.dll"));

            // No name at all would mean the fork stopped loading libusb
            // dynamically, and this check would then pass on nothing.
            Assert.NotEmpty(names);
            foreach (string name in names)
                Assert.True(File.Exists(Path.Combine(dir, name)),
                    $"SDL3.dll ({arch}) loads \"{name}\" at run time and no file by that name is bundled in {dir}.");
        }

        /// <summary>Both builds have to be present for the theory above to
        /// cover both, and a renamed folder would silently drop one.</summary>
        [Fact]
        public void BothArchitecturesAreChecked()
        {
            var folders = BundledSdlFolders().Select(o => ((string)o[0]).ToLowerInvariant()).ToList();
            Assert.Contains("x64", folders);
            Assert.Contains("arm64", folders);
        }

        /// <summary>The scanner has to be able to see the broken name, or the
        /// theory would pass on a DLL that carries it. The 4.5.0 string is
        /// fed to it here between bytes that are not part of a name.</summary>
        [Fact]
        public void TheScannerSeesTheNameThatShippedBroken()
        {
            byte[] image = Encoding.ASCII.GetBytes("\0\0SDL_LIBUSB\0libusb-1.0.lib\0\0hid\0");
            Assert.Equal(new[] { "libusb-1.0.lib" }, NamesIn(image));
        }

        private static List<string> NamesIn(string path) => NamesIn(File.ReadAllBytes(path));

        private static List<string> NamesIn(byte[] image)
        {
            // Latin-1 maps every byte to one char, so offsets and ASCII text
            // survive and no byte sequence can throw.
            string text = Encoding.Latin1.GetString(image);
            return LibusbName.Matches(text).Select(m => m.Value).Distinct().ToList();
        }

        private static string Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }
    }
}

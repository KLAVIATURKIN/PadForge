using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SDL3;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// PadForge sets SDL's XInput hint so XInput controllers enumerate, and a
    /// hint is matched by its exact name. The constant named
    /// "SDL_JOYSTICK_XINPUT", which is SDL's build macro and no hint, so the
    /// call set nothing and XInput stayed on only because SDL_XINPUT_ENABLED
    /// defaults to 1. This pins the constant to a name the bundled SDL3.dll
    /// actually reads, for every architecture PadForge ships.
    /// </summary>
    public class SdlXInputHintNameTests
    {
        private static string Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        public static IEnumerable<object[]> BundledSdlFolders()
        {
            string sdl = Path.Combine(Root(), "PadForge.App", "Resources", "SDL3");
            foreach (string dir in Directory.EnumerateDirectories(sdl))
                if (File.Exists(Path.Combine(dir, "SDL3.dll")))
                    yield return new object[] { Path.GetFileName(dir) };
        }

        /// <summary>True when the DLL holds the name as a whole NUL-terminated
        /// ASCII string: a longer name that starts or ends with it does not
        /// count.</summary>
        private static bool HoldsName(byte[] dll, string name)
        {
            byte[] needle = Encoding.ASCII.GetBytes(name + "\0");
            for (int i = 1; i + needle.Length <= dll.Length; i++)
            {
                byte before = dll[i - 1];
                if (before == (byte)'_' || char.IsAsciiLetterOrDigit((char)before)) continue;
                bool match = true;
                for (int k = 0; k < needle.Length && match; k++)
                    match = dll[i + k] == needle[k];
                if (match) return true;
            }
            return false;
        }

        [Theory]
        [MemberData(nameof(BundledSdlFolders))]
        public void TheXInputHintIsANameTheBundledSdlReads(string arch)
        {
            byte[] dll = File.ReadAllBytes(Path.Combine(Root(), "PadForge.App", "Resources", "SDL3", arch, "SDL3.dll"));
            // Positive control: a hint PadForge sets that SDL certainly reads.
            Assert.True(HoldsName(dll, SDL.SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS));
            Assert.True(HoldsName(dll, SDL.SDL_HINT_XINPUT_ENABLED),
                $"SDL3.dll ({arch}) holds no \"{SDL.SDL_HINT_XINPUT_ENABLED}\" string.");
            // The scanner matches whole names only.
            Assert.False(HoldsName(dll, "SDL_JOYSTICK_XINPUT"));
        }
    }
}

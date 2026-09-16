using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Switch 2 power-off chord targets ONE pad. The driver stamps each
    /// joystick's serial as its twelve-hex-digit Bluetooth address, so the
    /// record already carries the address the sweep needs, and a chord meant
    /// for one pad used to power off every connected Switch 2 in the room.
    /// </summary>
    public class Switch2ShutdownTargetTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static string Helper() => File.ReadAllText(Path.Combine(
            RepoRoot(), "PadForge.App", "Common", "Input", "BluetoothLinkHelper.cs"));

        /// <summary>The sweep takes the caller's serial and filters on the
        /// device's own address. Without the filter every connected pad of the
        /// family got the shutdown.</summary>
        [Fact]
        public void TheSweepTargetsTheAddressTheCallerNamed()
        {
            string src = Helper();

            Assert.Contains("private static bool TrySwitch2PowerOff(string serial)", src);
            Assert.Contains("TrySwitch2PowerOffAsync(string serial)", src);
            Assert.Contains("if (TrySwitch2PowerOff(serial))", src);

            var filter = Regex.Match(src,
                @"if \(wantAddress != 0 && dev\.BluetoothAddress != wantAddress\)\s*\r?\n\s*continue;");
            Assert.True(filter.Success, "the sweep no longer filters on the caller's address");
        }

        /// <summary>A caller with no serial still sweeps the family, which is
        /// what the all-devices target mode asks for. That is the only reason
        /// the filter is conditional.</summary>
        [Fact]
        public void AnAbsentSerialStillSweepsTheFamily()
        {
            string src = Helper();
            Assert.Contains("ulong wantAddress = 0;", src);
            Assert.Contains("if (!string.IsNullOrEmpty(serial))", src);
        }

        /// <summary>The address format is the driver's, not an invention: it
        /// writes twelve lowercase hex digits, so the parse takes exactly that
        /// width and rejects anything else back to a family sweep.</summary>
        [Fact]
        public void TheAddressIsParsedInTheDriversOwnFormat()
        {
            string src = Helper();
            Assert.Contains("hex.Length == 12", src);
            Assert.Contains("System.Globalization.NumberStyles.HexNumber", src);
            // Separators a settings file or a log may carry are tolerated.
            Assert.Contains("serial.Replace(\":\", \"\").Replace(\"-\", \"\").Trim()", src);
        }

        /// <summary>The doc says where the format comes from, so the next
        /// reader does not have to rediscover it.</summary>
        [Fact]
        public void TheDocNamesTheDriverSourceForTheFormat()
        {
            string src = Helper();
            Assert.Contains("SDL_ble_switch2joystick.c", src);
            Assert.Contains("bluetooth_address", src);
            // And the old family-wide claim is gone.
            Assert.DoesNotContain("Targeting is family-wide", src);
        }
    }
}

using System;
using System.IO;
using System.Linq;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The test host refuses the shared HID helpers, each helper asks before
    /// it opens anything, and nothing in the app turns the refusal on.
    /// </summary>
    public class HidDevicesOffLimitsTests
    {
        private static string Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static string Input(string file)
            => File.ReadAllText(Path.Combine(Root(), "PadForge.App", "Common", "Input", file));

        [Fact]
        public void TheTestHostRefusesRealHidDevices()
        {
            Assert.True(RealHidAccess.Refused);
            const string keyboard = @"\\?\hid#vid_048d&pid_c193&mi_01&col05#padforge-test#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            Assert.Null(HidReportChannel.Open(keyboard));
            Assert.Null(HidReportChannel.OpenFeatureOnly(keyboard));
            Assert.False(HidReportChannel.SetFeatureOnce(keyboard, new byte[] { 0x06, 0x01 }, 2000));
            Assert.False(HidReportChannel.WriteOutputOnce(keyboard, new byte[] { 0x00, 0x05 }, 2000));
            Assert.False(RawHidOutput.Write(keyboard, new byte[] { 0x00, 0x05 }));
            Assert.False(RawHidOutput.WriteOnce(keyboard, new byte[] { 0x00, 0x05 }));
            Assert.False(RawHidOutput.SetFeature(keyboard, new byte[] { 0x06, 0x01 }, stampSonyBtCrc: false));
            using var reader = new VendorHidReader(new VendorHidCollection { Path = keyboard });
            Assert.False(reader.Open());
        }

        [Fact]
        public void AnAllowScopeLiftsTheRefusal_AndPutsItBack()
        {
            using (RealHidAccess.AllowForTest())
            {
                Assert.False(RealHidAccess.Refused);
                using (RealHidAccess.AllowForTest())
                    Assert.False(RealHidAccess.Refused);
                Assert.False(RealHidAccess.Refused);
            }
            Assert.True(RealHidAccess.Refused);
        }

        /// <summary>Each helper asks before its first CreateFile and before
        /// it caches anything for the path, so a refusal opens no handle.</summary>
        [Theory]
        [InlineData("HidReportChannel.cs", "private static HidReportChannel Open(string path, bool featureOnly)")]
        [InlineData("RawHidOutput.cs", "public static bool Write(string devicePath, byte[] buf)")]
        [InlineData("RawHidOutput.cs", "public static bool WriteOnce(string devicePath, byte[] buf, int timeoutMs = 1000)")]
        [InlineData("RawHidOutput.cs", "public static bool SetFeature(string devicePath, byte[] buf, bool stampSonyBtCrc)")]
        [InlineData("VendorHidRuntime.cs", "public bool Open()")]
        public void EachHelperAsksBeforeItOpens(string file, string signature)
        {
            string source = Input(file);
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, signature);
            Assert.Equal(-1, source.IndexOf(signature, at + 1, StringComparison.Ordinal));
            int asks = source.IndexOf("RealHidAccess.Refused", at, StringComparison.Ordinal);
            Assert.True(asks > at, signature + " never asks");
            string before = source.Substring(at + signature.Length, asks - at - signature.Length);
            Assert.DoesNotContain("CreateFile", before);
            Assert.DoesNotContain("GetOrAdd", before);
            // The ask sits in this method, not in one further down the file.
            Assert.DoesNotContain(" static ", before);
            Assert.DoesNotContain("public ", before);
            Assert.DoesNotContain("private ", before);
        }

        [Fact]
        public void NothingInTheAppTurnsTheRefusalOn()
        {
            string root = Root();
            char slash = Path.DirectorySeparatorChar;
            var callers = new[] { "PadForge.App", "PadForge.Engine" }
                .SelectMany(dir => Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
                .Where(path => !path.Contains(slash + "obj" + slash) && !path.Contains(slash + "bin" + slash))
                .Where(path => File.ReadAllText(path).Contains("SetOffLimitsForTest", StringComparison.Ordinal))
                .Select(Path.GetFileName)
                .ToList();
            Assert.Equal(new[] { "RealHidAccess.cs" }, callers);
        }
    }
}

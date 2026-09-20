using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// ARM64 (4.5.1, preliminary). Three features have a native half that
    /// does not exist for ARM64, and which architecture decides each one is
    /// the part that is easy to get wrong.
    ///
    /// <para>A kernel driver follows the MACHINE, because a kernel driver
    /// cannot run emulated. An in-process library follows the PROCESS,
    /// because an x64 DLL loads into an emulated x64 process on an ARM64
    /// machine and cannot load into a native ARM64 one. So the x64 build on
    /// an ARM64 machine loses HidHide alone, and the ARM64 build loses all
    /// three. Each rule is a pure function of the architecture it is handed,
    /// so both halves are pinned here on a bench that only has x64.</para>
    /// </summary>
    public class PlatformSupportTests
    {
        // x64 is the one architecture every native half was built for, so it
        // is the one that answers true. The rules first read "anything but
        // ARM64", which answered true for x86, where the x64 installer and
        // the x64 libraries are as unusable as they are on ARM64.
        [Theory]
        [InlineData(Architecture.X64, true)]
        [InlineData(Architecture.X86, false)]
        [InlineData(Architecture.Arm, false)]
        [InlineData(Architecture.Arm64, false)]
        public void HidHideFollowsTheMachine(Architecture machine, bool expected)
            => Assert.Equal(expected, PlatformSupport.HidHideAvailableOn(machine));

        [Theory]
        [InlineData(Architecture.X64, true)]
        [InlineData(Architecture.X86, false)]
        [InlineData(Architecture.Arm, false)]
        [InlineData(Architecture.Arm64, false)]
        public void VoskFollowsTheProcess(Architecture process, bool expected)
            => Assert.Equal(expected, PlatformSupport.VoskAvailableOn(process));

        [Theory]
        [InlineData(Architecture.X64, true)]
        [InlineData(Architecture.X86, false)]
        [InlineData(Architecture.Arm, false)]
        [InlineData(Architecture.Arm64, false)]
        public void SensaFollowsTheProcess(Architecture process, bool expected)
            => Assert.Equal(expected, PlatformSupport.SensaAvailableOn(process));

        /// <summary>The case the two-architecture split exists for: the x64
        /// build running emulated on ARM64 Windows. Its process is x64 and
        /// its machine is ARM64, so the in-process libraries still load and
        /// only the kernel driver is out of reach. Collapsing the two
        /// questions into one "is this ARM64" would either take voice and
        /// Sensa away from a build that can run them, or offer a HidHide
        /// install that can only fail.</summary>
        [Fact]
        public void TheX64BuildOnAnArm64MachineLosesOnlyTheKernelDriver()
        {
            Architecture machine = Architecture.Arm64, process = Architecture.X64;
            Assert.False(PlatformSupport.HidHideAvailableOn(machine));
            Assert.True(PlatformSupport.VoskAvailableOn(process));
            Assert.True(PlatformSupport.SensaAvailableOn(process));
        }

        /// <summary>The live properties answer for this process on this
        /// machine, and they have to agree with the pure rules they wrap.
        /// Nothing here assumes which architecture the suite runs on.</summary>
        [Fact]
        public void TheLivePropertiesAgreeWithTheRules()
        {
            Assert.Equal(PlatformSupport.HidHideAvailableOn(RuntimeInformation.OSArchitecture),
                         PlatformSupport.HidHideAvailable);
            Assert.Equal(PlatformSupport.VoskAvailableOn(RuntimeInformation.ProcessArchitecture),
                         PlatformSupport.VoskAvailable);
            Assert.Equal(PlatformSupport.SensaAvailableOn(RuntimeInformation.ProcessArchitecture),
                         PlatformSupport.SensaAvailable);
            Assert.Equal(RuntimeInformation.OSArchitecture == Architecture.Arm64,
                         PlatformSupport.IsArm64Machine);
        }

        /// <summary>The test above cannot see the mistake this class exists
        /// to prevent. On an x64 bench the machine and the process are both
        /// x64, so a live property wired to the WRONG one of the two returns
        /// the same answer and every assertion still holds. Only an ARM64
        /// machine running the x64 build tells them apart, and the bench has
        /// none. So the wiring is pinned in the source: each live property
        /// has to hand its rule the architecture that rule is about.</summary>
        [Theory]
        [InlineData("MachineArchitecture", "RuntimeInformation.OSArchitecture")]
        [InlineData("ProcessArchitecture", "RuntimeInformation.ProcessArchitecture")]
        [InlineData("IsArm64Machine", "MachineArchitecture == Architecture.Arm64")]
        [InlineData("HidHideAvailable", "HidHideAvailableOn(MachineArchitecture)")]
        [InlineData("VoskAvailable", "VoskAvailableOn(ProcessArchitecture)")]
        [InlineData("SensaAvailable", "SensaAvailableOn(ProcessArchitecture)")]
        public void EachLivePropertyAsksTheArchitectureItsRuleIsAbout(string property, string expression)
        {
            Assert.Equal(expression, LivePropertyBody(PlatformSupportSource(), property));
        }

        /// <summary>The pin is worthless unless it can tell the two
        /// architectures apart, so it is shown the swap it guards against.</summary>
        [Fact]
        public void ThePinSeesAMachineProcessSwap()
        {
            string swapped = PlatformSupportSource().Replace(
                "HidHideAvailableOn(MachineArchitecture)", "HidHideAvailableOn(ProcessArchitecture)");
            Assert.Equal("HidHideAvailableOn(ProcessArchitecture)", LivePropertyBody(swapped, "HidHideAvailable"));
        }

        private static string PlatformSupportSource()
            => File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(
                Path.Combine("PadForge.Engine", "Common", "PlatformSupport.cs")));

        /// <summary>The expression body of one static property, read from the
        /// declaration itself so a mention in a comment cannot satisfy it.</summary>
        private static string LivePropertyBody(string source, string property)
        {
            var m = Regex.Match(source,
                @"^\s*public static \w+ " + Regex.Escape(property) + @"\s*=>\s*(.+?);\s*$",
                RegexOptions.Multiline);
            Assert.True(m.Success, property + " is no longer an expression-bodied static property");
            return m.Groups[1].Value.Trim();
        }
    }
}

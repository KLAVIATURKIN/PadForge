using System.Runtime.InteropServices;
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
        [Theory]
        [InlineData(Architecture.X64, true)]
        [InlineData(Architecture.X86, true)]
        [InlineData(Architecture.Arm64, false)]
        public void HidHideFollowsTheMachine(Architecture machine, bool expected)
            => Assert.Equal(expected, PlatformSupport.HidHideAvailableOn(machine));

        [Theory]
        [InlineData(Architecture.X64, true)]
        [InlineData(Architecture.Arm64, false)]
        public void VoskFollowsTheProcess(Architecture process, bool expected)
            => Assert.Equal(expected, PlatformSupport.VoskAvailableOn(process));

        [Theory]
        [InlineData(Architecture.X64, true)]
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
    }
}

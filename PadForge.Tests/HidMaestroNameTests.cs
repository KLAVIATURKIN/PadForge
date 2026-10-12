using System;
using System.IO;
using System.Linq;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The instance ids that name HIDMaestro by themselves
    /// (<see cref="HidMaestroAncestry.NamesHidMaestro"/>), and the wiring that
    /// hands an XInput pad's identity the persona scope Step 1 and the hiding
    /// side ask. Neither had a test of its own: every tree the other tests
    /// walk also carries a HIDMaestro hardware id, so a name the rule stopped
    /// reading changed no answer there.
    /// </summary>
    public class HidMaestroNameTests
    {
        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }

        /// <summary>Each of the three names marks an id wherever it sits in
        /// it and in either case, and so does a root-enumerated id with an
        /// IG_ or XI_ part. The same parts on a HID or USB id mark nothing:
        /// a real XInput pad's HID node carries IG_ too.</summary>
        [Theory]
        [InlineData(@"HID\HIDMAESTRO\1&2&3", true)]
        [InlineData(@"hid\hidmaestro\1&2&3", true)]
        [InlineData(@"ROOT\HMCOMPANION\0000", true)]
        [InlineData(@"root\hmcompanion\0000", true)]
        [InlineData(@"ROOT\HMXINPUT\0000", true)]
        [InlineData(@"root\hmxinput\0001", true)]
        [InlineData(@"ROOT\VID_045E&PID_028E&IG_00\0000", true)]
        [InlineData(@"ROOT\VID_045E&PID_0B13&XI_00\0000", true)]
        [InlineData(@"root\vid_045e&pid_028e&ig_00\0000", true)]
        [InlineData(@"ROOT\VID_045E&PID_028E\0000", false)]
        [InlineData(@"HID\VID_045E&PID_028E&IG_00\7&1A2B3C4D&0&0000", false)]
        [InlineData(@"USB\VID_045E&PID_028E&XI_00\6&2E4F1A2B&0&3", false)]
        [InlineData(@"USB\VID_045E&PID_028E\6&2E4F1A2B&0&3", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void AnInstanceIdNamesHidMaestroByTheseMarksAlone(string instanceId, bool expected)
            => Assert.Equal(expected, HidMaestroAncestry.NamesHidMaestro(instanceId));

        /// <summary>A node whose own id names HIDMaestro is left out of the
        /// XInput identity whatever its walk and the scope say, a node that
        /// is not present included.</summary>
        [Theory]
        [InlineData(@"ROOT\HMXINPUT\0000")]
        [InlineData(@"ROOT\HMCOMPANION\0000")]
        [InlineData(@"ROOT\VID_045E&PID_028E&IG_00\0000")]
        public void AnIdThatNamesHidMaestroIsLeftOutOfTheXInputIdentity(string instanceId)
        {
            Assert.True(StableXInputInstance.IsLeftOut(instanceId, () => false, _ => null, _ => false));
            Assert.True(StableXInputInstance.IsHidMaestroInstance(instanceId, () => false, _ => null));
        }

        /// <summary>The identity asks the scope Step 1 and the hiding side
        /// ask. The engine cannot reach the app's rule, so the app hands it
        /// over as the engine starts, and the lookup passes the pad's own
        /// vendor and product id. Without the hand-over a persona under a
        /// host controller with no stamp is read as a real pad of its
        /// model.</summary>
        [Fact]
        public void TheAppHandsTheXInputIdentityThePersonaScope()
        {
            string service = RepoFile("PadForge.App", "Services", "InputService.cs");
            Assert.Contains(
                "PadForge.Engine.StableXInputInstance.PersonaInScope = PadForge.Common.Input.InputManager.ChecksUsbipAncestry;",
                service);

            string identity = RepoFile("PadForge.Engine", "Common", "StableXInputInstance.cs");
            Assert.Contains("() => usbIds && PersonaInScope?.Invoke(vid, pid) == true", identity);
            // Nothing in the engine sets the scope itself.
            Assert.DoesNotContain("PersonaInScope =", identity.Replace("PersonaInScope?.", "PersonaInScope."));
        }
    }
}

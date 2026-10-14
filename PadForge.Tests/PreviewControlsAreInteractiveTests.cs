using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Models2D;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A preview's buttons and outputs are controls as well as displays: a
    /// click on one records its row (owner, 2026-10-14). Two outputs the
    /// HIDMaestro 1.12.0 work drew had no place to click: the virtual
    /// keyboard's lock lights, and the Valve pads' capacitive sensors, whose
    /// stick caps already record the stick button and whose grips have no
    /// art.
    /// </summary>
    public class PreviewControlsAreInteractiveTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        [Theory]
        [InlineData("steam-deck", "LeftStickTouch,RightStickTouch")]
        [InlineData("steam-deck-composite", "LeftStickTouch,RightStickTouch")]
        [InlineData("steam-controller-2", "LeftStickTouch,RightStickTouch,LeftGripTouch,RightGripTouch")]
        [InlineData("steam-controller", "")]
        [InlineData("steam-controller-composite", "")]
        [InlineData("switch-pro", "")]
        public void AValveSlotGetsACapsuleForEachSensorItsWireCarries(string profile, string expected)
        {
            var roles = TouchSensorStrip.RolesFor(VirtualControllerType.Extended, profile);
            Assert.Equal(expected, string.Join(",", roles));
        }

        [Fact]
        public void OnlyAnExtendedSlotShowsTheCapsules()
        {
            foreach (VirtualControllerType type in Enum.GetValues(typeof(VirtualControllerType)))
            {
                if (type == VirtualControllerType.Extended) continue;
                Assert.Empty(TouchSensorStrip.RolesFor(type, "steam-controller-2"));
            }
        }

        /// <summary>A click sends the role, and PadPage turns it into the
        /// slot's raw row (NintendoPreviewMap.ToRaw). While that row records,
        /// the strip reads the role back from it to flash the capsule.</summary>
        [Theory]
        [InlineData("steam-deck")]
        [InlineData("steam-deck-composite")]
        [InlineData("steam-controller-2")]
        public void EveryCapsuleRecordsARowAndFlashesWhileItRecords(string profile)
        {
            foreach (string role in TouchSensorStrip.RolesFor(VirtualControllerType.Extended, profile))
            {
                string raw = NintendoPreviewMap.ToRaw(role, profile);
                Assert.True(raw != null && raw.StartsWith("RawBtn", StringComparison.Ordinal), $"{profile} {role} -> {raw}");
                Assert.Equal(role, TouchSensorStrip.RoleOf(raw, profile));
            }
            // Positive control: a row that is no sensor flashes no capsule.
            Assert.Null(TouchSensorStrip.RoleOf("RawBtn0", profile));
            Assert.Null(TouchSensorStrip.RoleOf(null, profile));
        }

        [Fact]
        public void EveryCapsuleHasItsRowsName()
        {
            foreach (string role in TouchSensorStrip.Roles)
                Assert.NotEqual(role, TouchSensorStrip.LabelOf(role));
        }

        [Fact]
        public void TheLockLightsBelongToTheLockKeysTheSlotCanPress()
        {
            Assert.Equal(new[] { 0x90, 0x14, 0x91 }, KBMPreviewView.LockLightKeys);
            foreach (int vk in KBMPreviewView.LockLightKeys)
                Assert.True(InputManager.IsKbmKeyTarget(vk), $"VK {vk:X2} is no Keyboard + Mouse key target");
        }

        /// <summary>The preview builds its controls in code, and the existing
        /// preview tests read that code (KbmPreviewFlashGuardTests). A light
        /// records its key's row on a click, flashes with that row, and the
        /// per-frame repaint spares the flash.</summary>
        [Fact]
        public void ALockLightRecordsItsKeyAndKeepsItsFlash()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Views", "KBMPreviewView.xaml.cs"));

            int build = src.IndexOf("private void BuildLedStrip(", StringComparison.Ordinal);
            Assert.True(build > 0);
            int end = src.IndexOf("\n        }\n", build, StringComparison.Ordinal);
            if (end < 0) end = src.IndexOf("\r\n        }\r\n", build, StringComparison.Ordinal);
            string body = src.Substring(build, end - build);
            Assert.Contains("string target = $\"KbmKey{vk:X2}\";", body);
            Assert.Matches(new Regex(@"MouseLeftButtonDown \+= \(\w+, e\) =>\s*\{\s*ControllerElementRecordRequested\?\.Invoke\(this, target\);"), body);

            int flash = src.IndexOf("private void ApplyFlashState(", StringComparison.Ordinal);
            Assert.True(flash > 0);
            Assert.Contains("_flashTarget == _ledTargets[i]) _ledDots[i].Fill = highlight ? FlashBrush", src.Substring(flash, 800));

            int repaint = src.IndexOf("_ledDots[i].Fill = on ? AccentBrush", StringComparison.Ordinal);
            Assert.True(repaint > 0);
            string loop = src.Substring(repaint - 400, 400);
            Assert.Contains("if (_flashOn && _ledTargets != null && _flashTarget == _ledTargets[i]) continue;", loop);
        }
    }
}

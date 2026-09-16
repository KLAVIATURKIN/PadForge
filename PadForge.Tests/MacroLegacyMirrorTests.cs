using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Removing a trigger input, or clearing a key, must not leave a hidden
    /// legacy copy still driving the macro.
    ///
    /// <para>Both shapes are the same defect. A modern value and a legacy one
    /// describe the same thing, the modern one is what the editor shows, and
    /// the runtime falls back to the legacy one when the modern is empty. So
    /// clearing what you can see hands control to what you cannot.</para>
    /// </summary>
    public sealed class MacroLegacyMirrorTests
    {
        private static readonly Guid Pad = Guid.NewGuid();

        /// <summary>A legacy macro migrates its raw button into the entry
        /// list on first access. Removing that entry must retire the trigger,
        /// not fall back to the legacy array it came from.</summary>
        [Fact]
        public void RemovingTheMigratedRawButtonRetiresTheTrigger()
        {
            var macro = new MacroItem
            {
                Name = "legacy",
                TriggerDeviceGuid = Pad,
                TriggerRawButtons = new[] { 5 },
            };

            // First access migrates the legacy pair into the entry list.
            var entries = macro.GetTriggerInputEntries();
            Assert.Single(entries);
            Assert.Equal(5, entries[0].RawButton);
            Assert.True(macro.UsesRawTrigger, "positive control: the migrated trigger is live");

            // The user removes the one visible row.
            macro.SetTriggerInputEntries(new List<MacroItem.TriggerInputEntry>());

            Assert.False(macro.UsesRawTrigger,
                "the removed button came back through the legacy mirror");
            Assert.Empty(macro.TriggerRawButtons);
        }

        [Fact]
        public void RemovingTheMigratedPovRetiresTheTrigger()
        {
            var macro = new MacroItem
            {
                Name = "legacy pov",
                TriggerDeviceGuid = Pad,
                TriggerPovs = new[] { "0:0" },
            };

            Assert.Single(macro.GetTriggerInputEntries());
            Assert.True(macro.UsesPovTrigger, "positive control: the migrated POV is live");

            macro.SetTriggerInputEntries(new List<MacroItem.TriggerInputEntry>());

            Assert.False(macro.UsesPovTrigger, "the removed POV came back through the legacy mirror");
            Assert.Empty(macro.TriggerPovs);
        }

        /// <summary>Replacing the list keeps what the caller supplied. The
        /// clear above must not be mistaken for dropping real entries.</summary>
        [Fact]
        public void ReplacingTheListKeepsTheSuppliedEntries()
        {
            var macro = new MacroItem { Name = "replace", TriggerDeviceGuid = Pad, TriggerRawButtons = new[] { 5 } };
            macro.SetTriggerInputEntries(new List<MacroItem.TriggerInputEntry>
            {
                new() { DeviceGuid = Pad, RawButton = 9 },
            });

            Assert.True(macro.UsesRawTrigger);
            Assert.Equal(9, macro.GetTriggerInputEntries().Single().RawButton);
        }

        /// <summary>Clearing the visible key clears the legacy code the parse
        /// falls back to, so the action stops pressing the old key.</summary>
        [Fact]
        public void ClearingTheKeyStringAlsoClearsTheLegacyKeyCode()
        {
            var action = new MacroAction { Type = MacroActionType.KeyPress, KeyCode = 0x41 };
            Assert.Equal(new[] { 0x41 }, action.ParsedKeyCodes);

            action.ClearKeyStringCommand.Execute(null);

            Assert.Equal(0, action.KeyCode);
            Assert.Empty(action.ParsedKeyCodes);
        }
    }
}

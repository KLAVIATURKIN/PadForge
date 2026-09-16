using System;
using System.Linq;
using PadForge.Engine;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Guard pins for the slot view-model findings in the 2026-09-15 audit.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class PadViewModelAuditFixTests
    {
        // C241: two setters clamping against one axis budget.

        /// <summary>The stick and trigger counts share an axis budget and
        /// clamp against each other, so seeding sticks against a leftover
        /// trigger count loses the last stick. A stick-only layout asking for
        /// four sticks landed on three while two triggers were still on the
        /// books.</summary>
        [Fact]
        public void SeedingAStickOnlyLayoutKeepsEveryStick()
        {
            var cfg = new ExtendedSlotConfig();   // defaults carry two triggers
            Assert.True(cfg.TriggerCount > 0, "the default no longer has triggers to collide with");

            cfg.TriggerCount = 0;
            cfg.ThumbstickCount = 4;

            Assert.Equal(4, cfg.ThumbstickCount);
        }

        /// <summary>The collision is real in the other order, which is what
        /// makes the ordering load-bearing rather than cosmetic.</summary>
        [Fact]
        public void TheOppositeOrderReallyDoesLoseAStick()
        {
            var cfg = new ExtendedSlotConfig();
            cfg.TriggerCount = 2;
            cfg.ThumbstickCount = 4;

            Assert.True(cfg.ThumbstickCount < 4,
                "the setters no longer clamp against each other, so the ordering rule is stale");
        }

        /// <summary>Every site that seeds a live Extended layout writes the
        /// counts in that order. The clamp above proves why it matters; this
        /// proves no seeding site forgets it. A new one that writes sticks
        /// first will silently drop a stick on a stick-heavy layout.</summary>
        [Theory]
        [InlineData("PadForge.App", "ViewModels", "PadViewModel.cs")]
        [InlineData("PadForge.App", "Services", "SettingsService.cs")]
        [InlineData("PadForge.App", "Services", "InputService.cs")]
        public void EverySiteThatSeedsALayoutZeroesTriggersFirst(params string[] parts)
        {
            string src = System.IO.File.ReadAllText(RepoPath(parts));

            // Each "write sticks then triggers" run must be preceded by a
            // trigger reset on the same object.
            var m = System.Text.RegularExpressions.Regex.Matches(src,
                @"(?<obj>[A-Za-z_][A-Za-z0-9_]*)\.ThumbstickCount = [^;]+;\s*
?
\s*\k<obj>\.TriggerCount = ");
            Assert.True(m.Count > 0, "no layout seeding site found, so this pin has gone stale");

            foreach (System.Text.RegularExpressions.Match hit in m)
            {
                string obj = hit.Groups["obj"].Value;
                string before = src.Substring(Math.Max(0, hit.Index - 400), Math.Min(400, hit.Index));
                Assert.Contains(obj + ".TriggerCount = 0;", before);
            }
        }

        private static string RepoPath(params string[] parts)
        {
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return System.IO.Path.Combine(d.FullName, System.IO.Path.Combine(parts));
        }

        // C247: two slot-owned configs survived the slot's reset.

        /// <summary>The MIDI and Extended configs persist under the slot
        /// index, not the device, so no load mirror can reach them. A deleted
        /// slot's product string, identifiers, force-feedback flag and whole
        /// MIDI bundle therefore survived, and the next save wrote them under
        /// whatever new slot took that index.</summary>
        [Fact]
        public void ResettingASlotClearsItsSlotOwnedConfigs()
        {
            var vm = new PadViewModel(0);
            vm.ExtendedConfig.ProductString = "Leftover Pad";
            vm.ExtendedConfig.VendorId = 0x1234;
            vm.ExtendedConfig.ProductId = 0x5678;
            vm.MidiConfig.Channel = 7;
            vm.MidiConfig.Velocity = 42;

            vm.ResetAllSettings();

            Assert.NotEqual("Leftover Pad", vm.ExtendedConfig.ProductString);
            Assert.NotEqual(0x1234, vm.ExtendedConfig.VendorId);
            Assert.NotEqual(0x5678, vm.ExtendedConfig.ProductId);
            Assert.NotEqual(7, vm.MidiConfig.Channel);
            Assert.NotEqual(42, vm.MidiConfig.Velocity);
        }

        /// <summary>The reset works in place. Replacing either instance would
        /// drop the autosave subscription the window wires once at startup.</summary>
        [Fact]
        public void TheSlotOwnedConfigsAreResetInPlace()
        {
            var vm = new PadViewModel(0);
            var midi = vm.MidiConfig;
            var ext = vm.ExtendedConfig;

            vm.ResetAllSettings();

            Assert.Same(midi, vm.MidiConfig);
            Assert.Same(ext, vm.ExtendedConfig);
        }

        // C242: a same-instance assignment used to unsubscribe for good.

        /// <summary>Assigning the same config back is not a change, and the
        /// detach ran before the change test, so it unsubscribed and never
        /// resubscribed. The view model went deaf to its own config.</summary>
        [Fact]
        public void ReassigningTheSameExtendedConfigKeepsTheSubscription()
        {
            var vm = new PadViewModel(0);
            var cfg = vm.ExtendedConfig;
            Assert.NotNull(cfg);

            int before = SubscriberCount(cfg);
            Assert.True(before > 0, "the view model was never subscribed to start with");

            vm.ExtendedConfig = cfg;   // same instance

            Assert.Equal(before, SubscriberCount(cfg));
        }

        /// <summary>A genuinely new instance still swaps the subscription, so
        /// the old one is not left attached.</summary>
        [Fact]
        public void ANewExtendedConfigTakesOverTheSubscription()
        {
            var vm = new PadViewModel(0);
            var old = vm.ExtendedConfig;
            var replacement = new ExtendedSlotConfig();

            vm.ExtendedConfig = replacement;
            Assert.Same(replacement, vm.ExtendedConfig);
        }

        // C252: the card reset left the other grammar's pairs in the field.

        /// <summary>Reset All says every pair is removed. Pairs authored under
        /// the other slot grammar are kept verbatim through ordinary edits and
        /// re-appended on publish, so leaving them there meant they came back
        /// as live pairs the moment the slot's grammar matched again.</summary>
        [Fact]
        public void TheSocdCardResetClearsThePreservedPairsToo()
        {
            var vm = new PadViewModel(0) { SocdMode = "Neutral" };

            // A pair authored under the OTHER slot grammar. The editor cannot
            // show it, the publish re-appends it, and it becomes live again
            // the moment the slot's grammar matches.
            var f = typeof(PadViewModel).GetField("_socdPreservedTokens",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(f);
            var kept = (System.Collections.Generic.List<string>)f.GetValue(vm);
            kept.Add("ButtonA:ButtonB");
            Assert.NotEmpty(kept);

            vm.ResetSocdCardCommand.Execute(null);

            Assert.Equal("", vm.SocdMode);
            Assert.Empty(vm.SocdPairItems);
            Assert.Empty((System.Collections.Generic.List<string>)f.GetValue(vm));
        }

        // C253: Clear All left a recorder running over the rows it cleared.

        /// <summary>A Map All walk writes each result into its row when the
        /// recording completes, so one landing after Clear All repopulated a
        /// row the user had just emptied, with nothing on screen to say why.</summary>
        [Fact]
        public void ClearAllMappingsCancelsARunningMapAll()
        {
            var vm = new PadViewModel(0);
            int cancels = 0;
            vm.MapAllCancelRequested += (_, _) => cancels++;
            vm.IsMapAllActive = true;
            vm.CurrentRecordingTarget = "LeftThumbAxisX";

            vm.ClearMappingsCommand.Execute(null);

            Assert.False(vm.IsMapAllActive);
            Assert.Equal(1, cancels);
            Assert.Null(vm.CurrentRecordingTarget);
        }

        /// <summary>Positive control: with nothing recording, Clear All raises
        /// no spurious cancel.</summary>
        [Fact]
        public void ClearAllMappingsWithNoRecordingRaisesNoCancel()
        {
            var vm = new PadViewModel(0);
            int cancels = 0;
            vm.MapAllCancelRequested += (_, _) => cancels++;

            vm.ClearMappingsCommand.Execute(null);

            Assert.Equal(0, cancels);
        }
        /// <summary>How many handlers are attached to a config's change
        /// event. The event is declared on the base observable type, so the
        /// backing field is found by walking up from the concrete type.</summary>
        private static int SubscriberCount(ExtendedSlotConfig cfg)
        {
            for (var t = cfg.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField("PropertyChanged",
                    System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance);
                if (f == null) continue;
                var d = f.GetValue(cfg) as Delegate;
                return d?.GetInvocationList().Length ?? 0;
            }
            throw new InvalidOperationException("no PropertyChanged backing field found");
        }
    }
}

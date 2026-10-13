using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Models2D;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The wire-stamp contract behind TranslateNintendoRawMappings. A slot's
    /// ProfileId property changes for two reasons that need opposite
    /// handling: the user re-targeting the SAME data to a new wire
    /// (translate), and the system re-describing the VM to match data that
    /// is ALREADY on the new wire (launch restore, profile apply, workshop
    /// import: do nothing). Keying the translation on the setter's previous
    /// value mistranslated consistent data on every restore path, so the
    /// "from" side is now SettingsManager's per-slot wire stamp, which those
    /// paths set before assigning the VM.
    /// </summary>
    // Mutates SettingsManager statics, which are process-global; xunit
    // runs collections in parallel, so it must serialize with every other
    // class that touches them.
    [Collection("SettingsManagerStatics")]
    public class NintendoWireStampTests : IDisposable
    {
        private const string S1 = "switch-pro";
        private const string S2 = "switch2-pro-controller";
        private const int Pad = 7;   // stays clear of other fixtures' slot 0

        private readonly SettingsCollection _savedSettings;
        private readonly MappingSet[] _savedSlotSets;
        private static readonly Guid DevGuid = new("aaaaaaaa-bbbb-cccc-dddd-eeeeffff0007");

        public NintendoWireStampTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedSlotSets = SettingsManager.SlotMappingSets;
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.SlotMappingSets = _savedSlotSets;
            SettingsManager.StampNintendoWire(Pad, null);
        }

        /// <summary>Installs a slot whose PadSetting and MappingSet both
        /// carry the given raw targets, and stamps the given wire.</summary>
        private static (PadSetting ps, MappingSet ms) Arrange(
            string stampedWire, params (string Target, string Source)[] rows)
        {
            SettingsManager.UserSettings = new SettingsCollection();
            var ps = new PadSetting();
            foreach (var (t, src) in rows)
                ps.SetRawMapping(t, src);
            ps.FlushRawMappings();
            ps.UpdateChecksum();

            var us = new UserSetting
            {
                InstanceGuid = DevGuid,
                MapTo = Pad,
            };
            us.SetPadSetting(ps);
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(us);

            var ms = new MappingSet();
            foreach (var (t, src) in rows)
                ms.Rows.Add(new MappingRow
                {
                    Target = t,
                    Sources = new List<MappingSource>
                    {
                        new MappingSource { DeviceGuid = DevGuid.ToString(), Descriptor = src },
                    },
                });
            var sets = new MappingSet[InputManager.MaxPads];
            sets[Pad] = ms;
            SettingsManager.SlotMappingSets = sets;

            SettingsManager.StampNintendoWire(Pad, stampedWire);
            return (ps, ms);
        }

        private static Dictionary<string, string> RawOf(PadSetting ps) =>
            (ps.RawMappingEntries ?? Array.Empty<RawMappingEntry>())
                .ToDictionary(e => e.Key, e => e.Value);

        /// <summary>The launch-restore path: the persisted profile is
        /// stamped before the VM assignment, so the setter's translation
        /// call finds from == to and MUST leave S2-persisted data alone.
        /// This is the exact shape that corrupted on every launch when the
        /// translation keyed on the setter's previous (null) value.</summary>
        [Fact]
        public void RestoreWithMatchingStamp_TranslatesNothing()
        {
            var (ps, _) = Arrange(S2,
                ("RawBtn8", "POV 0 Down"),     // S2 D-pad Down
                ("RawBtn14", "Button 6"),      // S2 Minus
                ("RawBtn20", "Button 17"));    // S2 C

            SettingsManager.TranslateNintendoRawMappings(Pad, S2);

            var raw = RawOf(ps);
            Assert.Equal("POV 0 Down", raw["RawBtn8"]);
            Assert.Equal("Button 6", raw["RawBtn14"]);
            Assert.Equal("Button 17", raw["RawBtn20"]);
        }

        /// <summary>An unknown stamp ADOPTS the incoming profile and
        /// translates nothing: the data was persisted with that profile,
        /// and guessing a "from" wire is the corruption this exists to
        /// prevent. The adoption is observable: a LATER user change
        /// translates from the adopted wire.</summary>
        [Fact]
        public void UnknownStamp_AdoptsThenTranslatesFromAdoptedWire()
        {
            var (ps, _) = Arrange(null, ("RawBtn8", "Button 6"));   // S1 Minus

            SettingsManager.TranslateNintendoRawMappings(Pad, S1);   // adopt
            Assert.Equal("Button 6", RawOf(ps)["RawBtn8"]);          // untouched

            SettingsManager.TranslateNintendoRawMappings(Pad, S2);   // user change
            var raw = RawOf(ps);
            Assert.False(raw.ContainsKey("RawBtn8"));
            Assert.Equal("Button 6", raw["RawBtn14"]);               // Minus moved 8 -> 14
        }

        /// <summary>THE launch-corruption scenario: an S2-persisted slot
        /// restoring with no stamp yet. Adopt must win over any guessed
        /// "from": reading this data under the S1 table would prune
        /// RawBtn14-20 as orphans and move RawBtn8 onto RawBtn14,
        /// which is exactly what every launch did before the stamp.</summary>
        [Fact]
        public void UnknownStamp_AdoptsSwitch2DataUntouched()
        {
            var (ps, _) = Arrange(null,
                ("RawBtn8", "POV 0 Down"),     // S2 D-pad Down
                ("RawBtn14", "Button 6"),      // S2 Minus
                ("RawBtn20", "Button 17"));    // S2 C

            SettingsManager.TranslateNintendoRawMappings(Pad, S2);

            var raw = RawOf(ps);
            Assert.Equal("POV 0 Down", raw["RawBtn8"]);
            Assert.Equal("Button 6", raw["RawBtn14"]);
            Assert.Equal("Button 17", raw["RawBtn20"]);
            var ms = SettingsManager.SlotMappingSets[Pad];
            Assert.Equal(3, ms.Rows.Count);
        }

        /// <summary>The live user change: stamp holds the outgoing wire, so
        /// the four owner-reported carry-overs move to their S2 indices in
        /// both stores, and the D-pad crosses from the hat to buttons.</summary>
        [Fact]
        public void UserChange_TranslatesBothStoresByRole()
        {
            var (ps, _) = Arrange(S1,
                ("RawBtn8", "Button 6"),        // Minus
                ("RawBtn12", "Button 10"),      // Home
                ("RawBtn13", "Button 11"),      // Capture
                ("RawBtn10", "Button 8"),       // LS
                ("RawPov0Up", "POV 0 Up"));

            SettingsManager.TranslateNintendoRawMappings(Pad, S2);

            var raw = RawOf(ps);
            Assert.Equal("Button 6", raw["RawBtn14"]);
            Assert.Equal("Button 10", raw["RawBtn16"]);
            Assert.Equal("Button 11", raw["RawBtn17"]);
            Assert.Equal("Button 8", raw["RawBtn15"]);
            Assert.Equal("POV 0 Up", raw["RawBtn11"]);   // hat -> button D-pad
            Assert.False(raw.ContainsKey("RawPov0Up"));

            var ms = SettingsManager.SlotMappingSets[Pad];
            Assert.Contains(ms.Rows, r => r.Target == "RawBtn14");
            Assert.DoesNotContain(ms.Rows, r => r.Target == "RawBtn8");
        }

        /// <summary>The healing path for the shipped artifact damage: a
        /// switch-pro slot carrying orphaned S2 indices (the intermediate
        /// builds' wound, confirmed in the owner's live PadForge.xml) must
        /// come out of a cross-family translation with the orphans PRUNED
        /// and no duplicate targets, not with an orphan RawBtn14 sitting
        /// beside the real Minus arriving at RawBtn14.</summary>
        [Fact]
        public void CrossFamilyTranslation_PrunesOrphansWithoutDuplicates()
        {
            var (ps, _) = Arrange(S1,
                ("RawBtn8", "Button 6"),        // real S1 Minus
                ("RawBtn14", "Button 99"),      // orphan: S1 role-maps 0-13 only
                ("RawBtn16", "Button 98"));     // orphan

            SettingsManager.TranslateNintendoRawMappings(Pad, S2);

            var raw = RawOf(ps);
            Assert.Equal("Button 6", raw["RawBtn14"]);   // the real Minus move won
            Assert.False(raw.ContainsKey("RawBtn16"));   // orphan pruned, not carried

            var ms = SettingsManager.SlotMappingSets[Pad];
            var dup = ms.Rows.GroupBy(r => r.Target).Where(g => g.Count() > 1).ToList();
            Assert.Empty(dup);
            Assert.Single(ms.Rows, r => r.Target == "RawBtn14");
        }

        /// <summary>Same wire family on both sides (the type-switch shape:
        /// an Xbox slug stamped by the automap, then the Nintendo default
        /// assigned) updates the stamp and touches nothing. Observable the
        /// same way as adoption: the NEXT change translates from S1.</summary>
        [Fact]
        public void SameFamilyChange_AdoptsWithoutTouchingData()
        {
            var (ps, _) = Arrange("xbox-series-x", ("RawBtn8", "Button 6"));

            SettingsManager.TranslateNintendoRawMappings(Pad, S1);
            Assert.Equal("Button 6", RawOf(ps)["RawBtn8"]);   // untouched

            SettingsManager.TranslateNintendoRawMappings(Pad, S2);
            Assert.Equal("Button 6", RawOf(ps)["RawBtn14"]);  // stamp was S1, so 8 -> 14
        }

        /// <summary>A raw slot's SOCD pairs name their buttons by wire index
        /// (SlotButtonSocd), so they move with the rows. Minus and Plus are
        /// buttons on both Switch wires, at other indices, and their pair
        /// lands on them. The Switch 2 Pro's D-pad is four buttons and the
        /// Switch Pro's is a hat, which no pair can name, so a pair goes
        /// whether both its buttons are lost or one of them.</summary>
        [Fact]
        public void ALiveRetarget_MovesTheSocdPairsByRole()
        {
            var (_, ms) = Arrange(S2, ("RawBtn14", "Button 6"));
            static int At(string wire, string role) => NintendoPreviewMap.IndexOf(wire, role);
            Assert.Equal(14, At(S2, "ButtonBack"));
            Assert.Equal(8, At(S1, "ButtonBack"));
            Assert.True(At(S2, "DPadLeft") >= 0 && At(S1, "DPadLeft") < 0);
            Assert.True(At(S2, "ButtonA") >= 0 && At(S1, "ButtonA") >= 0);
            ms.SocdMode = "LastWins";
            ms.SocdPairs = $"{At(S2, "ButtonBack")}:{At(S2, "ButtonStart")}|{At(S2, "DPadLeft")}:{At(S2, "DPadRight")}"
                + $"|{At(S2, "ButtonA")}:{At(S2, "DPadUp")}|{At(S2, "DPadDown")}:{At(S2, "ButtonB")}";

            SettingsManager.TranslateNintendoRawMappings(Pad, S1);

            Assert.Equal($"{At(S1, "ButtonBack")}:{At(S1, "ButtonStart")}", ms.SocdPairs);
            Assert.Equal("LastWins", ms.SocdMode);
        }

        /// <summary>A device's deadzone and Bidirectional flag for a raw
        /// mapping are kept under the mapping's raw target
        /// (PadSetting.GetMappingDeadZone), so they move with it: ZL is raw
        /// button 6 on a Switch Pro and 13 on a Switch 2 Pro.</summary>
        [Fact]
        public void ALiveRetarget_MovesTheDevicesDeadZonesAndBidirectionalFlags()
        {
            var (ps, _) = Arrange(S1, ("RawBtn6", "Axis 4"));
            Assert.Equal(13, NintendoPreviewMap.IndexOf(S2, "LeftTrigger"));
            ps.SetMappingDeadZone("RawBtn6", "35");
            ps.SetMappingBidirectional("RawBtn6", "1");
            ps.FlushMappingDeadZones();
            ps.FlushMappingBidirectional();
            ps.UpdateChecksum();
            var record = SettingsManager.FindSettingByInstanceGuidAndSlot(DevGuid, Pad);
            record.PadSettingChecksum = ps.PadSettingChecksum;
            string before = ps.PadSettingChecksum;

            SettingsManager.TranslateNintendoRawMappings(Pad, S2);

            // The device's record names its settings by their checksum, and
            // follows it as after any other write to them.
            Assert.NotEqual(before, ps.PadSettingChecksum);
            Assert.Equal(ps.PadSettingChecksum, record.PadSettingChecksum);
            Assert.Equal("Axis 4", ps.GetRawMapping("RawBtn13"));
            Assert.Equal("35", ps.GetMappingDeadZone("RawBtn13"));
            Assert.Equal("1", ps.GetMappingBidirectional("RawBtn13"));
            Assert.Equal("", ps.GetMappingDeadZone("RawBtn6"));
            Assert.Equal("", ps.GetMappingBidirectional("RawBtn6"));
            Assert.Contains(ps.MappingDeadZoneEntries, e => e.Key == "RawBtn13" && e.Value == "35");
            Assert.Contains(ps.MappingBidirectionalEntries, e => e.Key == "RawBtn13" && e.Value == "1");
        }

        /// <summary>Two controls of one wire can be one control of another:
        /// a Steam Deck's R3 and its right pad's click are the 2015 Steam
        /// Controller's one right click. Their two rows land on one target,
        /// and the first that has a source keeps it, in the table and in
        /// the device's own entries alike, so no row is left that the grid
        /// cannot show.</summary>
        [Fact]
        public void ALiveRetarget_KeepsOneRowWhereTwoControlsBecomeOne()
        {
            const string Deck = "steam-deck", Pad2015 = "steam-controller";
            string r3 = NintendoPreviewMap.ToRaw("RightThumbButton", Deck);
            string click = NintendoPreviewMap.ToRaw("RightTouchpadClick", Deck);
            string one = NintendoPreviewMap.ToRaw("RightTouchpadClick", Pad2015);
            Assert.NotEqual(r3, click);
            Assert.Equal(one, NintendoPreviewMap.TranslateRawTarget(r3, Deck, Pad2015));
            Assert.Equal(one, NintendoPreviewMap.TranslateRawTarget(click, Deck, Pad2015));
            var (ps, ms) = Arrange(Deck, (r3, "Button 9"), (click, "Button 17"));

            SettingsManager.TranslateNintendoRawMappings(Pad, Pad2015);

            var row = Assert.Single(ms.Rows, r => r.Target == one);
            Assert.Equal("Button 9", Assert.Single(row.Sources).Descriptor);
            Assert.Equal("Button 9", ps.GetRawMapping(one));
        }

        /// <summary>The control goes to the first of the two rows that has a
        /// source, so a row nothing is bound on does not keep it from the
        /// one that is.</summary>
        [Fact]
        public void ALiveRetarget_ARowWithNoSourceDoesNotKeepTheControl()
        {
            const string Deck = "steam-deck", Pad2015 = "steam-controller";
            string r3 = NintendoPreviewMap.ToRaw("RightThumbButton", Deck);
            string click = NintendoPreviewMap.ToRaw("RightTouchpadClick", Deck);
            string one = NintendoPreviewMap.ToRaw("RightTouchpadClick", Pad2015);
            var (_, ms) = Arrange(Deck, (r3, "Button 9"), (click, "Button 17"));
            ms.Rows.Single(r => r.Target == r3).Sources.Clear();

            SettingsManager.TranslateNintendoRawMappings(Pad, Pad2015);

            var row = Assert.Single(ms.Rows, r => r.Target == one);
            Assert.Equal("Button 17", Assert.Single(row.Sources).Descriptor);
        }

        /// <summary>Two rows the table already held on one target are no
        /// doing of the move. They are still two after it, on the control's
        /// new index, in their order.</summary>
        [Fact]
        public void ALiveRetarget_LeavesTwoRowsOfOneTargetAsTwo()
        {
            var (_, ms) = Arrange(S1, ("RawBtn8", "Button 6"), ("RawBtn8", "Button 7"));

            SettingsManager.TranslateNintendoRawMappings(Pad, S2);

            Assert.Equal(new[] { "Button 6", "Button 7" },
                ms.Rows.Where(r => r.Target == "RawBtn14").Select(r => Assert.Single(r.Sources).Descriptor));
            Assert.DoesNotContain(ms.Rows, r => r.Target == "RawBtn8");
        }

        /// <summary>A pair cleans two buttons against each other. Where the
        /// new wire has the two as one control, a Steam Deck's R3 and its
        /// right pad's click on the 2015 Steam Controller, nothing is left
        /// to clean and the pair goes. A pair of two buttons the new wire
        /// keeps apart stays, under their indices there.</summary>
        [Fact]
        public void ALiveRetarget_DropsAPairWhoseButtonsBecomeOne()
        {
            const string Deck = "steam-deck", Pad2015 = "steam-controller";
            static int At(string wire, string role) => NintendoPreviewMap.IndexOf(wire, role);
            var (_, ms) = Arrange(Deck, ("RawBtn0", "Button 0"));
            Assert.NotEqual(At(Deck, "LeftTouchpadClick"), At(Pad2015, "LeftTouchpadClick"));
            ms.SocdMode = "LastWins";
            ms.SocdPairs = $"{At(Deck, "RightThumbButton")}:{At(Deck, "RightTouchpadClick")}"
                + $"|{At(Deck, "LeftTouchpadClick")}:{At(Deck, "ButtonGuide")}";

            SettingsManager.TranslateNintendoRawMappings(Pad, Pad2015);

            Assert.Equal($"{At(Pad2015, "LeftTouchpadClick")}:{At(Pad2015, "ButtonGuide")}", ms.SocdPairs);
        }

        /// <summary>The poll thread enumerates set.Rows concurrently, so a
        /// translating call must REPLACE the list reference rather than
        /// mutate it in place.</summary>
        [Fact]
        public void TranslatingCall_SwapsTheRowsListReference()
        {
            var (_, ms) = Arrange(S1, ("RawBtn8", "Button 6"));
            var before = ms.Rows;

            SettingsManager.TranslateNintendoRawMappings(Pad, S2);

            Assert.NotSame(before, SettingsManager.SlotMappingSets[Pad].Rows);
        }
    }
}

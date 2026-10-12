using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Round-34 guard for the build-then-swap discipline on the two lists
    /// the poll thread enumerates without a lock.
    ///
    /// <para>ApplyShiftLayerSnapshot already documents the rule: "the engine
    /// polling thread enumerates slotMs.ShiftActivators every frame without
    /// our lock, so it must never observe a list that is mid-fill." The
    /// PadPage add and delete paths mutated that same list in place, so a
    /// layer added or deleted while the pad was being polled could throw
    /// "collection was modified" inside ApplyMappingSetToGamepad's foreach.
    /// UpdateOutputStates catches it, which means the device silently lost
    /// its whole mapping evaluation for that frame.</para>
    ///
    /// <para>These tests assert the observable consequence of a swap: a
    /// reader holding the previous list reference keeps seeing a stable,
    /// complete list. That is exactly what the poll thread does, since both
    /// ResolveActiveLayerMask and the inheritance scan capture the reference
    /// before iterating.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class LiveListSwapTests : System.IDisposable
    {
        // SlotMappingSets is process-wide state that other classes in this
        // collection read. Restore whatever was there, so these tests cannot
        // decide the outcome of a test that happens to run after them.
        private readonly MappingSet[] _priorSets = SettingsManager.SlotMappingSets;
        private readonly SettingsCollection _priorSettings = SettingsManager.UserSettings;
        private readonly DeviceCollection _priorDevices = SettingsManager.UserDevices;
        private readonly System.Action _priorAfterRefresh = PadForge.Services.SettingsService.AfterMappingSetsRefreshed;

        public void Dispose()
        {
            SettingsManager.SlotMappingSets = _priorSets;
            SettingsManager.UserSettings = _priorSettings;
            SettingsManager.UserDevices = _priorDevices;
            PadForge.Services.SettingsService.AfterMappingSetsRefreshed = _priorAfterRefresh;
        }

        private static MappingSet SetWith(params string[] masks)
        {
            var ms = new MappingSet();
            foreach (var m in masks)
                ms.ShiftActivators.Add(new ShiftActivator { LayerMask = m, LayerName = m, Mode = "Hold" });
            return ms;
        }

        [Fact]
        public void LayerDelete_SwapsTheListInsteadOfMutatingIt()
        {
            var ms = SetWith("Shift", "Alt");
            SettingsManager.SlotMappingSets = new[] { ms };
            var target = ms.ShiftActivators[0];

            // What the poll thread holds when the delete lands.
            var pollThreadView = ms.ShiftActivators;

            PadForge.Views.PadPage.ExecuteLayerDelete(ms, target, "Shift", new List<PadViewModel>());

            Assert.NotSame(pollThreadView, ms.ShiftActivators);   // swapped
            Assert.Equal(2, pollThreadView.Count);                // old view intact
            Assert.Single(ms.ShiftActivators);                    // new view correct
            Assert.Equal("Alt", ms.ShiftActivators[0].LayerMask);
        }

        [Fact]
        public void LayerDelete_OldViewStaysEnumerableThroughTheEdit()
        {
            // The failure this replaces: an in-place Remove during the poll
            // thread's foreach throws InvalidOperationException.
            var ms = SetWith("Shift", "Alt", "Ctrl");
            SettingsManager.SlotMappingSets = new[] { ms };
            var view = ms.ShiftActivators;

            int seen = 0;
            foreach (var a in view)
            {
                if (seen == 0)
                    PadForge.Views.PadPage.ExecuteLayerDelete(
                        ms, ms.ShiftActivators[2], "Ctrl", new List<PadViewModel>());
                seen++;
            }
            Assert.Equal(3, seen);
        }

        [Fact]
        public void RowSources_SwapLeavesTheOldListComplete()
        {
            // The mirror of the same rule on MappingRow.Sources, which the
            // save path rebuilds. A reader that captured the old list must
            // keep seeing every entry it had.
            var row = new MappingRow { Target = "ButtonA" };
            row.Sources.Add(new MappingSource { Descriptor = "Button 1" });
            row.Sources.Add(new MappingSource { Descriptor = "Button 2" });
            var captured = row.Sources;

            row.Sources = new List<MappingSource>
            {
                new MappingSource { Descriptor = "Button 9" }
            };

            Assert.Equal(2, captured.Count);
            Assert.Single(row.Sources);
            Assert.NotSame(captured, row.Sources);
        }

        [Fact]
        public void CapturedSources_SurviveAConcurrentSwapMidIteration()
        {
            // Pins the reader-side half: capture the reference, then index
            // that local. Pre-fix the loops re-read row.Sources every
            // iteration, so a swap between the Count read and the indexer
            // threw ArgumentOutOfRangeException on the poll thread.
            var row = new MappingRow { Target = "ButtonA" };
            for (int i = 0; i < 4; i++)
                row.Sources.Add(new MappingSource { Descriptor = "Button " + i });

            var srcs = row.Sources;
            int count = 0;
            for (int i = 0; i < srcs.Count; i++)
            {
                if (i == 1) row.Sources = new List<MappingSource>();  // the save lands
                Assert.NotNull(srcs[i]);
                count++;
            }
            Assert.Equal(4, count);
            Assert.Empty(row.Sources);
        }

        // ── The writers outside the save path ──
        //
        // Unassigning a controller, the assign and unassign merge, a device
        // paste and the motion backfill edited a live row's sources in place.
        // A shrink between the poll thread's Count and its indexer threw
        // there, and a commit reaching a row (LayerExitCommitReaches) read a
        // captured list the same way. Each now builds the new list and swaps
        // it in.

        private static readonly System.Guid PadA = new("5a5a0001-0000-0000-0000-00000000000a");
        private static readonly System.Guid PadB = new("5a5a0002-0000-0000-0000-00000000000b");
        private static readonly System.Guid PadC = new("5a5a0003-0000-0000-0000-00000000000c");

        private static MappingSource On(System.Guid device, string descriptor)
            => new() { DeviceGuid = device.ToString(), Descriptor = descriptor };

        private static void Assign(System.Guid id, PadSetting ps = null)
        {
            SettingsManager.UserDevices.Items.Add(new UserDevice
            {
                InstanceGuid = id, ProductGuid = id, IsOnline = true,
                ProductName = id.ToString(), InstanceName = id.ToString(),
                CapType = PadForge.Engine.InputDeviceType.Gamepad, CapButtonCount = 16,
                InputState = new PadForge.Engine.CustomInputState(),
            });
            var setting = new UserSetting { InstanceGuid = id, ProductGuid = id, MapTo = 0 };
            setting.SetPadSetting(ps ?? new PadSetting());
            SettingsManager.UserSettings.Items.Add(setting);
        }

        private static void FreshSlots()
        {
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            PadForge.Services.SettingsService.AfterMappingSetsRefreshed = null;
        }

        [Fact]
        public void UnassigningAController_SwapsTheListsInsteadOfShrinkingThem()
        {
            FreshSlots();
            var kept = new MappingRow { Target = "ButtonA", LayerMask = "Base" };
            kept.Sources.Add(On(PadA, "Button 0"));
            kept.Sources.Add(On(PadB, "Button 1"));
            var emptied = new MappingRow { Target = "ButtonB", LayerMask = "Base" };
            emptied.Sources.Add(On(PadA, "Button 2"));
            var ms = new MappingSet();
            ms.Rows.Add(kept);
            ms.Rows.Add(emptied);
            SettingsManager.SlotMappingSets[0] = ms;
            var sourcesView = kept.Sources;
            var rowsView = ms.Rows;

            PadForge.Services.SettingsService.StripDeviceFromSlot(PadA, 0);

            Assert.Equal(2, sourcesView.Count);            // what the poll thread holds stays whole
            Assert.NotSame(sourcesView, kept.Sources);
            Assert.Equal(PadB.ToString(), Assert.Single(kept.Sources).DeviceGuid);
            Assert.Equal(2, rowsView.Count);
            Assert.NotSame(rowsView, ms.Rows);
            Assert.Same(kept, Assert.Single(ms.Rows));
        }

        [Fact]
        public void TheAssignMerge_SwapsARowsSourcesInsteadOfEditingThem()
        {
            FreshSlots();
            Assign(PadA, new PadSetting { ButtonA = "Button 0", ButtonB = "Button 2" });
            Assign(PadB);
            // PadC has left the slot. Its source goes, and PadA's legacy
            // default joins, the merge's two edits to one live row. The
            // second row only gains PadA's default.
            var row = new MappingRow { Target = "ButtonA", LayerMask = "Base" };
            row.Sources.Add(On(PadB, "Button 1"));
            row.Sources.Add(On(PadC, "Button 3"));
            var grows = new MappingRow { Target = "ButtonB", LayerMask = "Base" };
            grows.Sources.Add(On(PadB, "Button 4"));
            var ms = new MappingSet();
            ms.Rows.Add(row);
            ms.Rows.Add(grows);
            SettingsManager.SlotMappingSets[0] = ms;
            var view = row.Sources;
            var growsView = grows.Sources;

            PadForge.Services.SettingsService.RefreshMappingSetsFromLegacy();

            Assert.Equal(2, view.Count);
            Assert.Equal(PadC.ToString(), view[1].DeviceGuid);
            var merged = Assert.Single(SettingsManager.SlotMappingSets[0].Rows, r => r.Target == "ButtonA");
            Assert.Same(row, merged);
            Assert.NotSame(view, merged.Sources);
            Assert.Equal(new[] { PadB.ToString(), PadA.ToString() },
                merged.Sources.Select(s => s.DeviceGuid.ToLowerInvariant()));

            Assert.Single(growsView);
            var grown = Assert.Single(SettingsManager.SlotMappingSets[0].Rows, r => r.Target == "ButtonB");
            Assert.Same(grows, grown);
            Assert.Equal(new[] { PadB.ToString(), PadA.ToString() },
                grown.Sources.Select(s => s.DeviceGuid.ToLowerInvariant()));
        }

        [Fact]
        public void ADevicePaste_SwapsTheTargetRowsSourcesInsteadOfEditingThem()
        {
            FreshSlots();
            var row = new MappingRow { Target = "ButtonA", LayerMask = "Base" };
            row.Sources.Add(On(PadA, "Button 0"));
            row.Sources.Add(On(PadB, "Button 1"));
            var ms = new MappingSet();
            ms.Rows.Add(row);
            SettingsManager.SlotMappingSets[0] = ms;
            var view = row.Sources;
            var donor = new MappingRow { Target = "ButtonA", LayerMask = "Base" };
            donor.Sources.Add(On(PadC, "Button 7"));

            PadForge.Services.InputService.ApplyMultiSourceRowsToCurrentDevice(0, PadA, new[] { donor });

            Assert.Equal(2, view.Count);
            Assert.Equal(PadA.ToString(), view[0].DeviceGuid);
            Assert.NotSame(view, row.Sources);
            Assert.Equal(new[] { PadB.ToString(), PadA.ToString() }, row.Sources.Select(s => s.DeviceGuid));
            Assert.Equal("Button 7", row.Sources[1].Descriptor);
        }

        /// <summary>The motion backfill gives a newly assigned controller's
        /// gyro a source on the slot's Motion Gyro row. It runs on the live
        /// set after a device change, and it appended to the row's list in
        /// place.</summary>
        [Fact]
        public void TheMotionBackfill_SwapsARowsSourcesInsteadOfAppendingToThem()
        {
            var row = new MappingRow { Target = MappingSetMigrator.MotionGyroTarget, LayerMask = "Base" };
            row.Sources.Add(On(PadA, MappingSetMigrator.MotionGyroSourceDescriptor));
            var ms = new MappingSet();
            ms.Rows.Add(row);
            var view = row.Sources;

            MappingSetMigrator.EnsureMotionRows(ms, slotType: 5, new[]
            {
                (PadA.ToString(), true, false),
                (PadB.ToString(), true, false),
            });

            Assert.Single(view);
            Assert.NotSame(view, row.Sources);
            Assert.Equal(new[] { PadA.ToString(), PadB.ToString() }, row.Sources.Select(s => s.DeviceGuid));
        }
    }
}

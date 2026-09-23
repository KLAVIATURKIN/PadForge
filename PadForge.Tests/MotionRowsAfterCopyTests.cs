using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The copy half of discussion #446. Copy and Paste, and Copy From, move a
    /// slot's rows onto another slot and drop every source whose device has no
    /// same-product twin there. A motion row that lost every input that way
    /// used to be copied empty, and an empty motion row is how a user switches
    /// motion off, so the target slot came up with motion off and the motion
    /// auto-map left it alone when its device arrived.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class MotionRowsAfterCopyTests : IDisposable
    {
        private const int SourceSlot = 0;
        private const int TargetSlot = 1;
        private static readonly Guid SourcePad = new("44460001-1111-2222-3333-444444444444");
        private static readonly Guid TargetPad = new("44460002-1111-2222-3333-444444444444");
        private static readonly Guid OnlyOnSource = new("44460003-1111-2222-3333-444444444444");
        private static readonly Guid SharedProduct = new("44460004-1111-2222-3333-444444444444");

        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
        private readonly bool[] _created = (bool[])SettingsManager.SlotCreated.Clone();
        private readonly bool[] _enabled = (bool[])SettingsManager.SlotEnabled.Clone();
        private readonly bool _stale = InputService.VmMappingsStale;
        private readonly bool _suppressPush = InputService.SuppressMappingEditPush;
        private readonly Action _afterRefresh = SettingsService.AfterMappingSetsRefreshed;

        public MotionRowsAfterCopyTests()
        {
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            Array.Clear(SettingsManager.SlotCreated);
            Array.Clear(SettingsManager.SlotEnabled);
            SettingsManager.SlotCreated[SourceSlot] = SettingsManager.SlotCreated[TargetSlot] = true;
            SettingsManager.SlotEnabled[SourceSlot] = SettingsManager.SlotEnabled[TargetSlot] = true;
            InputService.VmMappingsStale = false;
            InputService.SuppressMappingEditPush = false;
            SettingsService.AfterMappingSetsRefreshed = null;
            AddDevice(SourcePad, SharedProduct, SourceSlot);
            AddDevice(TargetPad, SharedProduct, TargetSlot);
            AddDevice(OnlyOnSource, OnlyOnSource, SourceSlot);
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _settings;
            SettingsManager.UserDevices = _devices;
            SettingsManager.SlotMappingSets = _sets;
            Array.Copy(_created, SettingsManager.SlotCreated, _created.Length);
            Array.Copy(_enabled, SettingsManager.SlotEnabled, _enabled.Length);
            InputService.VmMappingsStale = _stale;
            InputService.SuppressMappingEditPush = _suppressPush;
            SettingsService.AfterMappingSetsRefreshed = _afterRefresh;
        }

        private static void AddDevice(Guid id, Guid product, int slot)
        {
            SettingsManager.UserDevices.Items.Add(new UserDevice
            {
                InstanceGuid = id, ProductGuid = product, IsOnline = true,
                ProductName = id.ToString(), InstanceName = id.ToString(),
                CapType = InputDeviceType.Gamepad,
            });
            var setting = new UserSetting { InstanceGuid = id, ProductGuid = product, MapTo = slot };
            setting.SetPadSetting(new PadSetting());
            SettingsManager.UserSettings.Items.Add(setting);
        }

        private static MappingSource Gyro(Guid id) =>
            new() { DeviceGuid = id.ToString(), Descriptor = MappingSetMigrator.MotionGyroSourceDescriptor };

        private static MappingSource Modifier(Guid id) =>
            new() { Kind = "InvertOnHold", DeviceGuid = id.ToString(), Descriptor = "Button 0" };

        private static MappingRow GyroRow(params MappingSource[] sources) =>
            new() { Target = MappingSetMigrator.MotionGyroTarget, LayerMask = "Base", Sources = sources.ToList() };

        private static MappingSet Copy(MappingRow row, bool clipboard)
        {
            SettingsManager.SlotMappingSets[SourceSlot] = new MappingSet { Rows = new List<MappingRow> { row } };
            SettingsManager.SlotMappingSets[TargetSlot] = new MappingSet();
            if (clipboard)
            {
                var payload = new PadSetting { SlotMultiSourceRows = InputService.ExtractAllRowsForSlot(SourceSlot) };
                var parsed = PadSetting.FromJson(payload.ToJson(VirtualControllerType.PlayStation, false), out _, out _);
                InputService.ApplySlotMappingSetFromRows(TargetSlot, parsed.SlotMultiSourceRows);
            }
            else InputService.ReplaceSlotMappingSet(TargetSlot, SourceSlot);
            return SettingsManager.SlotMappingSets[TargetSlot];
        }

        private static MappingRow GyroRowOf(MappingSet set) =>
            set?.Rows?.FirstOrDefault(r => r?.Target == MappingSetMigrator.MotionGyroTarget);

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AMotionRowThatLostEveryInputIsLeftForTheAutoMap(bool clipboard)
        {
            var copied = Copy(GyroRow(Gyro(OnlyOnSource)), clipboard);
            Assert.Null(GyroRowOf(copied));

            // The slot's own pad arrives, and the motion auto-map maps it.
            MappingSetMigrator.EnsureMotionRows(copied, slotType: 1,
                new[] { (TargetPad.ToString(), true, true) });
            var row = GyroRowOf(copied);
            Assert.NotNull(row);
            Assert.Contains(row.Sources, s => string.Equals(s.DeviceGuid, TargetPad.ToString(), StringComparison.OrdinalIgnoreCase));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AMotionRowThatKeptOneInputIsCopied(bool clipboard)
        {
            var copied = Copy(GyroRow(Gyro(OnlyOnSource), Gyro(SourcePad)), clipboard);
            var row = GyroRowOf(copied);
            Assert.NotNull(row);
            var source = Assert.Single(row.Sources);
            Assert.Equal(TargetPad.ToString(), source.DeviceGuid, ignoreCase: true);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AMotionRowTheUserSwitchedOffStaysOff(bool clipboard)
        {
            var copied = Copy(GyroRow(), clipboard);
            var row = GyroRowOf(copied);
            Assert.NotNull(row);
            Assert.True(MappingSetMigrator.IsEmptyMotionRow(row));

            MappingSetMigrator.EnsureMotionRows(copied, slotType: 1,
                new[] { (TargetPad.ToString(), true, true) });
            Assert.True(MappingSetMigrator.IsEmptyMotionRow(GyroRowOf(copied)));
        }

        /// <summary>A modifier that retargets is not a motion input, so a row
        /// left with only the modifier lost what made it a motion mapping.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AMotionRowLeftWithOnlyAModifierIsLeftForTheAutoMap(bool clipboard)
        {
            var copied = Copy(GyroRow(Gyro(OnlyOnSource), Modifier(SourcePad)), clipboard);
            Assert.Null(GyroRowOf(copied));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ACustomMotionRowKeepsItsPositions(bool clipboard)
        {
            var custom = GyroRow(Gyro(OnlyOnSource));
            custom.CombineMode = "Custom";
            custom.CombineExpression = "a";
            var row = GyroRowOf(Copy(custom, clipboard));
            Assert.NotNull(row);
            Assert.Single(row.Sources);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ANoInheritMotionRowIsCopied(bool clipboard)
        {
            var layer = GyroRow(Gyro(OnlyOnSource));
            layer.LayerMask = "1";
            layer.NoInherit = true;
            var copied = Copy(layer, clipboard);
            var row = copied.Rows.FirstOrDefault(r => r?.Target == MappingSetMigrator.MotionGyroTarget && r.LayerMask == "1");
            Assert.NotNull(row);
            Assert.True(row.NoInherit);
        }

        [Fact]
        public void TheRuleNeedsAMotionTargetThatHadInputs()
        {
            var button = new MappingRow { Target = "ButtonA", Sources = new List<MappingSource>() };
            var original = new MappingRow { Target = "ButtonA", Sources = new List<MappingSource> { Gyro(OnlyOnSource) } };
            Assert.False(InputService.LostEveryMotionInput(original, button));
            Assert.True(InputService.LostEveryMotionInput(GyroRow(Gyro(OnlyOnSource)), GyroRow()));
            Assert.False(InputService.LostEveryMotionInput(GyroRow(), GyroRow()));
        }
    }
}

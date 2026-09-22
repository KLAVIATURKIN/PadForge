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
    /// Discussion #446: a DualSense assigned to a new PlayStation slot came up
    /// with every row mapped except Motion Gyro and Motion Accelerometer, so
    /// the virtual DS4 carried no motion. The slot's grid had been saved while
    /// no device was on the slot, and the save wrote a row for every target,
    /// the two motion rows included, all of them empty. An empty motion row is
    /// how a user switches that channel off, so the auto-map on assignment
    /// left both alone while it filled every other row.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class MotionRowsAfterEmptyGridSaveTests : IDisposable
    {
        private static readonly Guid Pad = new("46464646-4646-4646-4646-464646464646");

        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;
        private readonly bool[] _savedCreated;
        private readonly bool[] _savedEnabled;
        private readonly MappingSet[] _savedMappingSets;
        private readonly List<int> _savedXboxOrder;
        private readonly List<int> _savedPlayStationOrder;
        private readonly Action _savedAfterRefresh;

        public MotionRowsAfterEmptyGridSaveTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
            _savedCreated = (bool[])SettingsManager.SlotCreated.Clone();
            _savedEnabled = (bool[])SettingsManager.SlotEnabled.Clone();
            _savedMappingSets = SettingsManager.SlotMappingSets;
            _savedXboxOrder = SettingsManager.XboxSlotOrder;
            _savedPlayStationOrder = SettingsManager.PlayStationSlotOrder;
            _savedAfterRefresh = SettingsService.AfterMappingSetsRefreshed;
        }

        public void Dispose()
        {
            InputService.VmMappingsStale = false;
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
            Array.Copy(_savedCreated, SettingsManager.SlotCreated, _savedCreated.Length);
            Array.Copy(_savedEnabled, SettingsManager.SlotEnabled, _savedEnabled.Length);
            SettingsManager.SlotMappingSets = _savedMappingSets;
            SettingsManager.XboxSlotOrder = _savedXboxOrder;
            SettingsManager.PlayStationSlotOrder = _savedPlayStationOrder;
            SettingsService.AfterMappingSetsRefreshed = _savedAfterRefresh;
        }

        /// <summary>A created PlayStation slot 0 with nothing assigned, its
        /// grid hydrated the way opening the pad page hydrates it.</summary>
        private static (MainViewModel vm, SettingsService ss) ArrangeEmptyPlayStationSlot()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
            Array.Clear(SettingsManager.SlotEnabled, 0, SettingsManager.SlotEnabled.Length);
            SettingsManager.SlotCreated[0] = true;
            SettingsManager.SlotEnabled[0] = true;
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            SettingsManager.XboxSlotOrder = new List<int>();
            SettingsManager.PlayStationSlotOrder = new List<int> { 0 };

            var vm = new MainViewModel();
            var ss = new SettingsService(vm);
            vm.Pads[0].OutputType = VirtualControllerType.PlayStation;
            InputService.RefreshMappingsToViewModel(vm.Pads[0]);
            Assert.True(vm.Pads[0].MappingsViewLoaded);
            Assert.Contains(vm.Pads[0].Mappings, m => m.TargetSettingName == MappingSetMigrator.MotionGyroTarget);
            return (vm, ss);
        }

        private static void AssignMotionPad()
        {
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(new UserDevice
                {
                    InstanceGuid = Pad,
                    ProductGuid = Pad,
                    InstanceName = "DualSense Wireless Controller",
                    ProductName = "DualSense Wireless Controller",
                    IsOnline = true,
                    CapType = InputDeviceType.Gamepad,
                    HasGyro = true,
                    HasAccel = true,
                });
            lock (SettingsManager.UserSettings.SyncRoot)
            {
                var us = new UserSetting { InstanceGuid = Pad, MapTo = 0 };
                us.SetPadSetting(new PadSetting());
                SettingsManager.UserSettings.Items.Add(us);
            }
            SettingsService.RefreshMappingSetsFromLegacy();
        }

        private static MappingRow BaseRow(string target)
            => SettingsManager.SlotMappingSets[0]?.Rows.FirstOrDefault(r =>
                r.Target == target && (r.LayerMask ?? "Base") == "Base");

        [Fact]
        public void AnEmptyGridSaveBeforeAssignment_LeavesMotionToTheAutoMap()
        {
            var (_, ss) = ArrangeEmptyPlayStationSlot();
            ss.PushUiExtraSourcesIntoSlotMappingSets();

            AssignMotionPad();

            foreach (var target in new[] { MappingSetMigrator.MotionGyroTarget, MappingSetMigrator.MotionAccelTarget })
            {
                var row = BaseRow(target);
                Assert.NotNull(row);
                Assert.Contains(row.Sources, s => string.Equals(s.DeviceGuid, Pad.ToString(), StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>The other half of the contract: a motion row the user
        /// emptied is a switched-off channel and stays that way through a
        /// grid save and a fresh auto-map.</summary>
        [Fact]
        public void AMotionRowTheUserCleared_StaysCleared()
        {
            var (vm, ss) = ArrangeEmptyPlayStationSlot();
            AssignMotionPad();
            Assert.Contains(BaseRow(MappingSetMigrator.MotionGyroTarget).Sources,
                s => string.Equals(s.DeviceGuid, Pad.ToString(), StringComparison.OrdinalIgnoreCase));

            InputService.RefreshMappingsToViewModel(vm.Pads[0]);
            var gyro = vm.Pads[0].Mappings.First(m => m.TargetSettingName == MappingSetMigrator.MotionGyroTarget);
            Assert.True(gyro.HasAnySource);
            gyro.ClearCommand.Execute(null);
            Assert.False(gyro.HasAnySource);
            ss.PushUiExtraSourcesIntoSlotMappingSets();

            SettingsService.RefreshMappingSetsFromLegacy();

            var row = BaseRow(MappingSetMigrator.MotionGyroTarget);
            Assert.NotNull(row);
            Assert.Empty(row.Sources);
        }
    }
}

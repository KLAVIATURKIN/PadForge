using System;
using PadForge.Engine;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Devices page shows named chips instead of the numbered grid for rows
    /// that name every button they publish: handheld buttons, VR controllers and
    /// G-Keys. The rebuild built the named chips and then cleared them a few
    /// lines later whenever the row was not a handheld one, so a G-Keys or VR
    /// row showed "No buttons learned yet" with the numbered grid hidden.
    /// </summary>
    public class NamedButtonChipsTests
    {
        private static DeviceObjectItem Button(string name, int index) => new DeviceObjectItem
        {
            Name = name,
            ObjectTypeGuid = ObjectGuid.Button,
            InputIndex = index,
        };

        [Fact]
        public void ARowWithNamedObjects_KeepsItsChips()
        {
            var vm = new DevicesViewModel();
            var named = new[] { Button("G1", 0), Button("G2", 1), Button("M1", 2) };
            vm.RebuildRawStateCollections(Array.Empty<int>(), new[] { 0, 1, 2 }, 0, namedObjects: named);
            Assert.True(vm.ShowNamedButtons);
            Assert.Equal(3, vm.HandheldButtons.Count);
        }

        [Fact]
        public void ClearingTheRawState_DropsTheNamedLayout()
        {
            var vm = new DevicesViewModel();
            vm.RebuildRawStateCollections(Array.Empty<int>(), new[] { 0 }, 0, namedObjects: new[] { Button("G1", 0) });
            vm.ClearRawState();
            Assert.False(vm.ShowNamedButtons);
            Assert.Empty(vm.HandheldButtons);
        }
    }
}

using System;
using System.Collections.Generic;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Flick Stick card seeds from a slot's mapping source when the
    /// receiving device has never stored the card. The seed must belong to
    /// that device, or to no device at all.
    ///
    /// <para>Returning the slot's first flick source regardless of owner gave a
    /// second device the first one's tuning. The empty guid is the "any device"
    /// sentinel and must stay eligible, because a Workshop import carries no
    /// concrete device and that seeding is the whole reason the lookup
    /// exists.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class FlickStickSeedOwnershipTests : IDisposable
    {
        private readonly MappingSet[] saved = SettingsManager.SlotMappingSets;

        public void Dispose() => SettingsManager.SlotMappingSets = saved;

        private static readonly string DeviceA = Guid.NewGuid().ToString();
        private static readonly string DeviceB = Guid.NewGuid().ToString();

        private static void Seed(params MappingSource[] sources)
        {
            var sets = new MappingSet[InputManager.MaxPads];
            sets[0] = new MappingSet
            {
                Rows = new List<MappingRow>
                {
                    new MappingRow { Target = "RightStick", LayerMask = "Base", Sources = new List<MappingSource>(sources) },
                },
            };
            SettingsManager.SlotMappingSets = sets;
        }

        private static MappingSource Flick(string owner, double dots) => new MappingSource
        {
            DeviceGuid = owner,
            Descriptor = "Flick Stick Right",
            ParamFlickCountsPer360 = dots,
        };

        [Fact]
        public void ADeviceSeedsFromItsOwnSourceNotTheFirstOne()
        {
            Seed(Flick(DeviceA, 1111), Flick(DeviceB, 2222));
            Assert.Equal(1111, SettingsService.FindSlotFlickStickSource(0, DeviceA).ParamFlickCountsPer360);
            Assert.Equal(2222, SettingsService.FindSlotFlickStickSource(0, DeviceB).ParamFlickCountsPer360);
        }

        [Fact]
        public void ADeviceWithNoSourceOfItsOwnDoesNotInheritAnothers()
        {
            Seed(Flick(DeviceA, 1111));
            Assert.Null(SettingsService.FindSlotFlickStickSource(0, DeviceB));
        }

        /// <summary>The Workshop import case: rows carry no device, and every
        /// device must still seed from them.</summary>
        [Fact]
        public void AWildcardSourceStillSeedsAnyDevice()
        {
            Seed(Flick("", 3333));
            Assert.Equal(3333, SettingsService.FindSlotFlickStickSource(0, DeviceA).ParamFlickCountsPer360);
            Assert.Equal(3333, SettingsService.FindSlotFlickStickSource(0, DeviceB).ParamFlickCountsPer360);
        }

        /// <summary>The device's own tuning outranks a wildcard in the same
        /// slot, whichever order they appear in.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AnOwnedSourceOutranksAWildcard(bool wildcardFirst)
        {
            Seed(wildcardFirst
                ? new[] { Flick("", 3333), Flick(DeviceA, 1111) }
                : new[] { Flick(DeviceA, 1111), Flick("", 3333) });
            Assert.Equal(1111, SettingsService.FindSlotFlickStickSource(0, DeviceA).ParamFlickCountsPer360);
            Assert.Equal(3333, SettingsService.FindSlotFlickStickSource(0, DeviceB).ParamFlickCountsPer360);
        }
    }
}

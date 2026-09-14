using PadForge.Common.Input;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The identity key PadForge hands HIDMaestro for a slot's
    /// virtual controller (HM#60, #395). HIDMaestro derives every device
    /// path from it, so the key has to be the same on every life of the
    /// same slot and family, and different across slots and families.</summary>
    public class VirtualControllerIdentityKeyTests
    {
        [Fact]
        public void SameSlotAndFamily_SameKeyEveryTime()
        {
            Assert.Equal(
                HMaestroVirtualController.IdentityKeyFor(0, VirtualControllerType.Extended),
                HMaestroVirtualController.IdentityKeyFor(0, VirtualControllerType.Extended));
        }

        [Fact]
        public void Slots_GetDistinctKeys()
        {
            Assert.NotEqual(
                HMaestroVirtualController.IdentityKeyFor(0, VirtualControllerType.Xbox),
                HMaestroVirtualController.IdentityKeyFor(1, VirtualControllerType.Xbox));
        }

        [Fact]
        public void Families_GetDistinctKeys()
        {
            Assert.NotEqual(
                HMaestroVirtualController.IdentityKeyFor(0, VirtualControllerType.Xbox),
                HMaestroVirtualController.IdentityKeyFor(0, VirtualControllerType.PlayStation));
        }

        [Fact]
        public void KeyIsPlainAscii_WithNoSurroundingWhitespace()
        {
            // HIDMaestro trims keys before hashing; a key that differs only
            // by whitespace would collide, so none is emitted.
            string k = HMaestroVirtualController.IdentityKeyFor(3, VirtualControllerType.PlayStation);
            Assert.Equal(k.Trim(), k);
            foreach (char c in k) Assert.True(c < 128 && !char.IsWhiteSpace(c));
            Assert.Equal("padforge:slot3:PlayStation", k);
        }
    }
}

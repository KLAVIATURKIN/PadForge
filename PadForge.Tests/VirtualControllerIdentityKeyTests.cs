using System.Collections.Generic;
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

        /// <summary>The number in the key is the pad's position within its own
        /// family's order list, not its pad index. Pad index is data identity
        /// and moves on a reorder.</summary>
        [Fact]
        public void KeyCarriesThePositionInTheGroup_NotThePadIndex()
        {
            var order = new List<int> { 3, 7 };
            Assert.Equal("padforge:slot0:Xbox",
                HMaestroVirtualController.IdentityKeyForPad(3, VirtualControllerType.Xbox, order));
            Assert.Equal("padforge:slot1:Xbox",
                HMaestroVirtualController.IdentityKeyForPad(7, VirtualControllerType.Xbox, order));
        }

        /// <summary>A reorder rotates which pad each position serves. Every
        /// position keeps the key it had, so nothing asks for a key another
        /// live controller is still holding and no live pad's device paths
        /// change under it.</summary>
        [Fact]
        public void AReorderLeavesEveryPositionsKeyWhereItWas()
        {
            var before = new List<int> { 0, 1, 2 };
            var after = new List<int> { 2, 0, 1 };

            for (int position = 0; position < 3; position++)
            {
                Assert.Equal(
                    HMaestroVirtualController.IdentityKeyForPad(
                        before[position], VirtualControllerType.Xbox, before),
                    HMaestroVirtualController.IdentityKeyForPad(
                        after[position], VirtualControllerType.Xbox, after));
            }
        }

        /// <summary>The reorder case that collided. Position 0 reuses its
        /// controller because the pad rotating in wants the same profile, and
        /// position 1 is destroyed and rebuilt because its pad's profile
        /// differs. Keyed on the pad index, the rebuild asked for the key the
        /// reused controller still held.</summary>
        [Fact]
        public void AReusedPositionAndARebuiltOneDoNotWantTheSameKey()
        {
            var before = new List<int> { 0, 1, 2 };
            var after = new List<int> { 2, 0, 1 };

            // Position 0 keeps its controller and therefore its key.
            string reused = HMaestroVirtualController.IdentityKeyForPad(
                before[0], VirtualControllerType.Xbox, before);
            // Position 1 is rebuilt for the pad that rotated into it.
            string rebuilt = HMaestroVirtualController.IdentityKeyForPad(
                after[1], VirtualControllerType.Xbox, after);

            Assert.NotEqual(reused, rebuilt);
        }

        /// <summary>Every live position holds a different key, which is what
        /// keeps HIDMaestro from refusing a create.</summary>
        [Fact]
        public void EveryPositionInAGroupHoldsADistinctKey()
        {
            var order = new List<int> { 5, 0, 9, 2 };
            var keys = new HashSet<string>();
            foreach (int pad in order)
            {
                Assert.True(keys.Add(HMaestroVirtualController.IdentityKeyForPad(
                    pad, VirtualControllerType.Extended, order)));
            }
            Assert.Equal(order.Count, keys.Count);
        }

        /// <summary>A pad the order list does not carry has no position. It
        /// gets a key of its own namespace rather than one built from its pad
        /// index, which would collide with whichever pad sits at that
        /// position.</summary>
        [Fact]
        public void APadWithNoPositionDoesNotBorrowAnothersKey()
        {
            var order = new List<int> { 3, 7 };
            string orphan = HMaestroVirtualController.IdentityKeyForPad(
                1, VirtualControllerType.Xbox, order);

            Assert.Equal("padforge:pad1:Xbox", orphan);
            Assert.NotEqual(
                HMaestroVirtualController.IdentityKeyForPad(7, VirtualControllerType.Xbox, order),
                orphan);
            Assert.Null(Record.Exception(() =>
                HMaestroVirtualController.IdentityKeyForPad(1, VirtualControllerType.Xbox, null)));
        }

        /// <summary>Position and family together make the key, so the same
        /// position in two families stays distinct.</summary>
        [Fact]
        public void TheSamePositionInTwoFamiliesStaysDistinct()
        {
            var xbox = new List<int> { 4 };
            var extended = new List<int> { 6 };
            Assert.NotEqual(
                HMaestroVirtualController.IdentityKeyForPad(4, VirtualControllerType.Xbox, xbox),
                HMaestroVirtualController.IdentityKeyForPad(6, VirtualControllerType.Extended, extended));
        }
    }
}

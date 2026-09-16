using System;
using System.Linq;
using System.Reflection;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.RemoteLink;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A supported-index set has three states, and the wire used to carry only
    /// two. Null is "the owner does not gate this", a non-empty array is
    /// "exactly these positions", and an empty array is "the device has none".
    /// The v6 and v7 masks spell dense as a single zero byte, and an empty set
    /// had no other spelling, so a pad whose owner knows it has no sticks
    /// arrived on the consumer advertising all six and the default profile
    /// auto-bound both of them.
    /// </summary>
    public class ExplicitEmptyCapabilityTests
    {
        private static RemotePeerDeviceInfo Stickless() => new RemotePeerDeviceInfo
        {
            PeerLocalDeviceId = "nosticks",
            Name = "Arcade Stick",
            InputDeviceType = InputDeviceType.Gamepad,
            NumAxes = 6,
            RawAxisCount = 6,
            NumButtons = 22,
            RawButtonCount = 22,
            NumHats = 1,
            SupportedButtonIndices = new[] { 0, 1, 2, 3, 4, 5, 6, 7 },
            // The owner asked SDL and got no axes at all.
            SupportedAxisIndices = Array.Empty<int>(),
        };

        [Fact]
        public void AnEmptyAxisSetCrossesTheWireAsEmpty()
        {
            var round = LinkConnection.DecodeDeviceList(
                LinkConnection.EncodeDeviceList(new[] { Stickless() }, ""));

            Assert.Single(round);
            Assert.NotNull(round[0].SupportedAxisIndices);
            Assert.Empty(round[0].SupportedAxisIndices);
        }

        [Fact]
        public void TheConsumerAdvertisesNoAxesForAnEmptySet()
        {
            var round = LinkConnection.DecodeDeviceList(
                LinkConnection.EncodeDeviceList(new[] { Stickless() }, ""));
            var dev = new RemotePeerDevice(round[0]);

            Assert.Empty(dev.SupportedAxisIndices);
            Assert.DoesNotContain(dev.GetDeviceObjects(), o => o.IsAxis);
            // And the buttons it does have are untouched by the flag.
            Assert.Equal(8, dev.SupportedButtonIndices.Length);
        }

        [Fact]
        public void AnEmptyButtonSetCrossesTheWireAsEmpty()
        {
            var info = Stickless();
            info.SupportedButtonIndices = Array.Empty<int>();
            info.SupportedAxisIndices = new[] { 0, 1 };

            var round = LinkConnection.DecodeDeviceList(
                LinkConnection.EncodeDeviceList(new[] { info }, ""));

            Assert.NotNull(round[0].SupportedButtonIndices);
            Assert.Empty(round[0].SupportedButtonIndices);

            var dev = new RemotePeerDevice(round[0]);
            Assert.Empty(dev.SupportedButtonIndices);
            Assert.DoesNotContain(dev.GetDeviceObjects(), o => o.IsButton);
        }

        [Fact]
        public void ADenseSetStillArrivesAsNullAndStillCostsOneByte()
        {
            // The dense spelling is unchanged, so a 256-key keyboard still
            // spends one byte per mask instead of thirty-two.
            var kb = new RemotePeerDeviceInfo
            {
                PeerLocalDeviceId = "kb",
                Name = "Keyboard",
                InputDeviceType = InputDeviceType.Keyboard,
                NumButtons = 256,
                RawButtonCount = 256,
                SupportedButtonIndices = Enumerable.Range(0, 256).ToArray(),
            };

            var round = LinkConnection.DecodeDeviceList(
                LinkConnection.EncodeDeviceList(new[] { kb }, ""));

            Assert.Null(round[0].SupportedButtonIndices);
            var dev = new RemotePeerDevice(round[0]);
            // 255 because the raw count rides one clamped byte, which is a
            // separate documented fact. The point here is that it is dense.
            Assert.Equal(255, dev.SupportedButtonIndices.Length);
        }

        [Fact]
        public void AnUnknownSetStaysUnknown()
        {
            var info = new RemotePeerDeviceInfo
            {
                PeerLocalDeviceId = "unknown",
                Name = "Pad",
                InputDeviceType = InputDeviceType.Gamepad,
                NumAxes = 6, RawAxisCount = 6, NumButtons = 22, RawButtonCount = 22,
            };

            var round = LinkConnection.DecodeDeviceList(
                LinkConnection.EncodeDeviceList(new[] { info }, ""));

            Assert.Null(round[0].SupportedButtonIndices);
            Assert.Null(round[0].SupportedAxisIndices);
            var dev = new RemotePeerDevice(round[0]);
            Assert.Equal(22, dev.SupportedButtonIndices.Length);
            Assert.Equal(6, dev.SupportedAxisIndices.Length);
        }

        [Fact]
        public void APeerWithoutTheNewTailFallsBackToDenseNotToEmpty()
        {
            // An older peer stops before the flag tail. The consumer must read
            // dense, which is what it read before the tail existed, and must
            // not conclude the device has nothing.
            var full = LinkConnection.EncodeDeviceList(new[] { Stickless() }, "");
            var older = new byte[full.Length - 2]; // chop the flag tail
            Array.Copy(full, older, older.Length);

            var round = LinkConnection.DecodeDeviceList(older);
            Assert.Single(round);
            Assert.Null(round[0].SupportedAxisIndices);

            var dev = new RemotePeerDevice(round[0]);
            Assert.Equal(6, dev.SupportedAxisIndices.Length);
        }

        [Fact]
        public void ADeviceWithNoControlsAtAllSpendsNoFlag()
        {
            // Empty and dense agree when the count is zero, so the flag has
            // nothing to say and the round trip must not invent a claim.
            var motion = new RemotePeerDeviceInfo
            {
                PeerLocalDeviceId = "motion",
                Name = "Motion Source",
                InputDeviceType = InputDeviceType.Gamepad,
                NumAxes = 0, RawAxisCount = 0, NumButtons = 0, RawButtonCount = 0,
                SupportedButtonIndices = Array.Empty<int>(),
                SupportedAxisIndices = Array.Empty<int>(),
            };

            var round = LinkConnection.DecodeDeviceList(
                LinkConnection.EncodeDeviceList(new[] { motion }, ""));

            Assert.Null(round[0].SupportedButtonIndices);
            Assert.Null(round[0].SupportedAxisIndices);
            var dev = new RemotePeerDevice(round[0]);
            Assert.Empty(dev.SupportedButtonIndices);
            Assert.Empty(dev.SupportedAxisIndices);
        }

        [Fact]
        public void TheKeyboardWrapperEmitsItsRealDenseSet()
        {
            // It has keys, so it may not spell dense as an empty array any
            // more: empty now means the device has no buttons.
            var kb = new SdlKeyboardWrapper();
            typeof(SdlKeyboardWrapper)
                .GetField("_numKeys", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(kb, 256);

            int[] set = kb.SupportedButtonIndices;
            Assert.Equal(256, set.Length);
            Assert.Equal(0, set[0]);
            Assert.Equal(255, set[255]);
            // Cached, not rebuilt on every read.
            Assert.Same(set, kb.SupportedButtonIndices);
        }

        [Fact]
        public void TheConsumerControlWrapperEmitsItsRealDenseSet()
        {
            var cc = new ConsumerControlWrapper();
            int[] set = cc.SupportedButtonIndices;

            Assert.Equal(ConsumerUsageTable.TotalSlots, set.Length);
            Assert.Equal(Enumerable.Range(0, set.Length), set);
            Assert.Same(set, cc.SupportedButtonIndices);
        }
    }
}

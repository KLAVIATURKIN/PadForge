using System.Reflection;
using PadForge.Engine.Common.Mapping;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A source whose button read thresholds on the per-source deadzone has to
    /// show that slider on a button row, in both grid view models. Shake, lean,
    /// the stick and touchpad rings, MIDI pitch bend and inbound rumble all read
    /// it, and none showed a control: a shake row saved 50 and read about 1 g
    /// with nothing to lower it, and a ring's radius was stuck at the global
    /// 50%. The same rule as a setting with no card.
    /// </summary>
    public class ThresholdedButtonFamilyTests
    {
        private static string FirstRumbleDescriptor()
        {
            var field = typeof(SourceCoercion).GetField("RumbleDescriptorTable",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(field);
            return ((string[])field.GetValue(null))[0];
        }

        public static TheoryData<string> Families() => new()
        {
            SourceCoercion.MotionShakeDescriptor,
            SourceCoercion.MotionShakeAuxDescriptor,
            SourceCoercion.MotionLeanDescriptor,
            SourceCoercion.MotionLeanAuxDescriptor,
            SourceCoercion.LeftStickRingDescriptor,
            SourceCoercion.RightStickRingDescriptor,
            "Touchpad 0 Finger 0 Ring",
            "Touchpad 0 Finger 0 Ring Left",
            "Midi Pitch Bend",
        };

        [Theory]
        [MemberData(nameof(Families))]
        public void APrimarySourceOnAButtonRow_ShowsItsThreshold(string descriptor)
        {
            var mi = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
            mi.LoadDescriptor(descriptor);
            Assert.True(mi.IsDeadZoneApplicable, descriptor);
        }

        [Theory]
        [MemberData(nameof(Families))]
        public void AnExtraSourceOnAButtonRow_ShowsItsThreshold(string descriptor)
        {
            var msi = new MappingSourceItem { Descriptor = descriptor, ParentTargetIsDiscrete = true };
            Assert.True(msi.IsDeadZoneApplicable, descriptor);
            var onAxis = new MappingSourceItem { Descriptor = descriptor, ParentTargetIsDiscrete = false };
            Assert.False(onAxis.IsDeadZoneApplicable, descriptor);
        }

        [Fact]
        public void InboundRumble_ShowsItsThreshold_OnBothViewModels()
        {
            string rumble = FirstRumbleDescriptor();
            var mi = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
            mi.LoadDescriptor(rumble);
            Assert.True(mi.IsDeadZoneApplicable, rumble);
            Assert.True(new MappingSourceItem { Descriptor = rumble, ParentTargetIsDiscrete = true }.IsDeadZoneApplicable);
        }

        [Fact]
        public void AStickAxisRow_KeepsTheSliderHidden()
        {
            var mi = new MappingItem("Left Stick X", "LeftThumbAxisX", MappingCategory.LeftStick);
            mi.LoadDescriptor(SourceCoercion.MotionShakeDescriptor);
            Assert.False(mi.IsDeadZoneApplicable);
        }
    }
}

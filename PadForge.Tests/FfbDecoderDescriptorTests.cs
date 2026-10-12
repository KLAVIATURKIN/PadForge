using System;
using System.Reflection;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The force feedback decoder walks a profile's HID descriptor once, when
    /// HMaestroVirtualController.Connect builds it, to scale each report's
    /// magnitude field to the canonical 10000. The walk must take any
    /// descriptor a profile can carry: it runs after HIDMaestro has made the
    /// controller, where a throw fails the whole slot.
    /// </summary>
    public class FfbDecoderDescriptorTests
    {
        /// <summary>A Set Constant Force report (PID usage 0x73) whose
        /// Magnitude (usage 0x70) is declared with the given 4-byte Logical
        /// Minimum and Maximum.</summary>
        private static string ConstantForce(int logicalMin, int logicalMax)
        {
            static string Le(int v) => Convert.ToHexString(BitConverter.GetBytes(v));
            return "050F"                       // Usage Page (Physical Interface)
                + "0973" + "A102"               // Usage (Set Constant Force Report), Collection (Logical)
                + "8505"                        // Report ID (5)
                + "0970"                        // Usage (Magnitude)
                + "17" + Le(logicalMin)         // Logical Minimum, 4 bytes
                + "27" + Le(logicalMax)         // Logical Maximum, 4 bytes
                + "7520" + "9501"               // Report Size (32), Report Count (1)
                + "9102"                        // Output (Data, Variable, Absolute)
                + "C0";                         // End Collection
        }

        private static double ConstantScale(HMaestroFfbDecoder decoder)
        {
            var field = typeof(HMaestroFfbDecoder).GetField("_constantMagScale",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return (double)field.GetValue(decoder);
        }

        /// <summary>A 32-bit field's Logical Minimum is -2147483648, which has
        /// no positive int. The walk took its absolute value as an int and
        /// threw on every Output item read while it stood.</summary>
        [Fact]
        public void AFullRangeThirtyTwoBitMagnitudeScalesAndDoesNotThrow()
        {
            var decoder = new HMaestroFfbDecoder(null, ConstantForce(int.MinValue, int.MaxValue));
            Assert.Equal(10000.0 / 2147483648.0, ConstantScale(decoder), 15);
        }

        /// <summary>Control: the canonical range scales by one, and a small
        /// hand-authored one by its own factor.</summary>
        [Theory]
        [InlineData(-10000, 10000, 1.0)]
        [InlineData(-255, 255, 10000.0 / 255.0)]
        [InlineData(0, 0, 1.0)]
        public void ADeclaredMagnitudeRangeScalesToTenThousand(int logicalMin, int logicalMax, double expected)
        {
            var decoder = new HMaestroFfbDecoder(null, ConstantForce(logicalMin, logicalMax));
            Assert.Equal(expected, ConstantScale(decoder), 12);
        }
    }
}

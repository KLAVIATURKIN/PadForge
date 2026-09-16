using System;
using System.Reflection;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A windowed touchpad finger axis names its finger explicitly, and the
    /// contribution builder must read it.
    ///
    /// <para>The memoized parse matched an exact five-token spelling, so
    /// "Touchpad 0 Finger 1 X Left" reported no explicit finger and the caller
    /// substituted its own default. The active-contact test then looked at the
    /// wrong finger. SourceCoercion.TryParseTouchpadAxis, the validated
    /// grammar, accepts five or six tokens.</para>
    /// </summary>
    public sealed class TouchpadWindowedFingerParseTests
    {
        private static (int Pad, int Finger) Parse(string descriptor)
        {
            var m = typeof(InputManager).GetMethod("ParseTouchpadPadFinger",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.True(m != null, "ParseTouchpadPadFinger is gone");
            var r = m.Invoke(null, new object[] { descriptor });
            var t = r.GetType();
            return ((int)t.GetField("Item1").GetValue(r), (int)t.GetField("Item2").GetValue(r));
        }

        /// <summary>Positive control: the unwindowed spelling always worked
        /// and must keep working.</summary>
        [Theory]
        [InlineData("Touchpad 0 Finger 1 X", 0, 1)]
        [InlineData("Touchpad 1 Finger 0 Y", 1, 0)]
        public void ThePlainFingerAxisStillParses(string d, int pad, int finger)
            => Assert.Equal((pad, finger), Parse(d));

        /// <summary>The windowed forms carry the same explicit finger.</summary>
        [Theory]
        [InlineData("Touchpad 0 Finger 1 X Left", 0, 1)]
        [InlineData("Touchpad 0 Finger 1 X Right", 0, 1)]
        [InlineData("Touchpad 0 Finger 2 Y Upper", 0, 2)]
        [InlineData("Touchpad 1 Finger 0 Y Lower", 1, 0)]
        public void AWindowedFingerAxisReportsItsOwnFinger(string d, int pad, int finger)
            => Assert.Equal((pad, finger), Parse(d));

        /// <summary>A descriptor with no Finger clause still reports -1 so the
        /// caller can substitute its default, and a malformed finger index is
        /// still rejected.</summary>
        [Theory]
        [InlineData("Touchpad 0 Click")]
        [InlineData("Touchpad 0 Finger X Left")]
        public void AnAbsentOrMalformedFingerStillReportsNone(string d)
            => Assert.Equal(-1, Parse(d).Finger);
    }
}

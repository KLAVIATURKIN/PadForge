using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The descriptor parsers the row loops run on every frame read each
    /// descriptor as they did before they stopped allocating. Each test runs
    /// the parse as it stood beside the engine's, over the forms it accepts
    /// and many it refuses, under cultures with a minus sign and an i of
    /// their own, and asserts the same answer, then that the read allocates
    /// nothing.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class DescriptorParseTests : IDisposable
    {
        private readonly Func<int, string, string, bool> _savedHeldBack = SourceCoercion.KeyHeldBackProvider;
        private readonly Func<string, int, SourceCoercion.GyroTuning> _savedTuning = SourceCoercion.GyroTuningProvider;
        private readonly Func<int, string, string, bool> _savedMouseGesture = SourceCoercion.MouseGestureFiredProvider;
        private readonly Func<int, string, int, string, bool> _savedTouchGesture = SourceCoercion.TouchpadGestureFiredProvider;
        private readonly CultureInfo _savedCulture = CultureInfo.CurrentCulture;

        public DescriptorParseTests() => SourceCoercion.KeyHeldBackProvider = null;

        public void Dispose()
        {
            SourceCoercion.KeyHeldBackProvider = _savedHeldBack;
            SourceCoercion.GyroTuningProvider = _savedTuning;
            SourceCoercion.MouseGestureFiredProvider = _savedMouseGesture;
            SourceCoercion.TouchpadGestureFiredProvider = _savedTouchGesture;
            CultureInfo.CurrentCulture = _savedCulture;
        }

        public static TheoryData<string> Cultures() => new() { "", "en-US", "sv-SE", "tr-TR", "ar-SA" };

        private static void UseCulture(string culture)
            => CultureInfo.CurrentCulture = culture.Length == 0 ? CultureInfo.InvariantCulture : new CultureInfo(culture);

        // ── The Up and Down keys ───────────────────────────────────────

        private static readonly string[] KeyForms =
        {
            "Button 0", "Button 1", "Button 7", "Button 31", "Button 127", "Button 255", "Button 256",
            "Button 1000", "Button -1", "Button -0", "Button +3", "Button 03", "Button  3", "Button \t3",
            "Button 3x", "Button x", "Button ", "Button", "Button 2147483647", "Button 2147483648",
            "Button 1,000", "Button 1.0", "Button \u0663", "Button \uFF13", "Button \u22123", " Button 3", "Button 3 ",
            "button 3", "Gamepad ButtonA", "Gamepad ButtonY", "Gamepad Start", "Gamepad Back",
            "Gamepad DPadUp", "Gamepad DPadDown", "Gamepad DPadLeft", "Gamepad DPadRight",
            "POV 0 Up", "POV 0 up", "POV 0 UP", "POV 0 uP", "POV 0 Down", "POV 0 down", "POV 0 Left",
            "POV 0 LEFT", "POV 0 Right", "POV 0 rIGHT", "POV 1 Up", "POV 3 Left", "POV 4 Up", "POV 10 Up",
            "POV -1 Up", "POV -0 Up", "POV +0 Up", "POV 00 Down", "POV 0", "POV 0 ", "POV  0  Up",
            "POV 0  Right", "POV 0 Up Extra", "POV x Up", "POV 0 Upward", "POV 0 U", "POV 0 r\u0130ght",
            "POV 0 R\u0130GHT", "POV 0 \uFF35\uFF50", "POV 0\tUp", "POV\t0 Up", "POV 0 Any", "POV 0 \u0131p",
            "POV \u22120 Up", "POV 0 Up\u00A0", "pov 0 up", "POV", "POV ",
        };

        /// <summary>The key reader's parse of "Button N" and "POV N Dir" as
        /// it stood, with its Substring, Split and ToLowerInvariant.
        /// <paramref name="s"/> is the canonical form.</summary>
        private static bool KeyReadBefore(CustomInputState state, string s, string deviceGuid, int slotIndex)
        {
            if (s.StartsWith("Button ", StringComparison.Ordinal))
            {
                if (int.TryParse(s.Substring(7), out int idx) && idx >= 0 && idx < state.Buttons.Length)
                    return state.Buttons[idx];
                return false;
            }
            var parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && int.TryParse(parts[1], out int povIdx) && povIdx >= 0 && povIdx < state.Povs.Length)
            {
                int v = SourceCoercion.GripPov(deviceGuid, slotIndex, state.Povs[povIdx]);
                if (v < 0) return false;
                int n = ((v % 36000) + 36000) % 36000;
                return parts[2].ToLowerInvariant() switch
                {
                    "up" => n >= 31500 || n <= 4500,
                    "right" => n >= 4500 && n <= 13500,
                    "down" => n >= 13500 && n <= 22500,
                    "left" => n >= 22500 && n <= 31500,
                    _ => false,
                };
            }
            return false;
        }

        /// <summary>The Up and Down pickers' test of the same two forms as
        /// it stood (SourceKindRuntime.ReadsAsKey).</summary>
        private static bool ReadsAsKeyBefore(string s)
        {
            if (s.StartsWith("Button ", StringComparison.Ordinal))
                return int.TryParse(s.Substring(7), out int idx) && idx >= 0 && idx < CustomInputState.MaxButtons;
            var parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !int.TryParse(parts[1], out int povIdx) || povIdx < 0 || povIdx >= CustomInputState.MaxPovs)
                return false;
            return parts[2].ToLowerInvariant() is "up" or "right" or "down" or "left";
        }

        private static bool IsButtonOrHat(string canonical)
            => canonical.StartsWith("Button ", StringComparison.Ordinal) || canonical.StartsWith("POV ", StringComparison.Ordinal);

        /// <summary>A key reads its button or its hat direction as it did,
        /// on every hat angle, with buttons held and not, on a pad held
        /// upright and one held sideways, and the Up and Down pickers offer
        /// the same keys.</summary>
        [Theory]
        [MemberData(nameof(Cultures))]
        public void AKeyReadsAsItDidBeforeItStoppedAllocating(string culture)
        {
            UseCulture(culture);
            var sideways = new SourceCoercion.GyroTuning { Grip = "Sideways" };
            var upright = new SourceCoercion.GyroTuning();
            SourceCoercion.GyroTuningProvider = (g, slot) => g == "grip" && slot == 2 ? sideways : upright;
            int compared = 0;
            foreach (string form in KeyForms)
            {
                string s = SourceCoercion.CanonicalDescriptor(form);
                if (!IsButtonOrHat(s)) continue;
                Assert.True(ReadsAsKeyBefore(s) == SourceKindRuntime.ReadsAsKey(form), $"the pickers on '{form}'");
                foreach (int angle in new[] { -1, 0, 4500, 4501, 9000, 13499, 13500, 18000, 22500, 27000, 31500, 35999, 36000, 40500, -4500 })
                    foreach (bool held in new[] { false, true })
                        foreach (var (device, slot) in new[] { ((string)null, -1), ("pad", 0), ("grip", 2) })
                        {
                            var state = new CustomInputState();
                            for (int i = 0; i < state.Buttons.Length; i++) state.Buttons[i] = held ^ (i % 3 == 0);
                            for (int i = 0; i < state.Povs.Length; i++) state.Povs[i] = angle;
                            bool expected = KeyReadBefore(state, s, device, slot);
                            Assert.True(expected == SourceKindRuntime.ReadKeyOn(state, form, device, slot),
                                $"'{form}' at {angle}, buttons {(held ? "odd" : "even")}, {device ?? "no device"}");
                            compared++;
                        }
            }
            Assert.True(compared > 4000, "compared " + compared);
        }

        /// <summary>A read of a button key, a hat key or a "Gamepad ..." name
        /// for either allocates nothing.</summary>
        [Theory]
        [InlineData("Button 3")]
        [InlineData("Button 300")]
        [InlineData("Button x")]
        [InlineData("POV 0 Up")]
        [InlineData("POV 0 right")]
        [InlineData("POV 9 Up")]
        [InlineData("POV 0 Upward")]
        [InlineData("Gamepad ButtonA")]
        [InlineData("Gamepad DPadLeft")]
        public void AKeyReadAllocatesNothing(string key)
        {
            var state = new CustomInputState();
            state.Buttons[3] = true;
            state.Povs[0] = 9000;
            Assert.Equal(new long[] { 0, 0, 0 },
                AllocationWindow.Clean(() => SourceKindRuntime.ReadKeyOn(state, key, "pad", 0), 1000));
        }

        /// <summary>The positive control of the reads' counts: a read of a
        /// held key whose hold-back test allocates is counted, an allocation
        /// for every read.</summary>
        [Fact]
        public void AnAllocationInsideAKeyReadIsCounted()
        {
            var state = new CustomInputState();
            state.Buttons[3] = true;
            SourceCoercion.KeyHeldBackProvider = AllocationWindow.AllocatingHoldBack;
            foreach (long bytes in AllocationWindow.Clean(() => SourceKindRuntime.ReadKeyOn(state, "Button 3", "pad", 0), 1000))
                Assert.True(bytes >= 1000 * 24, "counted " + bytes);
        }

        // ── The "Gamepad ..." names ────────────────────────────────────

        private static readonly string[] GamepadForms = BuildGamepadForms();

        private static string[] BuildGamepadForms()
        {
            var forms = new List<string>();
            foreach (var (member, _) in SourceCoercion.GamepadAliasTable)
            {
                forms.Add("Gamepad " + member);
                forms.Add("Gamepad  " + member);
                forms.Add("Gamepad " + member + " ");
                forms.Add(" Gamepad " + member);
                forms.Add("Gamepad \t" + member);
                forms.Add("Gamepad " + member + "\u00A0");
                forms.Add("Gamepad " + member.ToLowerInvariant());
                forms.Add("Gamepad " + member.ToUpperInvariant());
                forms.Add("gamepad " + member);
                forms.Add("Gamepad" + member);
                forms.Add("Gamepad " + member + "X");
            }
            forms.AddRange(new[]
            {
                SourceCoercion.LeftStickRingDescriptor, SourceCoercion.RightStickRingDescriptor,
                SourceCoercion.CapSenseLeftStickDescriptor, SourceCoercion.CapSenseRightStickDescriptor,
                SourceCoercion.CapSenseLeftGripDescriptor, SourceCoercion.CapSenseRightGripDescriptor,
                "Gamepad ", "Gamepad", "Gamepad  ", "Gamepad Button A", "Gamepad Touchpad", "Gamepad \u2003ButtonA",
                "Gamepad ButtonA Extra", "Button 0", "", "   ", null,
            });
            return forms.ToArray();
        }

        /// <summary>The alias lookup as it stood, with its Substring, over the
        /// engine's own table.</summary>
        private static string AliasBefore(string descriptor)
        {
            if (string.IsNullOrEmpty(descriptor)) return null;
            string s = descriptor.Trim();
            foreach (var (member, canonical) in SourceCoercion.GamepadAliasTable)
                if (string.Equals(s, "Gamepad " + member, StringComparison.Ordinal)) return canonical;
            if (!s.StartsWith("Gamepad ", StringComparison.Ordinal)) return null;
            string tail = s.Substring("Gamepad ".Length).Trim();
            foreach (var (member, canonical) in SourceCoercion.GamepadAliasTable)
                if (string.Equals(tail, member, StringComparison.Ordinal)) return canonical;
            return null;
        }

        /// <summary>A "Gamepad ..." name resolves as it did: written the usual
        /// way or not, a member of the table or not. A stick ring or a touch
        /// sensor, which no member is, resolves to nothing, as before.</summary>
        [Fact]
        public void AGamepadNameResolvesAsItDidBeforeItStoppedAllocating()
        {
            using var placement = SourceCoercion.UsePlacement(null);
            int members = 0;
            foreach (string form in GamepadForms)
            {
                string expected = AliasBefore(form);
                if (expected != null) members++;
                Assert.True(expected == SourceCoercion.ResolveGamepadAlias(form), $"'{form}'");
                Assert.True((expected?.StartsWith("Axis ", StringComparison.Ordinal) ?? false)
                    == SourceCoercion.IsGamepadAxisAlias(form), $"axis '{form}'");
            }
            Assert.True(members > 100, "members " + members);
        }

        // ── The gyro axes ──────────────────────────────────────────────

        private static readonly string[] GyroForms =
        {
            "Gyro Pitch", "Gyro Yaw", "Gyro Roll", "Gyro Horizontal", "Gyro pitch", "Gyro PITCH", "Gyro hOrIzOnTaL",
            "Gyro  Pitch", "Gyro Pitch ", " Gyro Pitch", "Gyro \tYaw", "Gyro Roll\u00A0", "gyro Pitch", "GYRO Pitch",
            "Gyro L Pitch", "Gyro L Yaw", "Gyro L Roll", "Gyro L Horizontal", "Gyro l pitch", "Gyro L HORIZONTAL",
            "Gyro L  Yaw", "Gyro L", "Gyro L ", "Gyro LPitch", "Gyro R Pitch", "Gyro R Yaw", "Gyro R Roll",
            "Gyro R Horizontal", "Gyro r roll", "Gyro R  Pitch", "Gyro R", "Gyro Lean X", "Gyro Lean Y",
            "Gyro Tilt X", "Gyro Tilt Y", "Gyro ", "Gyro", "Gyro X", "Gyro Pitch Extra", "Gyro Horizontal L",
            "Motion Gyro", "Button 3", "", null,
        };

        /// <summary>The axis parse as it stood, with its Substrings.</summary>
        private static int GyroAxisBefore(string descriptor)
        {
            string s = (descriptor ?? "").Trim();
            if (!s.StartsWith("Gyro ", StringComparison.Ordinal)) return -1;
            string axis = s.Substring(5).Trim();
            if (SourceCoercion.IsGyroAuxDescriptor(s)) axis = axis.Substring(2).Trim();
            else if (SourceCoercion.IsGyroRDescriptor(s)) axis = axis.Substring(2).Trim();
            if (axis.Equals("Pitch", StringComparison.OrdinalIgnoreCase)) return 0;
            if (axis.Equals("Yaw", StringComparison.OrdinalIgnoreCase)) return 1;
            if (axis.Equals("Roll", StringComparison.OrdinalIgnoreCase)) return 2;
            if (axis.Equals("Horizontal", StringComparison.OrdinalIgnoreCase)) return 1;
            return -1;
        }

        /// <summary>The blend test as it stood, with its Substrings.</summary>
        private static bool HorizontalBefore(string descriptor)
        {
            string s = (descriptor ?? "").Trim();
            if (!s.StartsWith("Gyro ", StringComparison.Ordinal)) return false;
            string axis = s.Substring(5).Trim();
            if (SourceCoercion.IsGyroAuxDescriptor(s)) axis = axis.Substring(2).Trim();
            else if (SourceCoercion.IsGyroRDescriptor(s)) axis = axis.Substring(2).Trim();
            return axis.Equals("Horizontal", StringComparison.OrdinalIgnoreCase);
        }

        private static Func<string, T> Private<T>(string name) => (Func<string, T>)Delegate.CreateDelegate(
            typeof(Func<string, T>), typeof(SourceCoercion).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));

        /// <summary>A gyro descriptor names the axis and the blend it did,
        /// the left and right sensors' spellings and the ones their test
        /// refuses included.</summary>
        [Fact]
        public void AGyroAxisParsesAsItDidBeforeItStoppedAllocating()
        {
            var axis = Private<int>("ParseGyroAxisIndex");
            var blend = Private<bool>("IsHorizontalBlendDescriptor");
            foreach (string form in GyroForms)
            {
                Assert.True(GyroAxisBefore(form) == axis(form), $"axis of '{form}'");
                Assert.True(HorizontalBefore(form) == blend(form), $"blend of '{form}'");
            }
        }

        // ── The gesture names ──────────────────────────────────────────

        private static readonly string[] MouseGestureForms =
        {
            "Mouse Gesture Left", "Mouse Gesture Right", "Mouse Gesture Up", "Mouse Gesture Down", "Mouse Gesture Click",
            "Mouse Gesture 0 Up", "Mouse Gesture 3 Left", "Mouse Gesture 4 Click", "Mouse Gesture  Left ",
            "Mouse Gesture \tLeft", "Mouse Gesture Left\u00A0", "Mouse Gesture ", "Mouse Gesture", "mouse gesture Left",
            "Mouse Gestures Left", " Mouse Gesture Left", "Button 3", "", null,
        };

        /// <summary>A mouse gesture names what it did, read once and read
        /// again from memory.</summary>
        [Fact]
        public void AMouseGestureNameParsesAsItDidBeforeItStoppedAllocating()
        {
            foreach (string form in MouseGestureForms)
                for (int pass = 0; pass < 2; pass++)
                {
                    string expected = SourceCoercion.IsMouseGestureDescriptor(form)
                        ? form.Substring("Mouse Gesture ".Length).Trim()
                        : "";
                    Assert.Equal(expected, SourceCoercion.ParseMouseGestureName(form));
                }
        }

        private static readonly string[] TouchGestureForms =
        {
            "Touchpad 0 SwipeUp", "Touchpad 0 SwipeUp Left", "Touchpad 0 SwipeRight Right", "Touchpad 0 TwoFingerTap",
            "Touchpad 1 DoubleTap", "Touchpad 0 RadialZone8_2", "Touchpad 0 Click", "Touchpad 0 Click Left",
            "Touchpad 0 Finger 0 X", "Touchpad 0 Pointer X", "Touchpad  0   SwipeUp   Left  ", "Touchpad 0 A B C D",
            "Touchpad x SwipeUp", "Touchpad -1 SwipeUp", "Touchpad \u22121 SwipeUp", "Touchpad +2 SwipeUp",
            "Touchpad \u0663 SwipeUp", "Touchpad 0", "Touchpad 0 ", "Touchpad", "touchpad 0 SwipeUp",
            "Touchpad 0\tSwipeUp Left", "Button 3", "", null,
        };

        /// <summary>The touchpad gesture parse as it stood, with its
        /// join.</summary>
        private static bool TouchGestureBefore(string descriptor, out int padIdx, out string gestureName)
        {
            padIdx = -1;
            gestureName = null;
            if (string.IsNullOrEmpty(descriptor)) return false;
            if (!descriptor.StartsWith("Touchpad ", StringComparison.Ordinal)) return false;
            var parts = descriptor.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return false;
            if (!int.TryParse(parts[1], out padIdx)) return false;
            if (parts[2].Equals("Click", StringComparison.Ordinal)) return false;
            if (parts[2].Equals("Finger", StringComparison.Ordinal)) return false;
            if (parts[2].Equals("Pointer", StringComparison.Ordinal)) return false;
            gestureName = parts.Length == 3 ? parts[2] : string.Join(" ", parts, 2, parts.Length - 2);
            return true;
        }

        /// <summary>A touchpad gesture parses to the pad and the name it did,
        /// a name of several words included, read once and read again from
        /// memory, under cultures with a minus sign of their own.</summary>
        [Theory]
        [MemberData(nameof(Cultures))]
        public void ATouchpadGestureParsesAsItDidBeforeItStoppedAllocating(string culture)
        {
            UseCulture(culture);
            foreach (string form in TouchGestureForms)
                for (int pass = 0; pass < 2; pass++)
                {
                    bool expected = TouchGestureBefore(form, out int pad, out string name);
                    bool got = SourceCoercion.TryParseTouchpadGesture(form, out int gotPad, out string gotName);
                    Assert.True(expected == got && pad == gotPad && name == gotName,
                        $"'{form}': {expected} {pad} '{name}', read {got} {gotPad} '{gotName}'");
                }
        }

        // ── The reads allocate nothing ─────────────────────────────────

        private const string Pad = "a8a8a8a8-0000-0000-0000-0000000000a8";

        /// <summary>A read of a stick ring, a touch sensor, a gyro axis, a
        /// mouse gesture or a touchpad gesture allocates nothing as a button,
        /// a stick or a trigger, nor as an Up or Down key where it is one. A
        /// ring or a touch sensor cost one to three strings a read, a gyro
        /// axis two to eight, and a gesture name one.</summary>
        [Theory]
        [InlineData("Gamepad LeftStickRing")]
        [InlineData("Gamepad RightStickTouch")]
        [InlineData("Gamepad LeftGripTouch")]
        [InlineData("Gyro Pitch")]
        [InlineData("Gyro Yaw")]
        [InlineData("Gyro Horizontal")]
        [InlineData("Gyro L Pitch")]
        [InlineData("Gyro L Horizontal")]
        [InlineData("Gyro R Roll")]
        [InlineData("Mouse Gesture Left")]
        [InlineData("Mouse Gesture 0 Up")]
        [InlineData("Touchpad 0 SwipeUp")]
        [InlineData("Touchpad 0 SwipeUp Left")]
        public void AReadOfTheFamilyAllocatesNothing(string descriptor)
        {
            SourceCoercion.MouseGestureFiredProvider = (slot, device, name) => name.Length > 0;
            SourceCoercion.TouchpadGestureFiredProvider = (slot, device, pad, name) => name.Length > 0;
            var state = new CustomInputState();
            Array.Fill(state.Axis, 50000);
            Array.Fill(state.Gyro, 3f);
            var touch = new TouchpadInputState(2);
            touch.FingerDown[0] = true;
            touch.FingerX[0] = touch.FingerY[0] = 0.5f;
            state.Touchpads = new[] { touch };
            var src = new MappingSource { Kind = "Direct", DeviceGuid = Pad, Descriptor = descriptor };
            bool key = SourceKindRuntime.ReadsAsKey(descriptor);
            void Read()
            {
                SourceCoercion.EvaluateForButtonTarget(state, src, 50, 0, Pad);
                SourceCoercion.EvaluateForBipolarAxisTarget(state, src, 0, evaluatedDeviceGuid: Pad);
                SourceCoercion.EvaluateForTriggerTarget(state, src, 0, Pad);
                if (key) SourceKindRuntime.ReadKeyOn(state, descriptor, Pad, 0);
            }
            Assert.Equal(new long[] { 0, 0, 0 }, AllocationWindow.Clean(Read, 1000));
        }
    }
}

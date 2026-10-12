using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A row of one input and one Invert on Hold modifier, read the way Step 3
    /// reads a row: the row evaluator takes the input, and the modifier
    /// inverts the row's value while its own input is on
    /// (InputManager.IsInvertOnHoldActive). The tests that read a modifier
    /// through the source evaluator's own Invert on Hold case moved here when
    /// that case went: no row ever handed the evaluator such a source.
    ///
    /// <para>Neither source names a controller, so both read the device of
    /// the pass, whose guid keys the per-device families (a gesture, the grip
    /// of a hat, IR Offscreen). A button row ignores a modifier, so the rows
    /// here are a trigger and a stick.</para>
    /// </summary>
    internal static class RowModifierHarness
    {
        /// <summary>A set with the one row.</summary>
        public static MappingSet Row(string target, string input, string modifier)
        {
            var set = new MappingSet();
            set.Rows.Add(new MappingRow
            {
                Target = target, LayerMask = "Base",
                Sources =
                {
                    new MappingSource { Kind = "Direct", Descriptor = input },
                    new MappingSource { Kind = "InvertOnHold", ParamModifier = modifier },
                },
            });
            return set;
        }

        /// <summary>The LeftTrigger row's pull on a pass of
        /// <paramref name="passDevice"/>: short.MinValue released,
        /// short.MaxValue fully pulled.</summary>
        public static short TriggerPull(MappingSet set, CustomInputState state, string passDevice, int slot)
        {
            InputManager.EndDeviceStateMemo();
            InputManager.ClearAllShiftRuntime();
            Assert.True(InputManager.TryEvaluateMappingSetRawTrigger(state, set, passDevice, slot, "LeftTrigger", out short pull));
            return pull;
        }

        /// <summary>The LeftThumbAxisX row's deflection on a pass of
        /// <paramref name="passDevice"/>.</summary>
        public static short StickX(MappingSet set, CustomInputState state, string passDevice, int slot)
        {
            InputManager.EndDeviceStateMemo();
            InputManager.ClearAllShiftRuntime();
            Assert.True(InputManager.TryEvaluateMappingSetBipolarAxis(state, set, passDevice, slot, "LeftThumbAxisX", out short value));
            return value;
        }
    }
}

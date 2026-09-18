using System;

namespace PadForge.Engine.Common.OpenXr
{
    /// <summary>Which hand a source speaks for.</summary>
    public enum OpenXrHand
    {
        Left = 0,
        Right = 1,
    }

    /// <summary>
    /// One motion controller, as one sample read it (issue #403).
    ///
    /// <para>Validity is per-part on purpose. A runtime reports a pose it has
    /// stopped tracking, and it reports controls for an action set that is
    /// not synchronized, and both look like real data. Publishing either
    /// pins whatever a user last did.</para>
    /// </summary>
    public struct OpenXrHandState
    {
        /// <summary>The grip pose is valid and tracked. The pose fields mean
        /// nothing when this is false.</summary>
        public bool PoseValid;

        /// <summary>Position and orientation in the same convention the head
        /// uses: centimeters and degrees, position relative to a captured
        /// neutral.</summary>
        public double TX, TY, TZ, YawDeg, PitchDeg, RollDeg;

        /// <summary>The action set was synchronized and these controls are
        /// live. False while another application holds focus, which is the
        /// ordinary case for a background client.</summary>
        public bool ControlsActive;

        public float Trigger;       // 0..1
        public float Squeeze;       // 0..1
        public float ThumbstickX;   // -1..1
        public float ThumbstickY;   // -1..1

        public bool ThumbstickClick;
        public bool PrimaryButton;   // A on the right hand, X on the left
        public bool SecondaryButton; // B on the right hand, Y on the left
        public bool MenuButton;

        /// <summary>Nothing of this hand is usable.</summary>
        public bool IsIdle => !PoseValid && !ControlsActive;

        public static OpenXrHandState Empty => default;
    }
}

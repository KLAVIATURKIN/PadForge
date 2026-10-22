namespace PadForge.Common.Input
{
    /// <summary>
    /// The rotation range a native-FFB wheel takes, from the writer that sends
    /// it (Logitech, Fanatec, Thrustmaster). The Wheel tab's slider follows it,
    /// so the range it shows is the range the wheel gets: it offered 40° to
    /// 2,520° on every wheel while each writer clamped to its model in silence,
    /// and a TS-PC set to 40° was sent 140° (discussion #512).
    /// </summary>
    internal static class WheelRangeLimits
    {
        /// <summary>The bounds with no native wheel selected, the widest any
        /// writer takes.</summary>
        public const int AnyMin = 40;
        public const int AnyMax = 2520;

        /// <summary>The model's range in degrees, or null for a device that no
        /// native writer drives.</summary>
        public static (int Min, int Max)? For(ushort vendorId, ushort productId)
        {
            if (LogitechRawHidWriter.IsLogitechWheel(vendorId, productId))
                return LogitechRawHidWriter.RangeLimits(productId);
            if (FanatecRawHidWriter.IsFanatecWheel(vendorId, productId))
                return FanatecRawHidWriter.RangeLimits(productId);
            if (ThrustmasterRawHidWriter.IsThrustmasterWheel(vendorId, productId))
                return ThrustmasterRawHidWriter.RangeLimits(productId);
            return null;
        }
    }
}

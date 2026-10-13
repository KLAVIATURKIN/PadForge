namespace PadForge.Engine.Common
{
    /// <summary>
    /// The gate in front of the lighting and haptics programs other vendors
    /// run on this PC: Razer's Chroma server and its Sensa runtime,
    /// SteelSeries GG's GameSense server, and Logitech's LED engine and
    /// G-key library, which belong to G HUB.
    ///
    /// <para>In the app the gate is always open, and passing it is one read
    /// of a flag. The test assembly closes it for the life of its process
    /// (VendorProgramsOffLimits in PadForge.Tests). A test stands a server
    /// of its own in for a vendor's, or hands a backend a fake, and those
    /// run as before. A test, or a build of the code with one of its checks
    /// removed, that reaches the vendor's own program is answered as on a
    /// PC that does not have it: no game is registered with it, no session
    /// opened, and nothing lit or shaken.</para>
    /// </summary>
    public static class VendorProgramGate
    {
        /// <summary>Set by the test assembly for the life of its process,
        /// never by the app. False in production, where every call is as it
        /// was.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>True in the test host.</summary>
        public static bool Refuses => OffLimitsForTest;
    }
}

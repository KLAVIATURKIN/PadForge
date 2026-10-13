namespace PadForge.Common.Input
{
    /// <summary>
    /// Whether this process may play sound on an audio endpoint of the PC or
    /// change one of its sound settings. PadForge plays in three places: a
    /// sound macro on the default output, rumble on a chosen endpoint, and a
    /// controller's own speaker over USB. It changes a setting in four: the
    /// master volume and a program's volume in the mixer, both from a macro,
    /// an endpoint it switches on, and the default output it puts back.
    /// Always yes in the app, where nothing sets the flag.
    ///
    /// <para>The test host says no for its whole life (AudioEndpointOffLimits
    /// in PadForge.Tests). A test hands a service a player of its own. A
    /// test, or a build of the code with one of its checks removed, that
    /// reaches a real endpoint plays nothing and changes nothing, so the
    /// bench stays as quiet and as loud as its owner left it. Captures only
    /// listen and are not asked about.</para>
    /// </summary>
    internal static class AudioEndpointAccess
    {
        /// <summary>Set by the test assembly for the life of its process,
        /// never by the app. False in production, where every call is as it
        /// was.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>True in the test host.</summary>
        internal static bool Refuses => OffLimitsForTest;
    }
}

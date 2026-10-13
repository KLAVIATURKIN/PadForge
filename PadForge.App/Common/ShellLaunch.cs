using System.Diagnostics;

namespace PadForge.Common
{
    /// <summary>
    /// The one way the app hands the user's shell something: a web page, a
    /// folder or a file to open, the program a Run Program macro names, and
    /// text for the clipboard.
    ///
    /// <para>In the app every call goes straight through. The test host
    /// hands the shell nothing (ShellOffLimits in PadForge.Tests): a test
    /// that reached one of these, or a build of the code with a check
    /// removed, would open a browser tab, an Explorer window or a program on
    /// the desktop the tests run on, or replace what its owner had
    /// copied.</para>
    /// </summary>
    internal static class ShellLaunch
    {
        /// <summary>Set by the test assembly for the life of its process,
        /// never by the app. False in production, where every call is as it
        /// was.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>Starts what <paramref name="psi"/> names. The test host
        /// starts nothing and is answered no process, as the shell answers a
        /// launch it took over without starting one.</summary>
        internal static Process Start(ProcessStartInfo psi)
            => OffLimitsForTest ? null : Process.Start(psi);

        /// <summary>The same for a program and its arguments.</summary>
        internal static Process Start(string fileName, string arguments)
            => OffLimitsForTest ? null : Process.Start(fileName, arguments);

        /// <summary>Puts text on the clipboard. The test host leaves the
        /// clipboard as it is.</summary>
        internal static void SetClipboardText(string text)
        {
            if (OffLimitsForTest) return;
            System.Windows.Clipboard.SetText(text);
        }
    }
}

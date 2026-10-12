using System;

namespace PadForge.Engine.Menus
{
    /// <summary>
    /// The menu runtime's clock, in milliseconds. Every time the menu runtime
    /// writes down comes from here, and so does every "now" one of them is
    /// weighed against: a commit's pulse, a menu context's last tick, the
    /// stay-open driver and overlay stamps, a layer-exit commit's end and a
    /// phone tile's press. A stamp is then never weighed against a clock
    /// other than the one that wrote it. It is the system tick count unless
    /// a test holds it.
    /// </summary>
    public static class MenuClock
    {
        private static readonly Func<long> s_system = () => Environment.TickCount64;
        private static volatile Func<long> s_source = s_system;

        /// <summary>The time now.</summary>
        public static long NowMs => s_source();

        /// <summary>Test seam (InternalsVisibleTo PadForge.Tests): the source
        /// of <see cref="NowMs"/>. A test sets one it moves by hand, so a
        /// commit's pulse stays live for as many frames as the test runs and
        /// lapses when the test says so, however long the test itself takes.
        /// Null puts the system tick count back, which a test that set a
        /// source does when it ends.</summary>
        internal static Func<long> Source
        {
            get => s_source;
            set => s_source = value ?? s_system;
        }
    }
}

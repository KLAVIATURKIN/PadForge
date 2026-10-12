using System.Reflection;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The engine's teardown runs only through Dispose. It joins threads and
    /// shuts down services every manager shares (voice macros, the NFC
    /// reader, the handheld chord runtime). A finalizer that called it ran
    /// all of that on the finalizer thread whenever a dropped manager was
    /// collected, which in the tests is hundreds of managers, each under
    /// whatever test happened to be running.
    /// </summary>
    public class InputManagerTeardownTests
    {
        [Fact]
        public void TheManagerDeclaresNoFinalizer()
        {
            var finalizer = typeof(InputManager).GetMethod("Finalize",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Assert.Null(finalizer);
        }

        [Fact]
        public void ThePinSeesAFinalizerWhereOneIsDeclared()
        {
            var finalizer = typeof(WithFinalizer).GetMethod("Finalize",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Assert.NotNull(finalizer);
        }

        private sealed class WithFinalizer
        {
            ~WithFinalizer() { }
        }
    }
}

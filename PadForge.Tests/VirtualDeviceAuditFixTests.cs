using System;
using System.Linq;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Guard pins for the virtual device findings in the 2026-09-15 audit.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class VirtualDeviceAuditFixTests
    {
        private static T Field<T>(InputManager im, string name)
        {
            var f = typeof(InputManager).GetField(name,
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(f);
            return (T)f.GetValue(im);
        }

        // C72: the ordering-deadlock bound was off for one pad index.

        /// <summary>The blocker slot starts at the not-blocked sentinel, not
        /// at zero. Zero is pad 0, a real blocker index, so a slot blocked by
        /// pad 0 read as already waiting on it, never stamped a start tick,
        /// and left the tick at zero, which the expiry test treats as never.
        /// The 45 second bound that breaks an ordering deadlock was disabled
        /// for exactly that case.</summary>
        [Fact]
        public void TheOrderWaitBlockerStartsAtTheNotBlockedSentinel()
        {
            var im = new InputManager();
            var blockers = Field<int[]>(im, "_orderWaitBlocker");

            Assert.NotEmpty(blockers);
            Assert.All(blockers, b => Assert.Equal(-1, b));
        }

        /// <summary>The sentinel really is the value the gate writes when it
        /// stops waiting, so seeding to it is the same "not blocked" state
        /// and not a new one.</summary>
        [Fact]
        public void TheSentinelMatchesWhatTheGateWritesWhenItClears()
        {
            string src = System.IO.File.ReadAllText(RepoPath(
                "PadForge.App", "Common", "Input", "InputManager.Step5.VirtualDevices.cs"));
            Assert.Contains("_orderWaitBlocker[padIndex] = -1;", src);
        }

        /// <summary>A zero start tick never expires, which is the contract the
        /// seeding has to work with rather than change.</summary>
        [Fact]
        public void AZeroStartTickStillNeverExpires()
        {
            Assert.False(InputManager.OrderWaitExpired(0, 0));
            Assert.False(InputManager.OrderWaitExpired(0, long.MaxValue / 2));
        }

        // C74: the Customize flag had no applied counterpart.

        /// <summary>The Customize flag decides whether an Extended slot gets
        /// the catalog profile's descriptor or a generic build, so flipping it
        /// changes the wire. The drift test compared eight other values, all
        /// of which can be identical across the flip, so ticking or clearing
        /// the box rebuilt nothing and the live device kept the descriptor it
        /// was born with.</summary>
        [Fact]
        public void TheExtendedDriftTestComparesTheCustomizeFlag()
        {
            string src = System.IO.File.ReadAllText(RepoPath(
                "PadForge.App", "Common", "Input", "InputManager.Step5.VirtualDevices.cs"));
            int i = src.IndexOf("private bool ExtendedConfigurationChanged(", StringComparison.Ordinal);
            Assert.True(i > 0);
            int end = src.IndexOf("\n        }", i, StringComparison.Ordinal);
            string body = src.Substring(i, end - i);

            Assert.Contains("SlotExtendedCustomize[padIndex] != _extendedAppliedCustomize[padIndex]", body);
        }

        /// <summary>The applied value is published when a controller wins its
        /// slot and cleared when one is destroyed, like every other member of
        /// the applied set. Without both, the comparison drifts.</summary>
        [Fact]
        public void TheAppliedCustomizeFlagIsPublishedAndReset()
        {
            string src = System.IO.File.ReadAllText(RepoPath(
                "PadForge.App", "Common", "Input", "InputManager.Step5.VirtualDevices.cs"));
            Assert.Contains("_extendedPendingCustomize[padIndex] = build.Customize;", src);
            Assert.Contains("_extendedAppliedCustomize[padIndex] = _extendedPendingCustomize[padIndex];", src);
            Assert.Contains("_extendedAppliedCustomize[padIndex] = false;", src);
        }

        // C71: the type change cleared a slug the caller had just published.

        /// <summary>A slug belonging to the new category survives the type
        /// change. The profile-apply path publishes the type and the slug in
        /// one pass, so clearing unconditionally built the slot on the default
        /// and then tore it down and rebuilt it on the intended profile.</summary>
        [Theory]
        [InlineData(VirtualControllerType.Xbox)]
        [InlineData(VirtualControllerType.PlayStation)]
        [InlineData(VirtualControllerType.Extended)]
        public void AnEmptySlugBelongsToEveryType(VirtualControllerType type)
        {
            var m = typeof(InputManager).GetMethod("ProfileBelongsToType",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);
            Assert.True((bool)m.Invoke(null, new object[] { null, type }));
            Assert.True((bool)m.Invoke(null, new object[] { "", type }));
        }

        /// <summary>A slug from another category does not belong, so the
        /// clear still fires where it was written to fire.</summary>
        [Fact]
        public void ASlugFromAnotherCategoryDoesNotBelong()
        {
            var m = typeof(InputManager).GetMethod("ProfileBelongsToType",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.False((bool)m.Invoke(null, new object[] { "no-such-profile-slug", VirtualControllerType.Xbox }));
        }

        // C19 and C22: the controller's own teardown and feedback contracts.

        /// <summary>The retirement is published under the dispatcher lock,
        /// before the controller dispose that can take seconds. Clearing the
        /// field first and flipping the connected flag afterwards left an
        /// attach window that wide, and a caller arriving in it saw connected
        /// plus null and built a dispatcher that outlived this controller.</summary>
        [Fact]
        public void TheRetirementIsPublishedUnderTheDispatcherLock()
        {
            string src = System.IO.File.ReadAllText(RepoPath(
                "PadForge.App", "Common", "Input", "HMaestroVirtualController.cs"));
            int lockAt = src.IndexOf("_retiring = true;", StringComparison.Ordinal);
            Assert.True(lockAt > 0, "the retirement flag is gone");

            int disposeAt = src.IndexOf("_controller?.Dispose();", StringComparison.Ordinal);
            Assert.True(disposeAt > lockAt,
                "the controller dispose runs before the retirement is published");

            // And the readers consult it.
            Assert.Contains("if (!IsConnected || _retiring) return;", src);
        }

        /// <summary>The pulse-train stop carries the generation it was armed
        /// with. A compare on the motor value could not tell a newer pulse of
        /// equal strength from the one it queued against, so two pulses of the
        /// same amplitude and different lengths each cut the other short.</summary>
        [Fact]
        public void ThePulseTrainStopIsBoundToItsOwnCommand()
        {
            string src = System.IO.File.ReadAllText(RepoPath(
                "PadForge.App", "Common", "Input", "HMaestroVirtualController.cs"));
            Assert.Contains("long myGen = System.Threading.Interlocked.Increment(ref _feedbackGeneration);", src);
            Assert.Contains("System.Threading.Volatile.Read(ref _feedbackGeneration) == myGen", src);
            // And it re-reads the slot instead of trusting the captured index.
            Assert.Contains("int liveIdx = FeedbackPadIndex;", src);
        }

        private static string RepoPath(params string[] parts)
        {
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return System.IO.Path.Combine(d.FullName, System.IO.Path.Combine(parts));
        }
    }
}

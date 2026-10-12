using System;
using System.Collections.Generic;
using System.Globalization;
using PadForge.Engine.Common;
using PadForge.Engine.Common.OpenXr;
using Xunit;
using static PadForge.Engine.Common.OpenXr.OpenXrInterop;

namespace PadForge.Tests
{
    /// <summary>
    /// The real <see cref="OpenXrSession"/> on a scripted runtime: the half of
    /// the session recovery that <see cref="OpenXrSessionRecoveryTests"/>
    /// scripts away behind its link. The session is driven on the test's own
    /// thread, and the runtime's clock is a number the test sets, so nothing
    /// here waits and nothing is loaded. The last tests play the source's
    /// whole loop over the real session the same way.
    ///
    /// <para>Result codes are written as the numbers openxr.h gives them. A
    /// constant that drifts in <see cref="OpenXrInterop"/> then fails here,
    /// where a test written against the constant would drift with it.</para>
    /// </summary>
    public class OpenXrSessionScriptedRuntimeTests
    {
        // openxr.h, XrResult.
        private const int SessionLossPending = 3;
        private const int RuntimeFailure = -2;
        private const int InitializationFailed = -6;
        private const int InstanceLost = -13;
        private const int SessionLost = -17;
        private const int PathUnsupported = -22;
        private const int ReferenceSpaceUnsupported = -31;
        private const int FormFactorUnsupported = -34;
        private const int FormFactorUnavailable = -35;
        private const int ViewConfigurationTypeUnsupported = -41;
        private const int RuntimeUnavailable = -51;

        // openxr.h, XrStructureType.
        private const int InteractionProfileChanged = 52;

        /// <summary>A number as an expected line holds it: ASCII digits
        /// behind an ASCII minus, whatever regional format the test runs
        /// under.</summary>
        private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

        private static OpenXrSession Open(OpenXrScriptedRuntime rt, List<string> log = null)
        {
            var session = rt.Open(line => log?.Add(line), out var failure, out bool runtimeUnavailable, out string read);
            Assert.NotNull(session);
            Assert.Equal(OpenXrSourceState.Connecting, failure);
            Assert.False(runtimeUnavailable);
            Assert.Null(read);
            return session;
        }

        private static void Begin(OpenXrScriptedRuntime rt, OpenXrSession session)
        {
            rt.QueueSessionState(XR_SESSION_STATE_READY);
            Assert.Equal(OpenXrSessionEnd.None, session.PumpEvents(out _, out _));
        }

        private static bool HandsAreLive(OpenXrSession session)
        {
            var left = default(OpenXrHeadPose.Baseline);
            var right = default(OpenXrHeadPose.Baseline);
            session.ReadHands(ref left, ref right, out var leftHand, out var rightHand);
            return leftHand.ControlsActive && rightHand.ControlsActive
                && leftHand.PoseValid && rightHand.PoseValid;
        }

        /// <summary>The event types, session states and result codes the
        /// recovery tells apart, against the numbers openxr.h gives them. The
        /// scripted runtime speaks through the constants the session reads,
        /// so a number that drifted would agree with itself on both sides.</summary>
        [Fact]
        public void TheNumbersTheRecoveryReadsAreTheOnesOpenXrDefines()
        {
            Assert.Equal(17, XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING);
            Assert.Equal(18, XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED);
            Assert.Equal(2, XR_SESSION_STATE_READY);
            Assert.Equal(6, XR_SESSION_STATE_STOPPING);
            Assert.Equal(7, XR_SESSION_STATE_LOSS_PENDING);
            Assert.Equal(8, XR_SESSION_STATE_EXITING);
            Assert.Equal(SessionLossPending, XR_SESSION_LOSS_PENDING);
            Assert.Equal(4, XR_EVENT_UNAVAILABLE);
            Assert.Equal(InstanceLost, XR_ERROR_INSTANCE_LOST);
            Assert.Equal(SessionLost, XR_ERROR_SESSION_LOST);
            Assert.Equal(FormFactorUnavailable, XR_ERROR_FORM_FACTOR_UNAVAILABLE);
            Assert.Equal(RuntimeUnavailable, XR_ERROR_RUNTIME_UNAVAILABLE);
        }

        /// <summary>The stand-in is a runtime the session can live a whole
        /// life on: an instance, a headless session, its controllers, a pose
        /// asked for at the runtime's own time, and everything destroyed
        /// again in order.</summary>
        [Fact]
        public void ASessionIsCreatedBegunReadAndDestroyed()
        {
            using var rt = new OpenXrScriptedRuntime();
            using (var session = Open(rt))
            {
                Assert.False(session.HasSession);
                Assert.Equal(OpenXrSessionEnd.None, session.PumpEvents(out _, out string nothing));
                Assert.Null(nothing);

                Assert.Equal(OpenXrSessionCreate.Created, session.TryCreateSession(out var state, out string read));
                Assert.Equal(OpenXrSourceState.Running, state);
                Assert.Null(read);
                Assert.True(session.HasSession);
                Assert.True(session.HasControllers);
                // Created is not begun, and only a begun session has a pose.
                Assert.False(session.TryLocateHead(out _, out _));

                Begin(rt, session);
                Assert.Equal(1, rt.Begins);
                Assert.True(session.TryLocateHead(out _, out var orientation));
                Assert.Equal(1f, orientation.w);
                Assert.Equal(rt.Now, rt.LastLocateTime);
                Assert.True(HandsAreLive(session));
            }

            Assert.Empty(rt.Faults);
            Assert.False(rt.InstanceAlive);
            Assert.Equal(1, rt.SessionsDestroyed);
            Assert.Equal(1, rt.ActionSetsDestroyed);
            Assert.Equal(0, rt.LiveSpaces);
        }

        /// <summary>XR_ERROR_FORM_FACTOR_UNAVAILABLE, -35, is a headset that
        /// is away and may be back (system.adoc, xrGetSystem). The answer is
        /// HeadsetUnavailable with the call and its result, nothing is
        /// destroyed, and a later ask on the same instance creates the
        /// session.</summary>
        [Fact]
        public void AHeadsetThatIsAwayIsAskedForAgainOnTheSameInstance()
        {
            using var rt = new OpenXrScriptedRuntime();
            rt.SystemAnswers.Enqueue(FormFactorUnavailable);
            rt.SystemAnswers.Enqueue(FormFactorUnavailable);
            using (var session = Open(rt))
            {
                Assert.Equal(OpenXrSessionCreate.HeadsetUnavailable, session.TryCreateSession(out var state, out string read));
                Assert.Equal(OpenXrSourceState.NoHeadset, state);
                Assert.Equal("xrGetSystem answered -35", read);
                Assert.Equal(OpenXrSessionCreate.HeadsetUnavailable, session.TryCreateSession(out _, out _));
                Assert.False(session.HasSession);
                Assert.True(rt.InstanceAlive);
                Assert.Equal(0, rt.SessionsCreated);

                Assert.Equal(OpenXrSessionCreate.Created, session.TryCreateSession(out _, out _));
                Assert.Equal(3, rt.SystemAsks);
                Assert.Equal(1, rt.InstancesCreated);
            }
            Assert.Empty(rt.Faults);
        }

        /// <summary>XR_ERROR_INSTANCE_LOST, -13, from the ask for the headset
        /// is a lost instance, and that ends as Ended, with the answer handed
        /// back for the source's line. The link writes nothing itself.</summary>
        [Fact]
        public void AnInstanceLostAtTheAskForTheHeadsetEndsAsEnded()
        {
            using var rt = new OpenXrScriptedRuntime();
            rt.SystemAnswers.Enqueue(InstanceLost);
            var log = new List<string>();
            using (var session = Open(rt, log))
            {
                Assert.Equal(OpenXrSessionCreate.Failed, session.TryCreateSession(out var state, out string read));
                Assert.Equal(OpenXrSourceState.Ended, state);
                Assert.Equal("xrGetSystem answered -13", read);
                Assert.False(session.HasSession);
            }
            Assert.Empty(log);
            Assert.Empty(rt.Faults);
        }

        /// <summary>Any other error from the ask for the headset is a call
        /// that failed. It reads as Failed, never as the line of a headset
        /// that is only away, at the first ask as after a wait, and the
        /// answer comes back with its code.</summary>
        [Theory]
        [InlineData(RuntimeFailure, 0)]
        [InlineData(RuntimeFailure, 2)]
        [InlineData(FormFactorUnsupported, 0)]
        [InlineData(FormFactorUnsupported, 2)]
        public void AnErrorFromTheAskForTheHeadsetIsAFailedCall(int answer, int asksBefore)
        {
            using var rt = new OpenXrScriptedRuntime();
            for (int i = 0; i < asksBefore; i++) rt.SystemAnswers.Enqueue(FormFactorUnavailable);
            rt.SystemAnswers.Enqueue(answer);
            var log = new List<string>();
            using (var session = Open(rt, log))
            {
                for (int i = 0; i < asksBefore; i++)
                    Assert.Equal(OpenXrSessionCreate.HeadsetUnavailable, session.TryCreateSession(out _, out _));

                Assert.Equal(OpenXrSessionCreate.Failed, session.TryCreateSession(out var state, out string read));
                Assert.Equal(OpenXrSourceState.Failed, state);
                Assert.Equal($"xrGetSystem answered {N(answer)}", read);
                Assert.False(session.HasSession);
            }
            Assert.Empty(log);
            Assert.Equal(0, rt.SessionsCreated);
            Assert.Empty(rt.Faults);
        }

        /// <summary>A session the runtime refuses reads as a runtime that
        /// cannot supply one, with the call and its code handed back. A lost
        /// instance is the exception: xrCreateSession reports that too, and
        /// it ends as Ended whichever call says it.</summary>
        [Theory]
        [InlineData(InitializationFailed, OpenXrSourceState.NotSupported)]
        [InlineData(RuntimeFailure, OpenXrSourceState.NotSupported)]
        [InlineData(InstanceLost, OpenXrSourceState.Ended)]
        public void ARefusedSessionSaysWhy(int answer, OpenXrSourceState expected)
        {
            using var rt = new OpenXrScriptedRuntime();
            rt.SessionAnswers.Enqueue(answer);
            var log = new List<string>();
            using (var session = Open(rt, log))
            {
                Assert.Equal(OpenXrSessionCreate.Failed, session.TryCreateSession(out var state, out string read));
                Assert.Equal(expected, state);
                Assert.StartsWith($"xrCreateSession answered {N(answer)} ", read);
                Assert.False(session.HasSession);
            }
            Assert.Empty(log);
            Assert.Empty(rt.Faults);
        }

        /// <summary>With no reference space to locate against, the session
        /// half built is destroyed again, and the answer that left it without
        /// one is handed back. A lost instance ends as Ended here too.</summary>
        [Theory]
        [InlineData(XR_REFERENCE_SPACE_TYPE_VIEW, ReferenceSpaceUnsupported, OpenXrSourceState.NotSupported, "view space")]
        [InlineData(XR_REFERENCE_SPACE_TYPE_LOCAL, ReferenceSpaceUnsupported, OpenXrSourceState.NotSupported, "local space")]
        [InlineData(XR_REFERENCE_SPACE_TYPE_LOCAL, InstanceLost, OpenXrSourceState.Ended, "local space")]
        [InlineData(XR_REFERENCE_SPACE_TYPE_VIEW, InstanceLost, OpenXrSourceState.Ended, "view space")]
        public void ASessionWithNoUsableReferenceSpaceSaysWhy(int missing, int answer, OpenXrSourceState expected, string space)
        {
            using var rt = new OpenXrScriptedRuntime();
            // The stage space is asked for first and the local space stands
            // in for it, so a base space is missing only when both are.
            rt.SpaceAnswers[XR_REFERENCE_SPACE_TYPE_STAGE] = ReferenceSpaceUnsupported;
            rt.SpaceAnswers[missing] = answer;
            var log = new List<string>();
            using (var session = Open(rt, log))
            {
                Assert.Equal(OpenXrSessionCreate.Failed, session.TryCreateSession(out var state, out string read));
                Assert.Equal(expected, state);
                Assert.StartsWith($"xrCreateReferenceSpace answered {N(answer)} for the {space}", read);
                Assert.False(session.HasSession);
                Assert.Equal(1, rt.SessionsDestroyed);
                Assert.Equal(0, rt.LiveSpaces);
            }
            Assert.Empty(log);
            Assert.Empty(rt.Faults);
        }

        /// <summary>The stage space is preferred and the local space stands
        /// in for it, so a runtime with no stage space still gives a session.</summary>
        [Fact]
        public void ARuntimeWithNoStageSpaceLocatesAgainstTheLocalOne()
        {
            using var rt = new OpenXrScriptedRuntime();
            rt.SpaceAnswers[XR_REFERENCE_SPACE_TYPE_STAGE] = ReferenceSpaceUnsupported;
            using (var session = Open(rt))
            {
                Assert.Equal(OpenXrSessionCreate.Created, session.TryCreateSession(out _, out _));
                Begin(rt, session);
                Assert.True(session.TryLocateHead(out _, out _));
            }
            Assert.Empty(rt.Faults);
        }

        /// <summary>XR_SESSION_LOSS_PENDING, 3, is a success code: a session
        /// that is being lost simulates success, and its loss arrives as an
        /// event (session.adoc, XR_SESSION_STATE_LOSS_PENDING). A session
        /// lost while it is being created is therefore a session, and the
        /// pump reads its loss, where a failure here would stop a source
        /// whose recovery the next ask was about to continue.</summary>
        [Fact]
        public void ASessionLostWhileItIsCreatedIsCreatedAndThenReadAsLost()
        {
            using var rt = new OpenXrScriptedRuntime();
            rt.SpaceAnswers[XR_REFERENCE_SPACE_TYPE_STAGE] = SessionLossPending;
            rt.SpaceAnswers[XR_REFERENCE_SPACE_TYPE_VIEW] = SessionLossPending;
            rt.OnSessionCreated = _ => rt.QueueSessionState(XR_SESSION_STATE_LOSS_PENDING);
            using (var session = Open(rt))
            {
                Assert.Equal(OpenXrSessionCreate.Created, session.TryCreateSession(out _, out _));
                Assert.True(session.HasSession);
                Assert.Equal(OpenXrSessionEnd.SessionLost, session.PumpEvents(out _, out string read));
                Assert.Equal("XR_SESSION_STATE_LOSS_PENDING", read);
                session.DestroySession();
                Assert.Equal(0, rt.LiveSpaces);
            }
            Assert.Empty(rt.Faults);
        }

        /// <summary>
        /// XR_ERROR_SESSION_LOST, -17, from xrCreateReferenceSpace is a
        /// session the runtime lost as it was created, with no grace period
        /// (session.adoc, XR_SESSION_STATE_LOSS_PENDING). It is not a
        /// runtime that cannot supply a session. What was built is
        /// destroyed, the instance stays, and the answer is SessionLost
        /// with the call and its code, so the source replaces it as it
        /// replaces a session whose loss the pump reads. The link writes
        /// nothing about it, and the next ask on the same instance gives a
        /// session that runs. A lost session answers every call the same
        /// way, so the base spaces answer it too in the second row.
        /// </summary>
        [Theory]
        [InlineData(false, "view space")]
        [InlineData(true, "local space, after the stage space")]
        public void ASessionLostAsItIsCreatedIsLetGoAndSaidLost(bool atEverySpace, string space)
        {
            using var rt = new OpenXrScriptedRuntime();
            rt.SpaceAnswers[XR_REFERENCE_SPACE_TYPE_VIEW] = SessionLost;
            if (atEverySpace)
            {
                rt.SpaceAnswers[XR_REFERENCE_SPACE_TYPE_STAGE] = SessionLost;
                rt.SpaceAnswers[XR_REFERENCE_SPACE_TYPE_LOCAL] = SessionLost;
            }
            var log = new List<string>();
            using (var session = Open(rt, log))
            {
                Assert.Equal(OpenXrSessionCreate.SessionLost, session.TryCreateSession(out _, out string read));
                Assert.Equal($"xrCreateReferenceSpace answered -17 for the {space}", read);
                Assert.False(session.HasSession);
                Assert.Equal(1, rt.SessionsDestroyed);
                Assert.Equal(0, rt.LiveSpaces);
                Assert.True(rt.InstanceAlive);
                Assert.Empty(log);

                rt.SpaceAnswers.Clear();
                Assert.Equal(OpenXrSessionCreate.Created, session.TryCreateSession(out _, out _));
                Begin(rt, session);
                Assert.True(session.TryLocateHead(out _, out _));
            }
            Assert.Equal(1, rt.InstancesCreated);
            Assert.Empty(rt.Faults);
        }

        /// <summary>A runtime that hands out no xrGetSystem cannot be asked
        /// for a headset at all, and one with no xrBeginSession cannot run
        /// the session it creates. Either is refused when the instance is
        /// set up, as a runtime that cannot supply the session, with the
        /// missing name handed back, and never reaches the ask.</summary>
        [Theory]
        [InlineData("xrGetSystem")]
        [InlineData("xrCreateSession")]
        [InlineData("xrBeginSession")]
        [InlineData("xrPollEvent")]
        public void ARuntimeWithoutAnEntryPointTheSessionNeedsIsRefused(string missing)
        {
            using var rt = new OpenXrScriptedRuntime();
            rt.Missing.Add(missing);
            var log = new List<string>();

            var session = rt.Open(log.Add, out var failure, out bool runtimeUnavailable, out string read);

            Assert.Null(session);
            Assert.Equal(OpenXrSourceState.NotSupported, failure);
            Assert.False(runtimeUnavailable);
            Assert.Equal($"Scripted Runtime hands out no {missing}", read);
            Assert.Empty(log);
            // The instance it did create is destroyed again.
            Assert.Equal(1, rt.InstancesCreated);
            Assert.False(rt.InstanceAlive);
            Assert.Empty(rt.Faults);
        }

        /// <summary>XR_ERROR_RUNTIME_UNAVAILABLE, -51, from xrCreateInstance
        /// is a runtime that is away and will be back (instance.adoc,
        /// XrEventDataInstanceLossPending). Any other refusal is not.</summary>
        [Theory]
        [InlineData(RuntimeUnavailable, true)]
        [InlineData(RuntimeFailure, false)]
        public void ARefusedInstanceSaysWhetherTheRuntimeIsAway(int answer, bool away)
        {
            using var rt = new OpenXrScriptedRuntime();
            rt.InstanceAnswers.Enqueue(answer);
            var log = new List<string>();

            var session = rt.Open(log.Add, out var failure, out bool runtimeUnavailable, out string read);

            Assert.Null(session);
            Assert.Equal(away, runtimeUnavailable);
            Assert.Equal(OpenXrSourceState.NotSupported, failure);
            Assert.Equal($"Scripted Runtime answered xrCreateInstance with {N(answer)}", read);
            Assert.Empty(log);
            Assert.Equal(0, rt.InstancesCreated);
            Assert.Empty(rt.Faults);
        }

        /// <summary>
        /// The extension listing answers XR_ERROR_RUNTIME_UNAVAILABLE too
        /// while the runtime is away (xr.xml lists it for that call), and it
        /// is read the same way. A listing that fails says nothing about what
        /// the runtime offers: what is handed back is the call and its code,
        /// not that the runtime lacks XR_MND_headless, which a session that
        /// just ran on it would disprove. Either of the two calls the listing
        /// makes can be the one that fails.
        /// </summary>
        [Theory]
        [InlineData(RuntimeUnavailable, 0, true)]
        [InlineData(0, RuntimeUnavailable, true)]
        [InlineData(RuntimeFailure, 0, false)]
        [InlineData(0, RuntimeFailure, false)]
        public void AListingThatFailsIsNotReadAsAMissingExtension(int count, int fill, bool away)
        {
            using var rt = new OpenXrScriptedRuntime { Listing = count, ListingFill = fill };
            var log = new List<string>();

            var session = rt.Open(log.Add, out var failure, out bool runtimeUnavailable, out string read);

            Assert.Null(session);
            Assert.Equal(away, runtimeUnavailable);
            Assert.Equal(OpenXrSourceState.NotSupported, failure);
            int code = count != 0 ? count : fill;
            Assert.Equal($"Scripted Runtime answered xrEnumerateInstanceExtensionProperties with {N(code)}", read);
            Assert.DoesNotContain(XR_MND_HEADLESS_EXTENSION_NAME, read);
            Assert.Empty(log);
            Assert.DoesNotContain("xrCreateInstance", rt.Calls);
        }

        /// <summary>A runtime whose listing succeeds and holds no
        /// XR_MND_headless is the one said to lack it.</summary>
        [Fact]
        public void ARuntimeWithoutTheHeadlessExtensionIsSaidToLackIt()
        {
            using var rt = new OpenXrScriptedRuntime
            {
                Extensions = new[] { XR_KHR_WIN32_CONVERT_PERFORMANCE_COUNTER_TIME_EXTENSION_NAME },
            };

            var session = rt.Open(_ => { }, out var failure, out bool runtimeUnavailable, out string read);

            Assert.Null(session);
            Assert.False(runtimeUnavailable);
            Assert.Equal(OpenXrSourceState.NotSupported, failure);
            Assert.StartsWith("Scripted Runtime lists no " + XR_MND_HEADLESS_EXTENSION_NAME, read);
            Assert.DoesNotContain("xrCreateInstance", rt.Calls);
        }

        /// <summary>The wait before a new instance is read off the loss
        /// event against the runtime's clock, half a second past the loss
        /// time. A loss time already past waits that half second, and one
        /// further out than five minutes is not believed. What is handed
        /// back names the event and how far out its loss time was.</summary>
        [Theory]
        [InlineData(3_000_000_000L, 3_500)]
        [InlineData(-5_000_000_000L, 500)]
        [InlineData(3_600_000_000_000L, 300_500)]
        public void TheLossWaitIsReadOffTheEventOnTheRuntimesClock(long aheadNs, long waitMs)
        {
            using var rt = new OpenXrScriptedRuntime();
            using (var session = Open(rt))
            {
                Assert.Equal(OpenXrSessionCreate.Created, session.TryCreateSession(out _, out _));
                rt.QueueInstanceLoss(rt.Now + aheadNs);

                Assert.Equal(OpenXrSessionEnd.InstanceLossPending, session.PumpEvents(out _, out string read));
                Assert.Equal(waitMs, session.InstanceLossWaitMs);
                Assert.Equal($"XrEventDataInstanceLossPending, its loss time {N(aheadNs / 1_000_000)} ms out", read);
            }
            Assert.Empty(rt.Faults);
        }

        /// <summary>With no session standing a session event is about
        /// nothing, and it is dropped: it begins nothing and ends nothing,
        /// before the first session and after a destroyed one, whether it
        /// names a session or leaves the handle empty. Nothing is written
        /// about it, since what a destroyed session left in the queue is
        /// expected there.</summary>
        [Fact]
        public void ASessionEventWithNoSessionIsDropped()
        {
            using var rt = new OpenXrScriptedRuntime();
            var log = new List<string>();
            using (var session = Open(rt, log))
            {
                rt.QueueSessionStateFor(0x999, XR_SESSION_STATE_READY);
                rt.QueueSessionStateFor(0x999, XR_SESSION_STATE_EXITING);
                rt.QueueSessionStateFor(0, XR_SESSION_STATE_READY);
                rt.QueueSessionStateFor(0, XR_SESSION_STATE_EXITING);
                Assert.Equal(OpenXrSessionEnd.None, session.PumpEvents(out _, out _));
                Assert.Equal(0, rt.BeginAsks);

                Assert.Equal(OpenXrSessionCreate.Created, session.TryCreateSession(out _, out _));
                ulong first = rt.LiveSession;
                session.DestroySession();
                log.Clear();
                rt.QueueSessionStateFor(first, XR_SESSION_STATE_LOSS_PENDING);
                rt.QueueSessionStateFor(0, XR_SESSION_STATE_LOSS_PENDING);
                Assert.Equal(OpenXrSessionEnd.None, session.PumpEvents(out _, out _));
                Assert.Empty(log);
            }
            Assert.Empty(rt.Faults);
        }

        /// <summary>
        /// An event that names another session is not about the one that
        /// stands. It ends nothing and begins nothing, and the log says so
        /// once. A null handle passes, as it does in the Khronos sample's
        /// handler (HandleSessionStateChangedEvent, openxr_program.cpp in
        /// ALVR's engine), so a runtime that leaves the field empty still
        /// works.
        /// </summary>
        [Fact]
        public void AnEventThatNamesAnotherSessionIsDropped()
        {
            using var rt = new OpenXrScriptedRuntime();
            var log = new List<string>();
            using (var session = Open(rt, log))
            {
                Assert.Equal(OpenXrSessionCreate.Created, session.TryCreateSession(out _, out _));
                ulong other = rt.LiveSession + 0x50;

                rt.QueueSessionStateFor(other, XR_SESSION_STATE_READY);
                rt.QueueSessionStateFor(other, XR_SESSION_STATE_EXITING);
                rt.QueueSessionStateFor(other, XR_SESSION_STATE_LOSS_PENDING);
                Assert.Equal(OpenXrSessionEnd.None, session.PumpEvents(out _, out string read));
                Assert.Null(read);
                Assert.Equal(0, rt.BeginAsks);
                Assert.False(session.TryLocateHead(out _, out _));
                Assert.Single(log, line => line.Contains("a session that is not this one"));

                // The live session's own events are read as before, and one
                // that names no session at all is taken for it.
                rt.QueueSessionStateFor(0, XR_SESSION_STATE_READY);
                rt.SetSessionState(XR_SESSION_STATE_READY);
                Assert.Equal(OpenXrSessionEnd.None, session.PumpEvents(out _, out _));
                Assert.Equal(1, rt.Begins);
                Assert.True(session.TryLocateHead(out _, out _));

                rt.QueueSessionState(XR_SESSION_STATE_EXITING);
                Assert.Equal(OpenXrSessionEnd.Exiting, session.PumpEvents(out _, out _));
            }
            Assert.Empty(rt.Faults);
        }

        /// <summary>
        /// XrEventDataReferenceSpaceChangePending names the session whose
        /// space is changing, as the state event names its session
        /// (openxr.h, spaces.adoc), and it is held to the same rule. With
        /// no session standing it is about nothing. One that names another
        /// session is dropped, and the log says so once, where it used to
        /// be applied unread and recapture the neutral of the session that
        /// stood. One that names this session, or none, is read as before.
        /// </summary>
        [Fact]
        public void AReferenceSpaceChangeForAnotherSessionIsDropped()
        {
            using var rt = new OpenXrScriptedRuntime();
            var log = new List<string>();
            using (var session = Open(rt, log))
            {
                rt.QueueReferenceSpaceChangeFor(0x999);
                rt.QueueReferenceSpaceChangeFor(0);
                Assert.Equal(OpenXrSessionEnd.None, session.PumpEvents(out bool changed, out _));
                Assert.False(changed);
                Assert.Empty(log);

                Assert.Equal(OpenXrSessionCreate.Created, session.TryCreateSession(out _, out _));
                Begin(rt, session);
                log.Clear();
                ulong other = rt.LiveSession + 0x50;

                rt.QueueReferenceSpaceChangeFor(other);
                rt.QueueReferenceSpaceChangeFor(other);
                Assert.Equal(OpenXrSessionEnd.None, session.PumpEvents(out changed, out string read));
                Assert.False(changed);
                Assert.Null(read);
                Assert.Equal("OpenXR: Scripted Runtime sent a reference space change for a session that is not this one, and it is dropped",
                             Assert.Single(log));

                rt.QueueReferenceSpaceChange();
                Assert.Equal(OpenXrSessionEnd.None, session.PumpEvents(out changed, out _));
                Assert.True(changed);

                rt.QueueReferenceSpaceChangeFor(0);
                Assert.Equal(OpenXrSessionEnd.None, session.PumpEvents(out changed, out _));
                Assert.True(changed);

                // The state event has a line of its own, said once too.
                rt.QueueSessionStateFor(other, XR_SESSION_STATE_EXITING);
                Assert.Equal(OpenXrSessionEnd.None, session.PumpEvents(out _, out _));
                Assert.Equal(new[]
                {
                    "OpenXR: Scripted Runtime sent a reference space change for a session that is not this one, and it is dropped",
                    "OpenXR: Scripted Runtime sent a state for a session that is not this one, and it is dropped",
                }, log);
            }
            Assert.Empty(rt.Faults);
        }

        /// <summary>The three events the pump reads, against the layout
        /// openxr.h gives them on a 64-bit build. The scripted runtime
        /// writes them through the structures the session reads them
        /// with, so a field that moved would agree with itself on both
        /// sides.</summary>
        [Fact]
        public void TheEventsHoldTheirFieldsWhereOpenXrPutsThem()
        {
            static int At<T>(string field) => (int)System.Runtime.InteropServices.Marshal.OffsetOf<T>(field);
            static int Size<T>() => System.Runtime.InteropServices.Marshal.SizeOf<T>();

            Assert.Equal(40, XR_TYPE_EVENT_DATA_REFERENCE_SPACE_CHANGE_PENDING);

            Assert.Equal(16, At<XrEventDataSessionStateChanged>("session"));
            Assert.Equal(24, At<XrEventDataSessionStateChanged>("state"));
            Assert.Equal(32, At<XrEventDataSessionStateChanged>("time"));
            Assert.Equal(40, Size<XrEventDataSessionStateChanged>());

            Assert.Equal(16, At<XrEventDataInstanceLossPending>("lossTime"));
            Assert.Equal(24, Size<XrEventDataInstanceLossPending>());

            Assert.Equal(16, At<XrEventDataReferenceSpaceChangePending>("session"));
            Assert.Equal(24, At<XrEventDataReferenceSpaceChangePending>("referenceSpaceType"));
            Assert.Equal(32, At<XrEventDataReferenceSpaceChangePending>("changeTime"));
            Assert.Equal(40, At<XrEventDataReferenceSpaceChangePending>("poseValid"));
            Assert.Equal(44, At<XrEventDataReferenceSpaceChangePending>("poseInPreviousSpace"));
            Assert.Equal(72, Size<XrEventDataReferenceSpaceChangePending>());
        }

        /// <summary>A lost session is destroyed with its spaces and its
        /// action set, the instance stays, and the next session on it runs
        /// with its controllers. xrEndSession is not called for a session
        /// that was lost, since that call belongs to
        /// XR_SESSION_STATE_STOPPING.</summary>
        [Fact]
        public void ADestroyedSessionLeavesTheInstanceForTheNextOne()
        {
            using var rt = new OpenXrScriptedRuntime();
            using (var session = Open(rt))
            {
                Assert.Equal(OpenXrSessionCreate.Created, session.TryCreateSession(out _, out _));
                ulong first = rt.LiveSession;
                Begin(rt, session);
                Assert.True(session.TryLocateHead(out _, out _));

                rt.QueueSessionState(XR_SESSION_STATE_LOSS_PENDING);
                Assert.Equal(OpenXrSessionEnd.SessionLost, session.PumpEvents(out _, out _));
                session.DestroySession();

                Assert.False(session.HasSession);
                Assert.False(session.HasControllers);
                Assert.False(session.TryLocateHead(out _, out _));
                Assert.Equal(0UL, rt.LiveSession);
                Assert.Equal(0, rt.LiveSpaces);
                Assert.Equal(0, rt.LiveActionSets);
                Assert.Equal(0, rt.Ends);
                Assert.True(rt.InstanceAlive);

                Assert.Equal(OpenXrSessionCreate.Created, session.TryCreateSession(out _, out _));
                Assert.True(session.HasSession);
                Assert.True(session.HasControllers);
                Assert.NotEqual(first, rt.LiveSession);
                // The new session has not begun, whatever the old one did.
                Assert.False(session.TryLocateHead(out _, out _));
                Begin(rt, session);
                Assert.Equal(2, rt.Begins);
                Assert.True(session.TryLocateHead(out _, out _));
                Assert.True(HandsAreLive(session));
            }

            Assert.Empty(rt.Faults);
            Assert.Equal(2, rt.SessionsCreated);
            Assert.Equal(2, rt.SessionsDestroyed);
            Assert.Equal(2, rt.ActionSetsCreated);
            Assert.Equal(2, rt.ActionSetsDestroyed);
            Assert.Equal(1, rt.InstancesCreated);
            Assert.Equal(1, rt.InstancesDestroyed);
        }

        /// <summary>The pump tells the ends apart as the runtime sends them:
        /// the two session states, the instance-loss event, and a poll that
        /// fails, the lost instance named apart. Each comes with what was
        /// read, and the link writes none of it to the log.</summary>
        [Fact]
        public void ThePumpReadsEachEndOffTheRuntime()
        {
            using var rt = new OpenXrScriptedRuntime();
            var log = new List<string>();
            using (var session = Open(rt, log))
            {
                Assert.Equal(OpenXrSessionCreate.Created, session.TryCreateSession(out _, out _));
                Begin(rt, session);
                log.Clear();

                rt.QueueReferenceSpaceChange();
                Assert.Equal(OpenXrSessionEnd.None, session.PumpEvents(out bool spaceChanged, out string read));
                Assert.True(spaceChanged);
                Assert.Null(read);

                // An event of a type the session does not read is read
                // past, as the change of the interaction profile is.
                rt.QueueEvent(InteractionProfileChanged);
                Assert.Equal(OpenXrSessionEnd.None, session.PumpEvents(out spaceChanged, out read));
                Assert.False(spaceChanged);
                Assert.Null(read);

                rt.QueueSessionState(XR_SESSION_STATE_STOPPING);
                Assert.Equal(OpenXrSessionEnd.None, session.PumpEvents(out _, out _));
                Assert.Equal(1, rt.Ends);

                rt.QueueSessionState(XR_SESSION_STATE_EXITING);
                Assert.Equal(OpenXrSessionEnd.Exiting, session.PumpEvents(out _, out read));
                Assert.Equal("XR_SESSION_STATE_EXITING", read);

                rt.QueueSessionState(XR_SESSION_STATE_LOSS_PENDING);
                Assert.Equal(OpenXrSessionEnd.SessionLost, session.PumpEvents(out _, out read));
                Assert.Equal("XR_SESSION_STATE_LOSS_PENDING", read);

                rt.QueueInstanceLoss(rt.Now);
                Assert.Equal(OpenXrSessionEnd.InstanceLossPending, session.PumpEvents(out _, out read));
                Assert.Equal("XrEventDataInstanceLossPending, its loss time 0 ms out", read);

                rt.PollAnswer = InstanceLost;
                Assert.Equal(OpenXrSessionEnd.InstanceLost, session.PumpEvents(out _, out read));
                Assert.Equal("xrPollEvent answered -13", read);

                rt.PollAnswer = RuntimeFailure;
                Assert.Equal(OpenXrSessionEnd.PollFailed, session.PumpEvents(out _, out read));
                Assert.Equal("xrPollEvent answered -2", read);

                rt.PollAnswer = XR_SUCCESS;
                Assert.Empty(log);
            }
            Assert.Empty(rt.Faults);
        }

        /// <summary>What xrBeginSession's answer ends, against the numbers
        /// openxr.h gives its codes. The two success codes end nothing, a
        /// lost instance and a lost session end what they end from any call,
        /// and every other answer leaves a session that never runs.</summary>
        [Fact]
        public void ABeginThatIsRefusedEndsTheSession()
        {
            Assert.Equal(OpenXrSessionEnd.None, OpenXrSession.EndOfBegin(0));
            Assert.Equal(OpenXrSessionEnd.None, OpenXrSession.EndOfBegin(SessionLossPending));
            Assert.Equal(OpenXrSessionEnd.InstanceLost, OpenXrSession.EndOfBegin(InstanceLost));
            Assert.Equal(OpenXrSessionEnd.SessionLost, OpenXrSession.EndOfBegin(SessionLost));
            Assert.Equal(OpenXrSessionEnd.BeginRefused, OpenXrSession.EndOfBegin(ViewConfigurationTypeUnsupported));
            Assert.Equal(OpenXrSessionEnd.BeginRefused, OpenXrSession.EndOfBegin(RuntimeFailure));
        }

        /// <summary>
        /// The pump begins the session on XR_SESSION_STATE_READY and reads
        /// the answer. A refusal comes back as the end of a session that
        /// never ran, with the call and its code, where the session used to
        /// be kept with nothing to locate. A session being lost answers with
        /// the success code XR_SESSION_LOSS_PENDING, and is treated as begun
        /// until its loss arrives as an event.
        /// </summary>
        [Theory]
        [InlineData(ViewConfigurationTypeUnsupported, nameof(OpenXrSessionEnd.BeginRefused), false)]
        [InlineData(RuntimeFailure, nameof(OpenXrSessionEnd.BeginRefused), false)]
        [InlineData(InstanceLost, nameof(OpenXrSessionEnd.InstanceLost), false)]
        [InlineData(SessionLost, nameof(OpenXrSessionEnd.SessionLost), false)]
        [InlineData(SessionLossPending, nameof(OpenXrSessionEnd.None), true)]
        public void ThePumpReadsWhatTheBeginAnswered(int answer, string end, bool begun)
        {
            using var rt = new OpenXrScriptedRuntime();
            rt.BeginAnswers.Enqueue(answer);
            var log = new List<string>();
            using (var session = Open(rt, log))
            {
                Assert.Equal(OpenXrSessionCreate.Created, session.TryCreateSession(out _, out _));
                log.Clear();
                rt.QueueSessionState(XR_SESSION_STATE_READY);

                Assert.Equal(Enum.Parse<OpenXrSessionEnd>(end), session.PumpEvents(out _, out string read));

                Assert.Equal(begun ? null : $"xrBeginSession answered {N(answer)}", read);
                Assert.Equal(1, rt.BeginAsks);
                Assert.Equal(begun, session.TryLocateHead(out _, out _));
                // The line about a running session is for one that runs.
                Assert.Empty(log);
            }
            Assert.Empty(rt.Faults);
        }

        /// <summary>
        /// A storm of events, or a runtime that answers every poll with one
        /// more, is bounded to a fixed number per poll. The log says so once
        /// when a poll first stops at the bound and once when the queue is
        /// empty again, where a line per poll was 125 a second.
        /// </summary>
        [Fact]
        public void AnEventStormIsSaidWhenItStartsAndWhenItEnds()
        {
            using var rt = new OpenXrScriptedRuntime();
            var log = new List<string>();
            using (var session = Open(rt, log))
            {
                Assert.Equal(OpenXrSessionCreate.Created, session.TryCreateSession(out _, out _));
                log.Clear();
                // Three polls' worth and a few more.
                for (int i = 0; i < 64 * 3 + 5; i++) rt.QueueReferenceSpaceChange();

                for (int poll = 0; poll < 3; poll++)
                {
                    Assert.Equal(OpenXrSessionEnd.None, session.PumpEvents(out bool changed, out _));
                    Assert.True(changed);
                }
                Assert.Equal("OpenXR: Scripted Runtime gave 64 events in one poll, so its queue is read on over the next polls",
                             Assert.Single(log));

                Assert.Equal(OpenXrSessionEnd.None, session.PumpEvents(out _, out _));
                Assert.Equal(2, log.Count);
                Assert.Equal("OpenXR: Scripted Runtime has its event queue empty again, after 3 polls that stopped at 64 events", log[1]);

                // A quiet queue says nothing more, and the next storm is said again.
                Assert.Equal(OpenXrSessionEnd.None, session.PumpEvents(out _, out _));
                Assert.Equal(2, log.Count);
                for (int i = 0; i < 64; i++) rt.QueueReferenceSpaceChange();
                Assert.Equal(OpenXrSessionEnd.None, session.PumpEvents(out _, out _));
                Assert.Equal(3, log.Count);
            }
            Assert.Empty(rt.Faults);
        }

        /// <summary>
        /// XR_SESSION_LOSS_PENDING, 3, is a success code of three more calls
        /// on a session: xrAttachSessionActionSets, xrCreateActionSpace and
        /// xrEndSession (xr.xml). A session being lost answers it and
        /// simulates success, as Monado's runtime does while a loss is
        /// pending (oxr_session_success_result). The controllers stand, the
        /// session ends, and nothing is said to be refused. The loss itself
        /// arrives as an event, and the session is replaced then.
        /// </summary>
        [Theory]
        [InlineData("attach")]
        [InlineData("action space")]
        [InlineData("end")]
        public void ASuccessCodeOfASessionBeingLostIsNotReadAsARefusal(string call)
        {
            using var rt = new OpenXrScriptedRuntime();
            if (call == "attach") rt.AttachAnswer = SessionLossPending;
            if (call == "action space") rt.ActionSpaceAnswer = SessionLossPending;
            if (call == "end") rt.EndAnswers.Enqueue(SessionLossPending);
            var log = new List<string>();
            using (var session = Open(rt, log))
            {
                Assert.Equal(OpenXrSessionCreate.Created, session.TryCreateSession(out _, out _));
                Begin(rt, session);
                Assert.True(session.HasControllers);
                Assert.True(HandsAreLive(session));

                rt.QueueSessionState(XR_SESSION_STATE_STOPPING);
                Assert.Equal(OpenXrSessionEnd.None, session.PumpEvents(out _, out _));
                Assert.Equal(1, rt.Ends);
            }
            Assert.Equal(new[]
            {
                "OpenXR: controllers ready, 3 interaction profile(s) accepted",
                "OpenXR: session running on Scripted Runtime",
            }, log);
            Assert.Empty(rt.Faults);
        }

        /// <summary>A runtime that refuses xrEndSession is in the log with
        /// the code it answered, and the session is let go as before.</summary>
        [Fact]
        public void ARefusedEndIsSaidWithItsCode()
        {
            using var rt = new OpenXrScriptedRuntime();
            rt.EndAnswers.Enqueue(RuntimeFailure);
            var log = new List<string>();
            using (var session = Open(rt, log))
            {
                Assert.Equal(OpenXrSessionCreate.Created, session.TryCreateSession(out _, out _));
                Begin(rt, session);
                log.Clear();

                rt.QueueSessionState(XR_SESSION_STATE_STOPPING);
                Assert.Equal(OpenXrSessionEnd.None, session.PumpEvents(out _, out _));

                Assert.Equal("OpenXR: Scripted Runtime refused to end the session (-2)", Assert.Single(log));
                Assert.Equal(0, rt.Ends);
            }
            Assert.Empty(rt.Faults);
        }

        /// <summary>A negotiation that gives no xrGetInstanceProcAddr hands
        /// back the call and what it answered. The call itself needs a
        /// runtime's library, so only its text is reached here.</summary>
        [Fact]
        public void ANegotiationThatFailsSaysWhatItAnswered()
        {
            Assert.Equal("Runtime X answered xrNegotiateLoaderRuntimeInterface with -6",
                         OpenXrSession.NegotiationRead("Runtime X", InitializationFailed));
            Assert.Equal("Runtime X answered xrNegotiateLoaderRuntimeInterface with 0 and no xrGetInstanceProcAddr",
                         OpenXrSession.NegotiationRead("Runtime X", 0));
        }

        /// <summary>The link tells the source whether its library is an
        /// OpenVR client library, read off the manifest it was negotiated
        /// from, and asks for that library and no other.</summary>
        [Theory]
        [InlineData("vrclient_x64.dll", true)]
        [InlineData("scripted-runtime.dll", false)]
        public void TheLinkKnowsWhetherItsLibraryIsAnOpenVrClient(string libraryFile, bool shares)
        {
            using var rt = new OpenXrScriptedRuntime("Scripted Runtime", libraryFile);
            using (var session = Open(rt))
            {
                Assert.Equal(shares, session.SharesOpenVrClient);
                Assert.Equal(rt.ManifestPath, session.ManifestPath);
            }

            Assert.Equal(rt.LibraryPath, Assert.Single(rt.Loaded));
            Assert.Empty(rt.Faults);
        }

        /// <summary>At the start the system default is the first registered
        /// runtime whose library is on disk, the active one first, and the
        /// link keeps the manifest it was negotiated from.</summary>
        [Fact]
        public void TheSystemDefaultIsTheFirstRegisteredRuntimeWithItsLibraryOnDisk()
        {
            using var active = new OpenXrScriptedRuntime("Active Runtime");
            using var next = new OpenXrScriptedRuntime("Next Runtime");
            var activeEntry = active.Entry;
            active.TakeLibraryOffDisk();
            PFN_xrGetInstanceProcAddr Load(OpenXrRuntimeEntry entry, out string read)
                => entry.LibraryPath == next.LibraryPath ? next.Load(entry, out read) : active.Load(entry, out read);

            using (var session = OpenXrSession.TryCreate(null, again: false, _ => { }, Load,
                       () => new[] { activeEntry, next.Entry }, out var failure, out bool runtimeUnavailable, out _))
            {
                Assert.NotNull(session);
                Assert.Equal(OpenXrSourceState.Connecting, failure);
                Assert.False(runtimeUnavailable);
                Assert.Equal(next.ManifestPath, session.ManifestPath);
            }
            Assert.Empty(active.Loaded);
            Assert.Empty(next.Faults);
        }

        /// <summary>A runtime that cannot be found or loaded reads as no
        /// runtime, with what was read handed back, and as one that is away,
        /// which is how the Khronos loader reports the same states and what
        /// the retry after a retired instance waits on.</summary>
        [Theory]
        [InlineData("manifest", "is not on disk")]
        [InlineData("garbled", "does not read as a runtime manifest")]
        [InlineData("library", "is not on disk")]
        [InlineData("load", "did not load")]
        [InlineData("none registered", "no registered runtime has its library on disk")]
        public void ARuntimeThatCannotBeFoundOrLoadedIsNoRuntimeAndAway(string what, string said)
        {
            using var runtime = new OpenXrScriptedRuntime("Runtime X");
            string manifest = runtime.ManifestPath;
            switch (what)
            {
                case "manifest": runtime.TakeManifestOffDisk(); break;
                case "garbled": System.IO.File.WriteAllText(manifest, "{ \"runtime\": 7 }"); break;
                case "library": runtime.TakeLibraryOffDisk(); break;
                case "load": runtime.Loads = false; break;
                case "none registered": manifest = null; break;
            }
            var log = new List<string>();

            var session = OpenXrSession.TryCreate(manifest, again: false, log.Add, runtime.Load,
                () => Array.Empty<OpenXrRuntimeEntry>(), out var failure, out bool runtimeUnavailable, out string read);

            Assert.Null(session);
            Assert.Equal(OpenXrSourceState.NoRuntime, failure);
            Assert.True(runtimeUnavailable);
            Assert.Equal(what == "load" ? 1 : 0, runtime.Loaded.Count);
            Assert.Contains(said, read);
            Assert.Empty(log);
            Assert.Equal(0, runtime.InstancesCreated);
        }

        /// <summary>A setup that throws is a call that failed: no instance,
        /// not a runtime that is away, and what was thrown is handed back
        /// for the one line the source writes, which leaves the row on the
        /// line that points at the log.</summary>
        [Fact]
        public void ASetupThatThrowsIsAFailedCallWithWhatItThrew()
        {
            using var runtime = new OpenXrScriptedRuntime("Runtime X");
            runtime.OnLoad = _ => throw new InvalidOperationException("the loader broke");
            var log = new List<string>();

            var session = runtime.Open(log.Add, out var failure, out bool runtimeUnavailable, out string read);

            Assert.Null(session);
            Assert.Equal(OpenXrSourceState.Failed, failure);
            Assert.False(runtimeUnavailable);
            Assert.Equal("the setup threw: the loader broke", read);
            Assert.Empty(log);

            var source = Play(() => Array.Empty<OpenXrRuntimeEntry>(), runtime.Load, log, choice: runtime.ManifestPath);

            Assert.Equal(OpenXrSourceState.Failed, source.State);
            Assert.Equal(new[]
            {
                "OpenXR: no instance was created (the setup threw: the loader broke), so the source stops (Failed)",
            }, log);
        }

        /// <summary>
        /// An ask the source sends on its own, after a retired instance,
        /// names the runtime by its manifest alone. With the manifest or the
        /// library off disk the runtime is away, and the registered runtimes
        /// are not read, so the ask cannot pass to the next one.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AnAskAgainForARuntimeWhoseFilesAreOffDiskReadsNoOtherRuntime(bool manifestGone)
        {
            using var runtime = new OpenXrScriptedRuntime("Runtime X");
            using var other = new OpenXrScriptedRuntime("Another Runtime");
            if (manifestGone) runtime.TakeManifestOffDisk();
            else runtime.TakeLibraryOffDisk();
            int listings = 0;
            var loaded = new List<string>();
            PFN_xrGetInstanceProcAddr Load(OpenXrRuntimeEntry entry, out string loadRead)
            {
                loaded.Add(entry.LibraryPath);
                loadRead = null;
                return null;
            }

            var session = OpenXrSession.TryCreate(runtime.ManifestPath, again: true, _ => { }, Load,
                () => { listings++; return new[] { other.Entry }; },
                out var failure, out bool runtimeUnavailable, out string read);

            Assert.Null(session);
            Assert.True(runtimeUnavailable);
            Assert.Equal(OpenXrSourceState.NoRuntime, failure);
            Assert.Equal(0, listings);
            Assert.Empty(loaded);
            Assert.Contains("is not on disk", read);
            Assert.Contains(manifestGone ? runtime.ManifestPath : runtime.LibraryPath, read);
        }

        /// <summary>No instance is created again on an OpenVR client library:
        /// asking SteamVR for an instance starts SteamVR, and nothing the
        /// source does on its own may do that. The manifest's library is
        /// read before anything is loaded, so the refusal comes first, it is
        /// final and not a runtime that is away, and it holds whether or not
        /// the library is on disk. The same manifest opens at the start,
        /// where the user asked for it
        /// (<see cref="TheLinkKnowsWhetherItsLibraryIsAnOpenVrClient"/>).</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AnAskAgainCreatesNoInstanceOnAnOpenVrClientLibrary(bool libraryOnDisk)
        {
            using var runtime = new OpenXrScriptedRuntime("SteamVR stand-in", "vrclient_x64.dll");
            if (!libraryOnDisk) runtime.TakeLibraryOffDisk();

            var session = runtime.Open(_ => { }, out var failure, out bool runtimeUnavailable, out string read, again: true);

            Assert.Null(session);
            Assert.False(runtimeUnavailable);
            Assert.Equal(OpenXrSourceState.Ended, failure);
            Assert.Empty(runtime.Loaded);
            Assert.Equal(0, runtime.InstancesCreated);
            Assert.Contains("names an OpenVR client library", read);
        }

        /// <summary>An ask the source sends on its own with no manifest to
        /// name the runtime asks no runtime at all. The runtime choice is
        /// not read in the manifest's place, so the registered runtimes stay
        /// unread, and the refusal is final.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        public void AnAskAgainWithNoManifestAsksNoRuntime(string manifest)
        {
            using var other = new OpenXrScriptedRuntime("Another Runtime");
            int listings = 0;

            var session = OpenXrSession.TryCreate(manifest, again: true, _ => { }, other.Load,
                () => { listings++; return new[] { other.Entry }; },
                out var failure, out bool runtimeUnavailable, out string read);

            Assert.Null(session);
            Assert.False(runtimeUnavailable);
            Assert.Equal(OpenXrSourceState.Ended, failure);
            Assert.Equal(0, listings);
            Assert.Empty(other.Loaded);
            Assert.Equal("no manifest names the runtime that was in use", read);
        }

        /// <summary>
        /// Plays the source's loop on this thread over the real session and
        /// scripted runtimes, to its end. The clock moves only when the loop
        /// sleeps, by as much as it asked for, and <paramref name="onSleep"/>
        /// runs after each sleep with the time, which is where a script
        /// changes what is on disk. <paramref name="asks"/> takes each ask
        /// for an instance: when, whether it was the source's own, and
        /// whether it found the runtime away. <paramref name="publish"/>
        /// takes each pose the source delivers. A script that never ends
        /// the source is stopped after an hour on that clock.
        /// </summary>
        private static OpenXrHeadPoseSource Play(
            Func<IReadOnlyList<OpenXrRuntimeEntry>> discover, OpenXrSession.RuntimeLoader load,
            List<string> log, Action<OpenXrHeadPoseSource, long> onSleep = null, string choice = null,
            List<(long At, bool Again, bool Away)> asks = null, Action<double[]> publish = null)
        {
            long now = 0;
            var source = new OpenXrHeadPoseSource(() => choice, publish ?? (_ => { }), log.Add)
            {
                RetryIntervalMs = 100,
                WaitingPumpIntervalMs = 10,
            };
            source.OpenLink = (string manifest, bool again, Action<string> write,
                               out OpenXrSourceState failure, out bool runtimeUnavailable, out string read) =>
            {
                var session = OpenXrSession.TryCreate(manifest, again, write, load, discover,
                                                      out failure, out runtimeUnavailable, out read);
                asks?.Add((now, again, session == null && runtimeUnavailable));
                return session;
            };
            source.Ticks = () => now;
            source.Sleep = ms =>
            {
                now += Math.Max(1, ms);
                if (now > 3_600_000) throw new InvalidOperationException("the script did not end the source");
                onSleep?.Invoke(source, now);
            };
            source.RunOnThisThread();
            return source;
        }

        /// <summary>
        /// The runtime setting is at the system default, the active runtime
        /// retires its instance under a session, and its update then has its
        /// manifest off disk for a while. SteamVR is registered beside it.
        ///
        /// <para>The source waits for the runtime it was using. It asks that
        /// runtime's manifest again at the cadence, reads the missing file as
        /// the runtime being away, as the Khronos loader reports one it
        /// cannot find (runtime_interface.cpp, RuntimeInterface::LoadRuntime),
        /// and takes the new instance from it once the file is back. The
        /// other registered runtime is never asked: a request for an instance
        /// is what starts SteamVR.</para>
        ///
        /// <para>The log holds one line for the retirement, one when the
        /// runtime is first found away, with the file that was missing, and
        /// one when the new instance stands. The asks in between write
        /// nothing.</para>
        /// </summary>
        [Fact]
        public void ARuntimeAwayForAnUpdateIsWaitedForAndNoOtherRuntimeIsAsked()
        {
            using var runtime = new OpenXrScriptedRuntime("Runtime X");
            using var steamVr = new OpenXrScriptedRuntime("SteamVR stand-in", "vrclient_x64.dll");
            // The registry lists the active runtime first, and leaves out a
            // runtime whose manifest is gone.
            IReadOnlyList<OpenXrRuntimeEntry> Registered()
            {
                var listed = new List<OpenXrRuntimeEntry>();
                if (System.IO.File.Exists(runtime.ManifestPath)) listed.Add(runtime.Entry);
                listed.Add(steamVr.Entry);
                return listed;
            }
            PFN_xrGetInstanceProcAddr Load(OpenXrRuntimeEntry entry, out string read)
                => entry.LibraryPath == steamVr.LibraryPath ? steamVr.Load(entry, out read) : runtime.Load(entry, out read);

            int session = 0, idle = 0;
            runtime.OnSessionCreated = n =>
            {
                session = n;
                idle = 0;
                runtime.QueueSessionState(XR_SESSION_STATE_READY);
            };
            runtime.OnIdlePoll = () =>
            {
                if (++idle != 3) return;
                if (session == 1)
                {
                    // The update starts: the instance is retired at once, and
                    // the manifest goes with the old files.
                    runtime.QueueInstanceLoss(runtime.Now);
                    runtime.TakeManifestOffDisk();
                }
                else runtime.QueueSessionState(XR_SESSION_STATE_EXITING);
            };

            var log = new List<string>();
            var shownWhileAway = new HashSet<OpenXrSourceState>();
            bool restored = false;
            var asks = new List<(long At, bool Again, bool Away)>();
            var source = Play(Registered, Load, log, asks: asks, onSleep: (played, now) =>
            {
                if (runtime.InstancesDestroyed != 1 || runtime.InstanceAlive) return;
                shownWhileAway.Add(played.State);
                if (now >= 1500 && !restored)
                {
                    restored = true;
                    runtime.PointManifestAt("scripted-runtime.dll");
                }
            });

            Assert.Empty(steamVr.Loaded);
            Assert.Equal(0, steamVr.InstancesCreated);
            Assert.Equal(new[] { runtime.LibraryPath, runtime.LibraryPath }, runtime.Loaded);
            Assert.Equal(2, runtime.InstancesCreated);
            Assert.Equal(2, runtime.InstancesDestroyed);
            Assert.Equal(2, runtime.SessionsCreated);
            Assert.Equal(new[] { OpenXrSourceState.Connecting }, shownWhileAway);
            Assert.Equal(OpenXrSourceState.Ended, source.State);
            Assert.True(source.SampleCount > 0);
            Assert.Empty(runtime.Faults);
            Assert.Empty(steamVr.Faults);

            // Every ask until the file was back found the manifest gone, and
            // the log says so once.
            int asksWhileAway = asks.FindAll(ask => ask.Away).Count;
            Assert.True(asksWhileAway > 5, $"only {asksWhileAway} asks found the runtime away");
            Assert.All(asks.GetRange(1, asks.Count - 1), ask => Assert.True(ask.Again));
            var recovery = log.FindAll(line => line.Contains("instance"));
            Assert.Equal(3, recovery.Count);
            Assert.Equal("OpenXR: Runtime X retires its instance (XrEventDataInstanceLossPending, its loss time 0 ms out), asking for a new one in 500 ms",
                         recovery[0]);
            Assert.Equal($"OpenXR: the runtime is away (the runtime manifest {runtime.ManifestPath} is not on disk), asking for a new instance every 100 ms",
                         recovery[1]);
            Assert.Equal($"OpenXR: Runtime X gave a new instance at ask {asksWhileAway + 1}", recovery[2]);
            Assert.Single(log, line => line.Contains("away"));
        }

        /// <summary>
        /// A runtime reports no headset, the source asks again, and a later
        /// ask fails with an error, as VirtualDesktop-OpenXR's xrGetSystem
        /// answers XR_ERROR_RUNTIME_FAILURE when a call inside it throws
        /// (framework/dispatch.gen.cpp).
        /// The row does not keep the line of the wait, which says the runtime
        /// reports no headset. The source settles as Failed, and the log holds
        /// two lines: the one that opened the wait, and the one that says the
        /// source stopped, with the call and what it answered.
        /// </summary>
        [Fact]
        public void AHeadsetWaitThatFailsOnTheRealSessionSettlesAsFailed()
        {
            using var runtime = new OpenXrScriptedRuntime("Runtime X");
            runtime.SystemAnswers.Enqueue(FormFactorUnavailable);
            runtime.SystemAnswers.Enqueue(FormFactorUnavailable);
            runtime.SystemAnswers.Enqueue(RuntimeFailure);
            var log = new List<string>();
            var shownWhileWaiting = new HashSet<OpenXrSourceState>();

            var source = Play(() => Array.Empty<OpenXrRuntimeEntry>(), runtime.Load, log,
                              (played, _) => shownWhileWaiting.Add(played.State), choice: runtime.ManifestPath);

            Assert.Equal(OpenXrSourceState.Failed, source.State);
            Assert.Equal(new[] { OpenXrSourceState.NoHeadset }, shownWhileWaiting);
            Assert.Equal(3, runtime.SystemAsks);
            Assert.Equal(0, runtime.SessionsCreated);
            Assert.False(runtime.InstanceAlive);
            Assert.Equal(new[]
            {
                "OpenXR: Runtime X reports no headset (xrGetSystem answered -35), asking again every 100 ms",
                "OpenXR: Runtime X gave no session at ask 3 (xrGetSystem answered -2), so the source stops (Failed)",
            }, log);
            Assert.Empty(runtime.Faults);
        }

        /// <summary>A headset that comes back ends the wait with a line that
        /// says at which ask, so the wait is in the log once when it starts
        /// and once when it ends.</summary>
        [Fact]
        public void AHeadsetThatComesBackEndsTheWaitWithALine()
        {
            using var runtime = new OpenXrScriptedRuntime("Runtime X");
            for (int i = 0; i < 4; i++) runtime.SystemAnswers.Enqueue(FormFactorUnavailable);
            int idle = 0;
            runtime.OnSessionCreated = _ => runtime.QueueSessionState(XR_SESSION_STATE_READY);
            runtime.OnIdlePoll = () =>
            {
                if (runtime.SessionsCreated == 1 && ++idle == 3) runtime.QueueSessionState(XR_SESSION_STATE_EXITING);
            };
            var log = new List<string>();

            var source = Play(() => Array.Empty<OpenXrRuntimeEntry>(), runtime.Load, log, choice: runtime.ManifestPath);

            Assert.Equal(OpenXrSourceState.Ended, source.State);
            Assert.Equal(5, runtime.SystemAsks);
            var waits = log.FindAll(line => line.Contains("headset"));
            Assert.Equal(new[]
            {
                "OpenXR: Runtime X reports no headset (xrGetSystem answered -35), asking again every 100 ms",
                "OpenXR: Runtime X reports a headset at ask 5, and a session stands",
            }, waits);
            Assert.Equal("OpenXR: Runtime X ended the session (XR_SESSION_STATE_EXITING), so the source stops (Ended)", log[^1]);
            Assert.Empty(runtime.Faults);
        }

        /// <summary>
        /// A session ran, the runtime lost it, and the session asked for in
        /// its place is refused. The line for a runtime that cannot supply a
        /// session would speak of one that had just supplied it, so the
        /// source reads Ended, and the log holds one line for the loss and
        /// one that says the source stopped, with the refusal and its code.
        /// </summary>
        [Theory]
        [InlineData(InitializationFailed)]
        [InlineData(InstanceLost)]
        public void ARecoveryThatIsRefusedOnTheRealSessionSettlesAsEnded(int answer)
        {
            using var runtime = new OpenXrScriptedRuntime("Runtime X");
            int idle = 0;
            runtime.OnSessionCreated = _ => runtime.QueueSessionState(XR_SESSION_STATE_READY);
            runtime.OnIdlePoll = () =>
            {
                if (++idle != 3) return;
                runtime.QueueSessionState(XR_SESSION_STATE_LOSS_PENDING);
                runtime.SessionAnswers.Enqueue(answer);
            };
            var log = new List<string>();

            var source = Play(() => Array.Empty<OpenXrRuntimeEntry>(), runtime.Load, log,
                              choice: runtime.ManifestPath);

            Assert.Equal(OpenXrSourceState.Ended, source.State);
            Assert.True(source.SampleCount > 0);
            Assert.Equal(1, runtime.SessionsCreated);
            Assert.Equal(1, runtime.SessionsDestroyed);
            Assert.False(runtime.InstanceAlive);
            var recovery = log.FindAll(line => line.Contains("session") && !line.Contains("session running"));
            Assert.Equal(new[]
            {
                "OpenXR: Runtime X lost the session (XR_SESSION_STATE_LOSS_PENDING), so a new one is asked for every 100 ms",
                $"OpenXR: Runtime X gave no session at ask 1 (xrCreateSession answered {N(answer)} to a session with no graphics binding), so the source stops (Ended)",
            }, recovery);
            Assert.Empty(runtime.Faults);
        }

        /// <summary>A lost session that is replaced is in the log once when
        /// it is lost and once when the new session stands.</summary>
        [Fact]
        public void ALostSessionThatIsReplacedIsSaidWhenItIsLostAndWhenItIsBack()
        {
            using var runtime = new OpenXrScriptedRuntime("Runtime X");
            int idle = 0;
            runtime.OnSessionCreated = _ =>
            {
                idle = 0;
                runtime.QueueSessionState(XR_SESSION_STATE_READY);
            };
            runtime.OnIdlePoll = () =>
            {
                if (++idle != 3) return;
                runtime.QueueSessionState(runtime.SessionsCreated == 1
                    ? XR_SESSION_STATE_LOSS_PENDING : XR_SESSION_STATE_EXITING);
            };
            var log = new List<string>();

            var source = Play(() => Array.Empty<OpenXrRuntimeEntry>(), runtime.Load, log, choice: runtime.ManifestPath);

            Assert.Equal(OpenXrSourceState.Ended, source.State);
            Assert.Equal(2, runtime.SessionsCreated);
            Assert.Equal(1, runtime.InstancesCreated);
            // Poses arrived on the first session, so the link's lines about
            // the second are written again: the once is until a pose.
            Assert.Equal(new[]
            {
                "OpenXR: controllers ready, 3 interaction profile(s) accepted",
                "OpenXR: session running on Runtime X",
                "OpenXR: Runtime X lost the session (XR_SESSION_STATE_LOSS_PENDING), so a new one is asked for every 100 ms",
                "OpenXR: controllers ready, 3 interaction profile(s) accepted",
                "OpenXR: Runtime X gave a new session at ask 1",
                "OpenXR: session running on Runtime X",
                "OpenXR: Runtime X ended the session (XR_SESSION_STATE_EXITING), so the source stops (Ended)",
            }, log.FindAll(line => !line.Contains("head pose")));
            Assert.Empty(runtime.Faults);
        }

        /// <summary>
        /// A runtime that begins every new session and loses it at once,
        /// with no pose in between. The link writes about its controllers
        /// and about a session that runs at every session it makes, and
        /// those lines are held to the rule of the source's own: written
        /// once until a pose arrives. The whole log is five lines, for nine
        /// sessions, where the link added two per session. The line about
        /// the controllers is whichever of its three the runtime leads to.
        /// </summary>
        [Theory]
        [InlineData("ready", "OpenXR: controllers ready, 3 interaction profile(s) accepted")]
        [InlineData("no profile", "OpenXR: no controller profile was accepted, hands stay offline")]
        [InlineData("no attach", "OpenXR: the runtime refused the controller action set")]
        public void ARuntimeThatLosesEveryNewSessionWritesEachLineOnce(string controllers, string controllerLine)
        {
            using var runtime = new OpenXrScriptedRuntime("Runtime X");
            if (controllers == "no profile") runtime.SuggestAnswer = PathUnsupported;
            if (controllers == "no attach") runtime.AttachAnswer = RuntimeFailure;
            runtime.OnSessionCreated = n =>
            {
                runtime.QueueSessionState(XR_SESSION_STATE_READY);
                runtime.QueueSessionState(n <= 8 ? XR_SESSION_STATE_LOSS_PENDING : XR_SESSION_STATE_EXITING);
            };
            var log = new List<string>();

            var source = Play(() => Array.Empty<OpenXrRuntimeEntry>(), runtime.Load, log, choice: runtime.ManifestPath);

            Assert.Equal(OpenXrSourceState.Ended, source.State);
            Assert.Equal(9, runtime.SessionsCreated);
            Assert.Equal(9, runtime.Begins);
            Assert.Equal(0, source.SampleCount);
            Assert.Equal(new[]
            {
                controllerLine,
                "OpenXR: session running on Runtime X",
                "OpenXR: Runtime X lost the session (XR_SESSION_STATE_LOSS_PENDING), so a new one is asked for every 100 ms",
                "OpenXR: Runtime X gave a new session at ask 1",
                "OpenXR: Runtime X ended the session (XR_SESSION_STATE_EXITING), so the source stops (Ended)",
            }, log);
            Assert.Empty(runtime.Faults);
        }

        /// <summary>The same for a runtime that retires every new instance
        /// at once. Each new instance makes a session, and the link's lines
        /// about it are written once for the whole stretch: five lines for
        /// six instances.</summary>
        [Fact]
        public void ARuntimeThatRetiresEveryNewInstanceWritesEachLineOnce()
        {
            using var runtime = new OpenXrScriptedRuntime("Runtime X");
            runtime.OnSessionCreated = n =>
            {
                runtime.QueueSessionState(XR_SESSION_STATE_READY);
                if (n <= 5) runtime.QueueInstanceLoss(runtime.Now);
                else runtime.QueueSessionState(XR_SESSION_STATE_EXITING);
            };
            var log = new List<string>();

            var source = Play(() => Array.Empty<OpenXrRuntimeEntry>(), runtime.Load, log, choice: runtime.ManifestPath);

            Assert.Equal(OpenXrSourceState.Ended, source.State);
            Assert.Equal(6, runtime.InstancesCreated);
            Assert.Equal(6, runtime.SessionsCreated);
            Assert.Equal(0, source.SampleCount);
            Assert.Equal(new[]
            {
                "OpenXR: controllers ready, 3 interaction profile(s) accepted",
                "OpenXR: session running on Runtime X",
                "OpenXR: Runtime X retires its instance (XrEventDataInstanceLossPending, its loss time 0 ms out), asking for a new one in 500 ms",
                "OpenXR: Runtime X gave a new instance at ask 1",
                "OpenXR: Runtime X ended the session (XR_SESSION_STATE_EXITING), so the source stops (Ended)",
            }, log);
            Assert.Empty(runtime.Faults);
        }

        /// <summary>
        /// The whole loop over the real session: the first session is lost
        /// as it is created, and the state event the specification has the
        /// runtime queue first is still in the queue behind it. The source
        /// says the loss once, asks again one interval later, and the
        /// second session runs. The state the first one left is read with
        /// no session standing and never reaches the second, on a runtime
        /// that hands out a new handle each time and on one whose handle
        /// has the same value every time.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ASessionLostAsItIsCreatedIsReplacedOnTheRealSession(bool reusedHandle)
        {
            using var runtime = new OpenXrScriptedRuntime("Runtime X") { ReusesTheSessionHandle = reusedHandle };
            long clock = 0;
            var createdAt = new List<long>();
            int idle = 0;
            runtime.OnSessionCreated = n =>
            {
                createdAt.Add(clock);
                idle = 0;
                if (n == 1)
                {
                    runtime.QueueSessionState(XR_SESSION_STATE_LOSS_PENDING);
                    runtime.SpaceAnswers[XR_REFERENCE_SPACE_TYPE_STAGE] = SessionLost;
                    runtime.SpaceAnswers[XR_REFERENCE_SPACE_TYPE_LOCAL] = SessionLost;
                    runtime.SpaceAnswers[XR_REFERENCE_SPACE_TYPE_VIEW] = SessionLost;
                    return;
                }
                runtime.SpaceAnswers.Clear();
                runtime.QueueSessionState(XR_SESSION_STATE_READY);
            };
            runtime.OnIdlePoll = () =>
            {
                if (runtime.SessionsCreated == 2 && ++idle == 3) runtime.QueueSessionState(XR_SESSION_STATE_EXITING);
            };
            var log = new List<string>();

            var source = Play(() => Array.Empty<OpenXrRuntimeEntry>(), runtime.Load, log,
                              (_, now) => clock = now, choice: runtime.ManifestPath);

            Assert.Equal(OpenXrSourceState.Ended, source.State);
            Assert.Equal(new long[] { 0, 100 }, createdAt);
            Assert.Equal(2, runtime.SessionsDestroyed);
            Assert.Equal(1, runtime.InstancesCreated);
            Assert.Equal(1, runtime.Begins);
            Assert.True(source.SampleCount > 0);
            Assert.Equal(new[]
            {
                "OpenXR: Runtime X lost the session (xrCreateReferenceSpace answered -17 for the local space, after the stage space), so a new one is asked for every 100 ms",
                "OpenXR: controllers ready, 3 interaction profile(s) accepted",
                "OpenXR: Runtime X gave a new session at ask 2",
                "OpenXR: session running on Runtime X",
                "OpenXR: Runtime X ended the session (XR_SESSION_STATE_EXITING), so the source stops (Ended)",
            }, log.FindAll(line => !line.Contains("head pose")));
            Assert.Empty(runtime.Faults);
        }

        /// <summary>The same loss after a session that ran: the session
        /// asked for in its place is lost as it is created, and the one
        /// after that runs. The loss is in the log once for the stretch,
        /// with what the pump read when it began.</summary>
        [Fact]
        public void AReplacementLostAsItIsCreatedIsAskedForAgain()
        {
            using var runtime = new OpenXrScriptedRuntime("Runtime X");
            int idle = 0;
            runtime.OnSessionCreated = n =>
            {
                idle = 0;
                runtime.SpaceAnswers.Clear();
                if (n == 2)
                {
                    runtime.SpaceAnswers[XR_REFERENCE_SPACE_TYPE_VIEW] = SessionLost;
                    return;
                }
                runtime.QueueSessionState(XR_SESSION_STATE_READY);
            };
            runtime.OnIdlePoll = () =>
            {
                if (runtime.SessionsCreated == 2 || ++idle != 3) return;
                runtime.QueueSessionState(runtime.SessionsCreated == 1
                    ? XR_SESSION_STATE_LOSS_PENDING : XR_SESSION_STATE_EXITING);
            };
            var log = new List<string>();

            var source = Play(() => Array.Empty<OpenXrRuntimeEntry>(), runtime.Load, log, choice: runtime.ManifestPath);

            Assert.Equal(OpenXrSourceState.Ended, source.State);
            Assert.Equal(3, runtime.SessionsCreated);
            Assert.Equal(3, runtime.SessionsDestroyed);
            Assert.Equal(1, runtime.InstancesCreated);
            Assert.Equal(2, runtime.Begins);
            Assert.Single(log, line => line.Contains("lost the session"));
            Assert.Contains("OpenXR: Runtime X lost the session (XR_SESSION_STATE_LOSS_PENDING), so a new one is asked for every 100 ms", log);
            Assert.Contains("OpenXR: Runtime X gave a new session at ask 2", log);
            Assert.Equal("OpenXR: Runtime X ended the session (XR_SESSION_STATE_EXITING), so the source stops (Ended)", log[^1]);
            Assert.Empty(runtime.Faults);
        }

        /// <summary>On a library that is an OpenVR client library the loss
        /// is final, as every loss is there: one ask, one line, and the
        /// session and the instance are let go.</summary>
        [Fact]
        public void ASessionLostAsItIsCreatedIsFinalOnAnOpenVrClientLibrary()
        {
            using var runtime = new OpenXrScriptedRuntime("SteamVR stand-in", "vrclient_x64.dll");
            runtime.SpaceAnswers[XR_REFERENCE_SPACE_TYPE_VIEW] = SessionLost;
            var log = new List<string>();

            var source = Play(() => Array.Empty<OpenXrRuntimeEntry>(), runtime.Load, log, choice: runtime.ManifestPath);

            Assert.Equal(OpenXrSourceState.Ended, source.State);
            Assert.Equal(1, runtime.SessionsCreated);
            Assert.Equal(1, runtime.SessionsDestroyed);
            Assert.Equal(1, runtime.SystemAsks);
            Assert.False(runtime.InstanceAlive);
            Assert.Equal(new[]
            {
                "OpenXR: SteamVR stand-in lost the session as it was created, and its instance is not kept waiting for a new one (xrCreateReferenceSpace answered -17 for the view space), so the source stops (Ended)",
            }, log);
            Assert.Empty(runtime.Faults);
        }

        /// <summary>
        /// A runtime that answers XR_ERROR_RUNTIME_UNAVAILABLE to the asks
        /// for a new instance, from xrCreateInstance or from its extension
        /// listing, is asked again at the cadence. The log holds one line
        /// for the start of the wait, with the call and what it answered,
        /// and one for its end, however many asks read the same thing.
        /// </summary>
        [Theory]
        [InlineData(false, "Runtime X answered xrCreateInstance with -51")]
        [InlineData(true, "Runtime X answered xrEnumerateInstanceExtensionProperties with -51")]
        public void ARuntimeThatAnswersAwayIsAskedAtTheCadenceAndSaidOnce(bool atTheListing, string read)
        {
            using var runtime = new OpenXrScriptedRuntime("Runtime X");
            long clock = 0, retiredAt = -1;
            var askedAt = new List<long>();
            int session = 0, idle = 0;
            runtime.OnSessionCreated = n =>
            {
                session = n;
                idle = 0;
                runtime.QueueSessionState(XR_SESSION_STATE_READY);
            };
            runtime.OnIdlePoll = () =>
            {
                if (++idle != 3) return;
                if (session == 2)
                {
                    runtime.QueueSessionState(XR_SESSION_STATE_EXITING);
                    return;
                }
                // The instance is retired at once, and the runtime is away
                // for the next four asks.
                runtime.QueueInstanceLoss(runtime.Now);
                retiredAt = clock;
                if (atTheListing) runtime.Listing = RuntimeUnavailable;
                else for (int i = 0; i < 4; i++) runtime.InstanceAnswers.Enqueue(RuntimeUnavailable);
            };
            runtime.OnLoad = _ =>
            {
                askedAt.Add(clock);
                if (askedAt.Count == 6) runtime.Listing = XR_SUCCESS;
            };

            var log = new List<string>();
            var source = Play(() => Array.Empty<OpenXrRuntimeEntry>(), runtime.Load, log,
                              (_, now) => clock = now, choice: runtime.ManifestPath);

            Assert.Equal(OpenXrSourceState.Ended, source.State);
            Assert.Equal(2, runtime.InstancesCreated);
            Assert.Equal(2, runtime.InstancesDestroyed);
            Assert.Empty(runtime.Faults);

            // The first ask comes after the loss time, half a second past
            // it, and the next ones one interval apart.
            Assert.Equal(6, askedAt.Count);
            Assert.Equal(retiredAt + 500, askedAt[1]);
            for (int i = 2; i < 6; i++) Assert.Equal(askedAt[i - 1] + 100, askedAt[i]);

            var recovery = log.FindAll(line => line.Contains("instance"));
            Assert.Equal(new[]
            {
                "OpenXR: Runtime X retires its instance (XrEventDataInstanceLossPending, its loss time 0 ms out), asking for a new one in 500 ms",
                $"OpenXR: the runtime is away ({read}), asking for a new instance every 100 ms",
                "OpenXR: Runtime X gave a new instance at ask 5",
            }, recovery);
            // Four asks found it away, and one line says so.
            Assert.Single(log, line => line.Contains("away"));
            Assert.DoesNotContain(log, line => line.Contains(XR_MND_HEADLESS_EXTENSION_NAME));
        }

        /// <summary>
        /// A session the runtime refuses to begin never runs. The source
        /// lets it go with its instance and stops as Failed, with one line
        /// that names the call and its answer. The session used to be kept:
        /// the row waited for a pose that could not come, and the instance
        /// was polled for as long as the input stayed on.
        /// </summary>
        [Fact]
        public void ASessionTheRuntimeWillNotBeginStopsTheSource()
        {
            using var runtime = new OpenXrScriptedRuntime("Runtime X");
            runtime.BeginAnswers.Enqueue(ViewConfigurationTypeUnsupported);
            runtime.OnSessionCreated = _ => runtime.QueueSessionState(XR_SESSION_STATE_READY);
            var log = new List<string>();

            var source = Play(() => Array.Empty<OpenXrRuntimeEntry>(), runtime.Load, log, choice: runtime.ManifestPath);

            Assert.Equal(OpenXrSourceState.Failed, source.State);
            Assert.Equal(0, source.SampleCount);
            Assert.False(source.HasControllers);
            Assert.Equal(1, runtime.SessionsCreated);
            Assert.Equal(1, runtime.SessionsDestroyed);
            Assert.False(runtime.InstanceAlive);
            Assert.Equal("OpenXR: Runtime X refused to begin the session (xrBeginSession answered -41), so the source stops (Failed)",
                         log[^1]);
            Assert.DoesNotContain(log, line => line.Contains("session running"));
            Assert.Empty(runtime.Faults);
        }

        /// <summary>The same refusal after an earlier session ran and was
        /// lost is a recovery that stopped, and reads Ended.</summary>
        [Fact]
        public void ARefusedBeginAfterALostSessionReadsEnded()
        {
            using var runtime = new OpenXrScriptedRuntime("Runtime X");
            int idle = 0;
            runtime.OnSessionCreated = n =>
            {
                idle = 0;
                if (n == 2) runtime.BeginAnswers.Enqueue(RuntimeFailure);
                runtime.QueueSessionState(XR_SESSION_STATE_READY);
            };
            runtime.OnIdlePoll = () =>
            {
                if (runtime.SessionsCreated == 1 && ++idle == 3) runtime.QueueSessionState(XR_SESSION_STATE_LOSS_PENDING);
            };
            var log = new List<string>();

            var source = Play(() => Array.Empty<OpenXrRuntimeEntry>(), runtime.Load, log, choice: runtime.ManifestPath);

            Assert.Equal(OpenXrSourceState.Ended, source.State);
            Assert.Equal(2, runtime.SessionsCreated);
            Assert.Equal(2, runtime.SessionsDestroyed);
            Assert.False(runtime.InstanceAlive);
            Assert.Equal("OpenXR: Runtime X refused to begin the session (xrBeginSession answered -2), so the source stops (Ended)",
                         log[^1]);
            Assert.Empty(runtime.Faults);
        }

        /// <summary>
        /// A runtime whose session handle has the same value every time, as
        /// Virtual Desktop's has, and which keeps a state of the session it
        /// lost in its queue: the specification has it discard that event
        /// (fundamentals.adoc, Events), and the handle cannot tell it from
        /// the next session's. The source reads the queue with no session
        /// standing before it asks for the next one, so the event is
        /// dropped there and never reaches the new session, which runs.
        /// </summary>
        [Fact]
        public void AStateLeftByALostSessionNeverReachesTheNextOneOnAReusedHandle()
        {
            using var runtime = new OpenXrScriptedRuntime("Runtime X") { ReusesTheSessionHandle = true };
            int idle = 0;
            runtime.OnSessionCreated = _ =>
            {
                idle = 0;
                runtime.QueueSessionState(XR_SESSION_STATE_READY);
            };
            runtime.OnIdlePoll = () =>
            {
                if (++idle != 3) return;
                if (runtime.SessionsCreated == 1)
                {
                    // The loss, and behind it a state the runtime should
                    // have discarded with the session.
                    runtime.QueueSessionState(XR_SESSION_STATE_LOSS_PENDING);
                    runtime.QueueSessionState(XR_SESSION_STATE_EXITING);
                }
                else runtime.QueueSessionState(XR_SESSION_STATE_EXITING);
            };
            var log = new List<string>();
            long posesOfTheFirst = -1;

            var source = Play(() => Array.Empty<OpenXrRuntimeEntry>(), runtime.Load, log, choice: runtime.ManifestPath,
                onSleep: (played, _) =>
                {
                    if (runtime.SessionsDestroyed == 1 && posesOfTheFirst < 0) posesOfTheFirst = played.SampleCount;
                });

            Assert.Equal(2, runtime.SessionsCreated);
            Assert.True(source.SampleCount > posesOfTheFirst, "the second session delivered no pose");
            Assert.Equal(2, runtime.Begins);
            Assert.Equal(OpenXrSourceState.Ended, source.State);
            Assert.DoesNotContain(log, line => line.Contains("no session standing"));
            Assert.Equal("OpenXR: Runtime X ended the session (XR_SESSION_STATE_EXITING), so the source stops (Ended)", log[^1]);
            Assert.Empty(runtime.Faults);
        }

        /// <summary>
        /// The loop recaptures the neutral when the live session's
        /// reference space changes, as when the user recenters in the
        /// runtime, and leaves it alone for a change that names another
        /// session. The head sits 10 cm to the right of where the session
        /// first saw it when the event arrives.
        /// </summary>
        [Theory]
        [InlineData(true, 0.0)]
        [InlineData(false, 10.0)]
        public void OnlyTheLiveSessionsReferenceSpaceChangeMovesTheNeutral(bool ofTheLiveSession, double rightAfter)
        {
            using var runtime = new OpenXrScriptedRuntime("Runtime X");
            int idle = 0;
            runtime.OnSessionCreated = _ => runtime.QueueSessionState(XR_SESSION_STATE_READY);
            runtime.OnIdlePoll = () =>
            {
                // The poll that reads an event queued here finds the queue
                // empty a second time, so the count moves twice in it.
                idle++;
                if (idle == 3)
                {
                    if (ofTheLiveSession) runtime.QueueReferenceSpaceChange();
                    else runtime.QueueReferenceSpaceChangeFor(runtime.LiveSession + 0x50);
                }
                if (idle == 6) runtime.QueueSessionState(XR_SESSION_STATE_EXITING);
            };
            var right = new List<double>();
            var log = new List<string>();

            var source = Play(() => Array.Empty<OpenXrRuntimeEntry>(), runtime.Load, log, choice: runtime.ManifestPath,
                onSleep: (played, _) =>
                {
                    if (played.SampleCount == 1) runtime.Head.position.x = 0.1f;
                },
                publish: pose => right.Add(Math.Round(pose[HeadPose.TX], 3)));

            Assert.Equal(OpenXrSourceState.Ended, source.State);
            // The neutral, 10 cm right of it, and then what the event left.
            Assert.Equal(new[] { 0.0, 10.0, rightAfter, rightAfter }, right);
            Assert.Empty(runtime.Faults);
        }

        /// <summary>The line about a pose holds its values with a dot for
        /// the decimal and an ASCII minus. The second pose is read against
        /// the first, the neutral: 1.5 cm to the right, 2 cm down, half a
        /// meter back, looking 30 degrees down.</summary>
        [Fact]
        public void APoseIsWrittenWithADotAndAnAsciiMinus()
        {
            using var runtime = new OpenXrScriptedRuntime("Runtime X");
            int idle = 0;
            runtime.OnSessionCreated = _ => runtime.QueueSessionState(XR_SESSION_STATE_READY);
            runtime.OnIdlePoll = () =>
            {
                if (++idle == 4) runtime.QueueSessionState(XR_SESSION_STATE_EXITING);
            };
            var log = new List<string>();

            var source = Play(() => Array.Empty<OpenXrRuntimeEntry>(), runtime.Load, log, choice: runtime.ManifestPath,
                onSleep: (played, _) =>
                {
                    if (played.SampleCount != 1) return;
                    runtime.Head = new XrPosef
                    {
                        position = new XrVector3f { x = 0.015f, y = -0.02f, z = 0.5f },
                        // A turn of -30 degrees about the X axis.
                        orientation = new XrQuaternionf { x = -0.25881904f, w = 0.9659258f },
                    };
                });

            Assert.Equal(OpenXrSourceState.Ended, source.State);
            Assert.Equal("Runtime X", source.RuntimeName);
            Assert.Contains("OpenXR: head pose #2 via Runtime X yaw=0.0 pitch=-30.0 x=1.5 y=-2.0 z=-50.0", log);
            Assert.Empty(runtime.Faults);
        }

        private const string MadeUpFormat = "made up";

        /// <summary>A regional format by its name, or one whose minus sign
        /// and decimal separator belong to no real format, so the cases
        /// below bite on a PC whose culture data gives Swedish an ASCII
        /// minus.</summary>
        private static CultureInfo Format(string name)
        {
            if (name != MadeUpFormat) return new CultureInfo(name);
            var made = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            made.NumberFormat.NegativeSign = "~";
            made.NumberFormat.NumberDecimalSeparator = "!";
            return made;
        }

        /// <summary>
        /// A result code is negative, and the minus sign of a PC's regional
        /// format is not always the ASCII hyphen. It is U+2212 under a
        /// Swedish, Norwegian or Finnish format, and stands behind a
        /// direction mark under a Hebrew or an Arabic one. The lines carry
        /// every number in the invariant culture, so the log of such a PC
        /// reads like any other, and a search of it for "-35" finds the
        /// line. The cases that pin a code, a loss time or a pose run again
        /// here under each of those formats, under a German one for its
        /// decimal comma, and under a made-up one.
        /// </summary>
        [Theory]
        [InlineData("sv-SE")]
        [InlineData("nb-NO")]
        [InlineData("fi-FI")]
        [InlineData("he-IL")]
        [InlineData("ar-SA")]
        [InlineData("de-DE")]
        [InlineData(MadeUpFormat)]
        public void TheLinesReadTheSameUnderEveryRegionalFormat(string format)
        {
            var keep = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = Format(format);

                // Each call a link makes whose answer is handed back.
                AHeadsetThatIsAwayIsAskedForAgainOnTheSameInstance();
                AnInstanceLostAtTheAskForTheHeadsetEndsAsEnded();
                AnErrorFromTheAskForTheHeadsetIsAFailedCall(RuntimeFailure, 0);
                ARefusedSessionSaysWhy(InitializationFailed, OpenXrSourceState.NotSupported);
                ASessionWithNoUsableReferenceSpaceSaysWhy(
                    XR_REFERENCE_SPACE_TYPE_VIEW, ReferenceSpaceUnsupported, OpenXrSourceState.NotSupported, "view space");
                ARefusedInstanceSaysWhetherTheRuntimeIsAway(RuntimeUnavailable, true);
                AListingThatFailsIsNotReadAsAMissingExtension(RuntimeUnavailable, 0, true);
                ANegotiationThatFailsSaysWhatItAnswered();
                TheLossWaitIsReadOffTheEventOnTheRuntimesClock(-5_000_000_000L, 500);
                ThePumpReadsEachEndOffTheRuntime();
                ThePumpReadsWhatTheBeginAnswered(
                    ViewConfigurationTypeUnsupported, nameof(OpenXrSessionEnd.BeginRefused), false);
                ARefusedEndIsSaidWithItsCode();

                // The source's lines over the real session.
                AHeadsetWaitThatFailsOnTheRealSessionSettlesAsFailed();
                AHeadsetThatComesBackEndsTheWaitWithALine();
                ARuntimeThatAnswersAwayIsAskedAtTheCadenceAndSaidOnce(false, "Runtime X answered xrCreateInstance with -51");
                ARuntimeThatAnswersAwayIsAskedAtTheCadenceAndSaidOnce(
                    true, "Runtime X answered xrEnumerateInstanceExtensionProperties with -51");
                ASessionTheRuntimeWillNotBeginStopsTheSource();
                ARefusedBeginAfterALostSessionReadsEnded();
                APoseIsWrittenWithADotAndAnAsciiMinus();
            }
            finally { CultureInfo.CurrentCulture = keep; }
        }

        /// <summary>
        /// What the runtime probe asks of a source
        /// (<see cref="OpenXrRuntimeProbeTests.AssertTheSourceCameBack"/>),
        /// on scripted runtimes, since the probe itself starts a real one
        /// and a normal run skips it. A source that settled as Failed fails
        /// the probe with its last line, which names the call and what it
        /// answered. So does one that found no runtime in a library that is
        /// on disk. A runtime that answers with a refusal passes.
        /// </summary>
        [Fact]
        public void TheProbeFailsOnAFailedSourceWithItsLastLine()
        {
            static OpenXrHeadPoseSource Run(OpenXrScriptedRuntime runtime, List<string> log)
                => Play(() => Array.Empty<OpenXrRuntimeEntry>(), runtime.Load, log, choice: runtime.ManifestPath);

            using (var runtime = new OpenXrScriptedRuntime("Runtime X"))
            {
                runtime.SystemAnswers.Enqueue(RuntimeFailure);
                var log = new List<string>();
                var source = Run(runtime, log);

                Assert.Equal(OpenXrSourceState.Failed, source.State);
                var failed = Assert.Throws<Xunit.Sdk.TrueException>(
                    () => OpenXrRuntimeProbeTests.AssertTheSourceCameBack(source.State, log));
                Assert.Equal("the source settled as Failed, and its last line says why: " +
                             "OpenXR: Runtime X gave no session at ask 1 (xrGetSystem answered -2), so the source stops (Failed)",
                             failed.Message);
            }

            using (var runtime = new OpenXrScriptedRuntime("Runtime X") { Loads = false })
            {
                var log = new List<string>();
                var source = Run(runtime, log);

                Assert.Equal(OpenXrSourceState.NoRuntime, source.State);
                var failed = Assert.Throws<Xunit.Sdk.TrueException>(
                    () => OpenXrRuntimeProbeTests.AssertTheSourceCameBack(source.State, log));
                Assert.StartsWith("the runtime's library is on disk and the source found no runtime in it, and its last line says why: " +
                                  "OpenXR: no instance was created (the library of Runtime X, ", failed.Message);
                Assert.EndsWith(", did not load), so the source stops (NoRuntime)", failed.Message);
            }

            using (var runtime = new OpenXrScriptedRuntime("Runtime X"))
            {
                runtime.SessionAnswers.Enqueue(InitializationFailed);
                var log = new List<string>();
                var source = Run(runtime, log);

                Assert.Equal(OpenXrSourceState.NotSupported, source.State);
                OpenXrRuntimeProbeTests.AssertTheSourceCameBack(source.State, log);
            }

            var stuck = Assert.Throws<Xunit.Sdk.TrueException>(
                () => OpenXrRuntimeProbeTests.AssertTheSourceCameBack(OpenXrSourceState.Connecting, Array.Empty<string>()));
            Assert.Contains("never left Connecting", stuck.Message);
            var silent = Assert.Throws<Xunit.Sdk.TrueException>(
                () => OpenXrRuntimeProbeTests.AssertTheSourceCameBack(OpenXrSourceState.Failed, Array.Empty<string>()));
            Assert.EndsWith("(it wrote no line)", silent.Message);
        }

        /// <summary>The runtime's own name is used once it gives one, and
        /// the manifest's name stands for a runtime that gives none.</summary>
        [Theory]
        [InlineData("Its Own Name", "Its Own Name")]
        [InlineData(null, "Manifest Name")]
        public void TheLinkIsNamedByTheRuntimeOrByItsManifest(string instanceName, string expected)
        {
            using var rt = new OpenXrScriptedRuntime("Manifest Name") { InstanceName = instanceName };
            using (var session = Open(rt))
                Assert.Equal(expected, session.RuntimeName);
            Assert.Empty(rt.Faults);
        }
    }
}

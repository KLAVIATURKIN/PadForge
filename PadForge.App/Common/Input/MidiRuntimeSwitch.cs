using System;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Common.Input
{
    /// <summary>What an uninstall of the App SDK runtime holds while its
    /// uninstaller runs
    /// (<see cref="MidiVirtualController.TryHoldWithoutRelease"/>).</summary>
    internal enum MidiUninstallHold
    {
        /// <summary>Nothing. The in-box API runs, which never reaches the
        /// runtime.</summary>
        None = 0,

        /// <summary>The legacy API keeps running, and no Windows MIDI
        /// Services backend starts.</summary>
        StartHeld = 1,

        /// <summary>MIDI is released and stays released.</summary>
        Released = 2,
    }

    /// <summary>
    /// The App SDK runtime's install and uninstall from the Settings card, as
    /// far as MIDI itself is concerned: releasing MIDI before the uninstaller
    /// runs, ending that release once the uninstaller exits, and moving MIDI
    /// onto a runtime whose install finished after PadForge stopped
    /// following it. Every step that can wait on the MIDI service runs on the
    /// card's worker, never the UI thread: a release or a reset waits behind
    /// a probe that hangs on a dead service, and on the UI thread that froze
    /// the window.
    /// </summary>
    internal static class MidiRuntimeSwitch
    {
        /// <summary>How long the release before an uninstall may take. It
        /// waits behind a probe that hangs until the recovery's service
        /// restart frees it. From the card turning Not Running that is up to
        /// about 50 s when no other restart's cooldown holds it back: 5 s
        /// more of watching the probe, to the create path's bound, then a
        /// restart of up to 45 s. It also waits on the shared input
        /// session's teardown, which is service RPC with no bound of its own
        /// (<see cref="MidiInputRuntime.Shutdown"/>). A call that first
        /// waits for an earlier release gives the worker it then starts the
        /// bound again, so the card waits twice this at most. Tests set
        /// it.</summary>
        internal static int ReleaseTimeoutMs = 60_000;

        /// <summary>Test seam: stands in for the tick count a release measures
        /// its bounds and its worker's time at work against, so a test moves
        /// the clock by hand. Null in production.</summary>
        internal static Func<long> TickSeam;

        private static long NowMs => TickSeam?.Invoke() ?? Environment.TickCount64;

        /// <summary>One release: its worker, and what the call waiting on it
        /// decided.</summary>
        private sealed class Release
        {
            public readonly ManualResetEventSlim Done = new(false);

            // Under the lock on this object: whether the worker has taken
            // its hold, what it took, and whether its caller gave up first.
            public bool Finished;
            public bool Abandoned;
            public MidiUninstallHold Hold;

            // Set by the worker as it starts closing MIDI input.
            public volatile bool InputsDown;

            // True while the worker is inside that teardown, which waits on
            // the service whatever a probe waits on.
            public volatile bool InTeardown;

            // When the worker began its work, or 0 while it has not. Read
            // and written through Interlocked.
            public long WorkBeganMs;
        }

        // The newest release, under s_newestLock. One release is in flight
        // at a time: a call that finds an earlier one still at work waits
        // for it within its own bound and starts no worker beside it.
        private static readonly object s_newestLock = new();
        private static Release s_newest;

        // A release's worker, through its undo, and the end of a release run
        // one at a time. The end lifts the latch and resumes MIDI input, and
        // a resume that landed between a worker closing MIDI input and
        // latching would let the next sweep open sessions on the runtime
        // again, with the uninstaller about to start. The callers never take
        // this lock: they wait on their release for their own bound.
        private static readonly object s_releaseLock = new();

        /// <summary>
        /// Takes what the runtime's uninstall holds while its uninstaller
        /// runs, and says what in <paramref name="hold"/>. Under the in-box
        /// API that is nothing, and under the legacy API only Windows MIDI
        /// Services is held from starting while MIDI keeps running
        /// (<see cref="MidiVirtualController.TryHoldWithoutRelease"/>).
        /// Otherwise MIDI is released: MIDI input closes
        /// (<paramref name="shutdownInputs"/>), then the API is released and
        /// latched released
        /// (<see cref="MidiVirtualController.SuppressForUninstall"/>).
        ///
        /// <para>Runs on a worker and waits for it up to
        /// <see cref="ReleaseTimeoutMs"/>. A call that finds an earlier
        /// release still at work waits for that one first, up to the same
        /// bound, and its own worker then gets the whole bound again. False
        /// when a wait ran out, and the uninstall must not start then. A
        /// release that gave up leaves nothing off: MIDI input resumes at
        /// once (<paramref name="resumeInputs"/>) when the worker had begun
        /// closing it, since the call the worker waits on may never return,
        /// and the worker undoes whatever it still takes when it gets
        /// through (<see cref="EndUninstall"/>). A worker that has been at
        /// work for the create path's bound without finishing is also the
        /// wedged-service signature, so the service restart is asked for
        /// (<see cref="AskForServiceRestart"/>), which is what lets the next
        /// Uninstall through.</para>
        ///
        /// <para>A release that returned true is ended by its caller with
        /// the hold it was handed, once the uninstaller has exited or never
        /// started.</para>
        /// </summary>
        internal static bool ReleaseForUninstall(Action shutdownInputs, Action resumeInputs, out MidiUninstallHold hold)
        {
            hold = MidiUninstallHold.None;
            long deadline = NowMs + ReleaseTimeoutMs;
            Release mine;
            while (true)
            {
                Release earlier;
                lock (s_newestLock)
                {
                    earlier = s_newest;
                    if (earlier == null || earlier.Done.IsSet)
                    {
                        mine = new Release();
                        s_newest = mine;
                        var started = mine;
                        Task.Run(() => Work(started, shutdownInputs, resumeInputs));
                        break;
                    }
                }
                // An earlier release is still at work: it missed its bound
                // and is on its way to undoing itself, or another call still
                // waits on it. This call starts once that one is done with
                // MIDI input and the latch.
                MidiVirtualController.SyncPointForTest?.Invoke("release-waits-for-earlier");
                if (!earlier.Done.Wait(Remaining(deadline)))
                {
                    AskForServiceRestart(earlier);
                    return false;
                }
            }

            // The worker gets a bound of its own. A call that waited for an
            // earlier release has spent some or all of the bound it came in
            // with on that wait, and with one bound for both, a worker
            // started as it ran out was given up on at once though it was
            // going through. The card waits twice the bound at most.
            deadline = NowMs + ReleaseTimeoutMs;
            MidiVirtualController.SyncPointForTest?.Invoke("release-waits");
            if (!mine.Done.Wait(Remaining(deadline)))
            {
                lock (mine)
                {
                    if (!mine.Finished)
                    {
                        mine.Abandoned = true;
                        // The worker still holds the release lock, so no
                        // later release has closed MIDI input since.
                        if (mine.InputsDown)
                        {
                            try { resumeInputs?.Invoke(); }
                            catch { /* best effort */ }
                        }
                    }
                }
                if (mine.Abandoned)
                {
                    AskForServiceRestart(mine);
                    return false;
                }
                // Finished in the instant after the wait: the uninstall can
                // go.
            }
            lock (mine) hold = mine.Hold;
            return true;
        }

        private static int Remaining(long deadline)
            => (int)Math.Clamp(deadline - NowMs, 0, int.MaxValue);

        /// <summary>Whether a release's worker is still at work, the undo of
        /// one that gave up included.</summary>
        internal static bool ReleaseInFlight
        {
            get { lock (s_newestLock) return s_newest != null && !s_newest.Done.IsSet; }
        }

        private static bool GaveUp(Release release)
        {
            lock (release) return release.Abandoned;
        }

        // The release whose worker this thread is, while it closes MIDI
        // input.
        [ThreadStatic] private static Release t_closingInputs;

        /// <summary>True on a release's worker, inside its teardown of MIDI
        /// input, once the call that waited on that release has given up.
        /// The teardown asks as it begins, after it has suppressed the input
        /// sweep (<see cref="InputManager.ShutdownMidiInputs"/>). A call that
        /// gave up in the instant before has resumed MIDI input already, and
        /// that resume would be lost behind the suppression, with a teardown
        /// that can hang on a wedged service to follow.</summary>
        internal static bool CallerGaveUp => t_closingInputs is Release mine && GaveUp(mine);

        /// <summary>The release itself, on its worker. A release whose caller
        /// gave up before the worker reached MIDI input closes nothing and
        /// latches nothing. One whose caller gave up as the worker reached
        /// MIDI input closes nothing either (<see cref="CallerGaveUp"/>),
        /// and the latch it then takes is undone with the rest.</summary>
        private static void Work(Release mine, Action shutdownInputs, Action resumeInputs)
        {
            MidiVirtualController.SyncPointForTest?.Invoke("release-worker");
            lock (s_releaseLock)
            {
                Interlocked.Exchange(ref mine.WorkBeganMs, Math.Max(1, NowMs));
                var hold = MidiUninstallHold.None;
                try
                {
                    if (!GaveUp(mine))
                    {
                        var needed = MidiVirtualController.TryHoldWithoutRelease();
                        if (needed != MidiUninstallHold.Released)
                        {
                            hold = needed;
                        }
                        else if (!GaveUp(mine))
                        {
                            // Marked as held before the latch is taken, so a
                            // teardown that throws partway is still ended.
                            mine.InputsDown = true;
                            mine.InTeardown = true;
                            hold = MidiUninstallHold.Released;
                            t_closingInputs = mine;
                            try { shutdownInputs?.Invoke(); }
                            finally
                            {
                                t_closingInputs = null;
                                mine.InTeardown = false;
                                MidiVirtualController.SuppressForUninstall();
                            }
                        }
                    }
                }
                catch { /* best effort, as every MIDI teardown */ }
                finally
                {
                    bool undo;
                    lock (mine)
                    {
                        mine.Hold = hold;
                        mine.Finished = true;
                        undo = mine.Abandoned;
                    }
                    if (undo) EndUninstall(hold, resumeInputs);
                    mine.Done.Set();
                }
            }
        }

        /// <summary>
        /// Asks for a service restart on behalf of a release that did not
        /// finish. What it waits on is service RPC: the shared input
        /// session's teardown, or a Windows MIDI Services start the refusal
        /// check began. Nothing else restarts the service for those, so MIDI
        /// input stayed off and every later Uninstall gave up behind the same
        /// call.
        ///
        /// <para>The signature is the worker's own time at work, not the
        /// call's wait: the restart is asked for only once the worker has
        /// itself been at work for the create path's bound
        /// (<see cref="MidiVirtualController.ConnectTimeoutMs"/>), the bound
        /// a hung create gets before its restart. A call can run out of time
        /// on a worker that began late, and restarting a service that has
        /// just answered drops other programs' MIDI sessions.</para>
        ///
        /// <para>A worker that waits behind a probe that hangs is the
        /// exception: the probe's recovery already restarts the service where
        /// a restart can reach the hang, and waits for a classic driver where
        /// none can (<see cref="MidiVirtualController.ProbeHung"/>). A worker
        /// inside the input teardown waits on the service whatever a probe
        /// waits on, so its restart is asked for either way. The restart's
        /// cooldown and API-mode gates apply
        /// (<see cref="MidiVirtualController.RestartService"/>). It runs on a
        /// worker, since a restart takes up to 45 s and the card has its
        /// answer already.</para>
        /// </summary>
        private static void AskForServiceRestart(Release waitedOn)
        {
            long began = Interlocked.Read(ref waitedOn.WorkBeganMs);
            if (began == 0 || NowMs - began < MidiVirtualController.ConnectTimeoutMs) return;
            if (!waitedOn.InTeardown && MidiVirtualController.ProbeHung) return;
            MidiVirtualController.SyncPointForTest?.Invoke("release-asks-for-restart");
            Task.Run(() => MidiVirtualController.RestartService(
                "the MIDI release for an uninstall did not finish", legacyCall: false));
        }

        /// <summary>
        /// Ends one release with the hold it took: its uninstaller exited or
        /// never started, or the release missed its bound and undoes itself.
        /// The latch belongs to the releases that hold it, so it lifts, and
        /// MIDI input resumes (<paramref name="resumeInputs"/>), only when
        /// this was the last one holding it
        /// (<see cref="MidiVirtualController.EndUninstallHold"/>). While
        /// another uninstaller still runs, both stay as they are, and that
        /// one's end lifts them. A hold that left MIDI running ends with
        /// nothing to resume.
        /// </summary>
        internal static void EndUninstall(MidiUninstallHold hold, Action resumeInputs)
        {
            MidiVirtualController.SyncPointForTest?.Invoke("end-before-release-lock");
            lock (s_releaseLock)
            {
                MidiVirtualController.SyncPointForTest?.Invoke("end-in-release-lock");
                try
                {
                    if (MidiVirtualController.EndUninstallHold(hold)) resumeInputs?.Invoke();
                }
                catch { /* best effort */ }
            }
        }

        /// <summary>
        /// Ends the release once <paramref name="exited"/> completes: the
        /// uninstaller outlived its five-minute wait, and the latch held MIDI
        /// off for the rest of the session. The end is the one the uninstall
        /// makes when its uninstaller exits in time
        /// (<see cref="EndUninstall"/>), then <paramref name="refresh"/> for
        /// the card. It never runs before the exit, since a probe while the
        /// uninstaller runs could load the files it is removing.
        /// </summary>
        internal static Task LiftWhenExited(Task exited, MidiUninstallHold hold, Action resumeInputs, Action refresh)
        {
            if (exited == null) exited = Task.CompletedTask;
            return exited.ContinueWith(_ =>
            {
                EndUninstall(hold, resumeInputs);
                try { refresh?.Invoke(); } catch { }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        // ── The card's two operations ──

        private static int s_uninstallersRunning;

        /// <summary>Whether a runtime uninstaller PadForge started has not
        /// exited yet, the five minutes it is waited on included. The card
        /// offers no second Uninstall meanwhile: Windows Installer runs one
        /// installation at a time, so a second one only fails.</summary>
        internal static bool UninstallerRunning => Volatile.Read(ref s_uninstallersRunning) != 0;

        /// <summary>
        /// The card's install, on its worker: <paramref name="install"/>,
        /// then <paramref name="switchApi"/>, which moves MIDI onto the
        /// runtime. Both wait on things the UI thread must not: the
        /// installer, and a reset that waits behind a probe hung on a dead
        /// service. An installer whose outcome PadForge could not follow (it
        /// outlived its wait, or Windows handed back no process) is marked
        /// for the card's refresh, which switches once the runtime is in
        /// place (<see cref="TakePendingSwitch"/>), and its failure goes on
        /// to the status line. An install that finished leaves nothing to
        /// watch for.
        /// </summary>
        internal static void RunInstall(Action install, Action switchApi)
        {
            try { install(); }
            catch (InstallerFailedException ex) when (ex.OutcomeUnknown)
            {
                MarkInstallPending();
                throw;
            }
            ClearPendingInstall();
            switchApi?.Invoke();
        }

        /// <summary>
        /// The card's uninstall, on its worker: the release
        /// (<see cref="ReleaseForUninstall"/>), then
        /// <paramref name="uninstall"/>, which answers whether the uninstaller
        /// exited within its wait and hands back a task for its exit. The
        /// release ends once the uninstaller has exited (done, failed or
        /// canceled) or never started. One still running after its wait ends
        /// its release when it exits, never before, since a probe then could
        /// load the files it is deleting. An uninstaller that failed, such
        /// as a second one refused while the first still runs, ends only the
        /// hold this call took, so the first one keeps its own.
        /// </summary>
        /// <exception cref="DriverCheckException">The release did not finish
        /// in time, and the uninstaller was not started:
        /// <see cref="DriverCheckFailure.MidiReleaseTimedOutOnDriver"/> where
        /// it waited behind a probe hung in a classic MIDI driver, which the
        /// status line then names in place of the MIDI service, else
        /// <see cref="DriverCheckFailure.MidiReleaseTimedOut"/>.</exception>
        internal static void RunUninstall(Func<(bool Finished, Task Exited)> uninstall,
            Action shutdownInputs, Action resumeInputs, Action refresh)
        {
            // An install still watched for is moot once its runtime is
            // being removed.
            ClearPendingInstall();
            if (!ReleaseForUninstall(shutdownInputs, resumeInputs, out var hold))
            {
                if (MidiVirtualController.ProbeHungInDriver)
                    throw new DriverCheckException(DriverCheckFailure.MidiReleaseTimedOutOnDriver,
                        "A classic MIDI driver did not answer, so MIDI was not released and the runtime's uninstaller was not started.");
                throw new DriverCheckException(DriverCheckFailure.MidiReleaseTimedOut,
                    "MIDI was not released in time, so the runtime's uninstaller was not started.");
            }

            Interlocked.Increment(ref s_uninstallersRunning);
            bool finished = true;
            Task exited = null;
            try { (finished, exited) = uninstall(); }
            finally
            {
                if (finished)
                {
                    Interlocked.Decrement(ref s_uninstallersRunning);
                    EndUninstall(hold, resumeInputs);
                }
                else
                {
                    var counted = (exited ?? Task.CompletedTask).ContinueWith(
                        _ => Interlocked.Decrement(ref s_uninstallersRunning),
                        CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                    LiftWhenExited(counted, hold, resumeInputs, refresh);
                }
            }
        }

        // ── An install PadForge stopped following ──

        /// <summary>How long a runtime install whose outcome PadForge could
        /// not follow is watched for.</summary>
        internal const long PendingInstallWatchMs = 30 * 60_000;

        private static long s_pendingInstallSinceMs;

        /// <summary>Marks a runtime install PadForge started and stopped
        /// following: it outlived its wait, or Windows handed back no
        /// process. The card's refresh switches MIDI to the runtime once it
        /// is in place (<see cref="TakePendingSwitch"/>).</summary>
        internal static void MarkInstallPending()
            => Interlocked.Exchange(ref s_pendingInstallSinceMs, Math.Max(1, Environment.TickCount64));

        internal static bool InstallPending => Interlocked.Read(ref s_pendingInstallSinceMs) != 0;

        /// <summary>True once, when a pending install has put the runtime in
        /// place under an engine still on the legacy API or on none
        /// (<see cref="MidiApiSelection.SwitchAfterPendingInstall"/>). The
        /// caller then switches MIDI to it off the UI thread. A pending
        /// install not done within <see cref="PendingInstallWatchMs"/> is
        /// dropped, so a failed install is not watched forever. Only an install
        /// PadForge started is watched: a runtime installed outside PadForge
        /// does not move a running legacy setup. While an uninstall holds
        /// anything (<paramref name="heldForUninstall"/>) nothing switches
        /// and the install stays watched.</summary>
        internal static bool TakePendingSwitch(bool runtimeInstalled, MidiApiKind engineActive, bool heldForUninstall)
        {
            long since = Interlocked.Read(ref s_pendingInstallSinceMs);
            if (since == 0) return false;
            if (Environment.TickCount64 - since > PendingInstallWatchMs)
            {
                Interlocked.CompareExchange(ref s_pendingInstallSinceMs, 0, since);
                return false;
            }
            if (!MidiApiSelection.SwitchAfterPendingInstall(true, runtimeInstalled, engineActive, heldForUninstall)) return false;
            return Interlocked.CompareExchange(ref s_pendingInstallSinceMs, 0, since) == since;
        }

        /// <summary>Forgets a pending install: one that finished in time
        /// took its place, or the runtime is being uninstalled.</summary>
        internal static void ClearPendingInstall() => Interlocked.Exchange(ref s_pendingInstallSinceMs, 0);

        /// <summary>Test seam: forgets the releases and uninstallers a test
        /// left behind, and its clock.</summary>
        internal static void ResetForTest()
        {
            TickSeam = null;
            lock (s_newestLock) s_newest = null;
            Interlocked.Exchange(ref s_uninstallersRunning, 0);
            ClearPendingInstall();
        }
    }
}

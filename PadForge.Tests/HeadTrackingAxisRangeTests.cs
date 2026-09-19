using System;
using System.IO;
using System.Linq;
using PadForge.Common.Input;
using PadForge.ViewModels;
using PadForge.Engine.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The per-axis ranges and the recenter counter (issue #403).
    ///
    /// <para>The reporter's ask was to place where a tuck, a neutral and a
    /// sit up each land, which one shared translation range cannot express:
    /// head elevation on a bike moves a few centimeters where leaning moves
    /// twenty. Each axis therefore carries its own range, and zero means it
    /// follows its family, so a setup that never touches this keeps behaving
    /// exactly as it did.</para>
    ///
    /// <para>These join the statics collection because
    /// <see cref="HeadTrackingRuntime"/> is static process state, and a test
    /// that pinned an axis while another read the family range would flake.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class HeadTrackingAxisRangeTests : IDisposable
    {
        public HeadTrackingAxisRangeTests() => Clear();

        public void Dispose() => Clear();

        private static void Clear()
        {
            for (int axis = 0; axis < HeadPose.AxisCount; axis++)
                HeadTrackingRuntime.SetAxisRange(axis, 0);
            HeadTrackingRuntime.RotationRangeDeg = HeadTrackingRuntime.DefaultRotationRangeDeg;
            HeadTrackingRuntime.TranslationRangeCm = HeadTrackingRuntime.DefaultTranslationRangeCm;
        }

        [Fact]
        public void AnUntouchedAxisFollowsItsFamily()
        {
            HeadTrackingRuntime.RotationRangeDeg = 70;
            HeadTrackingRuntime.TranslationRangeCm = 25;

            Assert.Equal(70, HeadTrackingRuntime.GetAxisRange(HeadPose.AxisYaw));
            Assert.Equal(70, HeadTrackingRuntime.GetAxisRange(HeadPose.AxisPitch));
            Assert.Equal(70, HeadTrackingRuntime.GetAxisRange(HeadPose.AxisRoll));
            Assert.Equal(25, HeadTrackingRuntime.GetAxisRange(HeadPose.AxisX));
            Assert.Equal(25, HeadTrackingRuntime.GetAxisRange(HeadPose.AxisY));
            Assert.Equal(25, HeadTrackingRuntime.GetAxisRange(HeadPose.AxisZ));
        }

        [Fact]
        public void PinningOneAxisLeavesTheOtherFiveOnTheFamily()
        {
            HeadTrackingRuntime.TranslationRangeCm = 25;
            HeadTrackingRuntime.SetAxisRange(HeadPose.AxisY, 6);

            Assert.Equal(6, HeadTrackingRuntime.GetAxisRange(HeadPose.AxisY));
            Assert.Equal(25, HeadTrackingRuntime.GetAxisRange(HeadPose.AxisX));
            Assert.Equal(25, HeadTrackingRuntime.GetAxisRange(HeadPose.AxisZ));
            Assert.True(HeadTrackingRuntime.AxisRangeIsPinned(HeadPose.AxisY));
            Assert.False(HeadTrackingRuntime.AxisRangeIsPinned(HeadPose.AxisX));
        }

        [Fact]
        public void MovingTheFamilyMovesTheFollowersAndNotThePinnedOne()
        {
            HeadTrackingRuntime.SetAxisRange(HeadPose.AxisY, 6);
            HeadTrackingRuntime.TranslationRangeCm = 40;

            Assert.Equal(6, HeadTrackingRuntime.GetAxisRange(HeadPose.AxisY));
            Assert.Equal(40, HeadTrackingRuntime.GetAxisRange(HeadPose.AxisX));
        }

        [Fact]
        public void ZeroReturnsAnAxisToItsFamily()
        {
            HeadTrackingRuntime.TranslationRangeCm = 30;
            HeadTrackingRuntime.SetAxisRange(HeadPose.AxisZ, 5);
            Assert.Equal(5, HeadTrackingRuntime.GetAxisRange(HeadPose.AxisZ));

            HeadTrackingRuntime.SetAxisRange(HeadPose.AxisZ, 0);
            Assert.Equal(30, HeadTrackingRuntime.GetAxisRange(HeadPose.AxisZ));
            Assert.False(HeadTrackingRuntime.AxisRangeIsPinned(HeadPose.AxisZ));
        }

        [Theory]
        [InlineData(HeadPose.AxisYaw, 180)]
        [InlineData(HeadPose.AxisPitch, 180)]
        [InlineData(HeadPose.AxisRoll, 180)]
        [InlineData(HeadPose.AxisX, 500)]
        [InlineData(HeadPose.AxisY, 500)]
        [InlineData(HeadPose.AxisZ, 500)]
        public void EachAxisClampsToItsOwnUnitsLimit(int axis, int limit)
        {
            HeadTrackingRuntime.SetAxisRange(axis, limit + 1000);
            Assert.Equal(limit, HeadTrackingRuntime.GetAxisRange(axis));
        }

        [Fact]
        public void AnOutOfRangeAxisIndexIsIgnoredRatherThanThrowing()
        {
            // The view model indexes these by a constant, but the settings
            // load walks whatever length the saved array had.
            HeadTrackingRuntime.SetAxisRange(-1, 10);
            HeadTrackingRuntime.SetAxisRange(HeadPose.AxisCount, 10);
            Assert.Equal(0, HeadTrackingRuntime.GetAxisRange(-1));
            Assert.Equal(0, HeadTrackingRuntime.GetAxisRange(HeadPose.AxisCount));
            Assert.False(HeadTrackingRuntime.AxisRangeIsPinned(-1));
        }

        [Fact]
        public void WhatGetsSavedIsThePinAndNotTheResolvedRange()
        {
            // Saving the resolved range would pin all six the first time a
            // user's settings were written, and moving the family range
            // afterwards would then move nothing.
            HeadTrackingRuntime.TranslationRangeCm = 25;
            HeadTrackingRuntime.SetAxisRange(HeadPose.AxisY, 6);

            Assert.Equal(6, HeadTrackingRuntime.GetAxisRangeOverride(HeadPose.AxisY));
            Assert.Equal(0, HeadTrackingRuntime.GetAxisRangeOverride(HeadPose.AxisX));
            Assert.Equal(25, HeadTrackingRuntime.GetAxisRange(HeadPose.AxisX));
        }

        [Fact]
        public void APinSurvivesASaveAndLoadRoundTrip()
        {
            HeadTrackingRuntime.SetAxisRange(HeadPose.AxisY, 6);
            HeadTrackingRuntime.SetAxisRange(HeadPose.AxisRoll, 45);

            int[] saved = Enumerable.Range(0, HeadPose.AxisCount)
                                    .Select(HeadTrackingRuntime.GetAxisRangeOverride)
                                    .ToArray();
            Clear();
            Assert.Equal(0, HeadTrackingRuntime.GetAxisRangeOverride(HeadPose.AxisY));

            for (int axis = 0; axis < HeadPose.AxisCount; axis++)
                HeadTrackingRuntime.SetAxisRange(axis, saved[axis]);

            Assert.Equal(6, HeadTrackingRuntime.GetAxisRange(HeadPose.AxisY));
            Assert.Equal(45, HeadTrackingRuntime.GetAxisRange(HeadPose.AxisRoll));
            Assert.False(HeadTrackingRuntime.AxisRangeIsPinned(HeadPose.AxisX));
        }

        [Fact]
        public void ASettingsFileWithNoRangesLeavesEveryAxisOnItsFamily()
        {
            // The load path reads a null array for any file written before
            // this shipped, which must not pin anything.
            HeadTrackingRuntime.TranslationRangeCm = 30;
            int[] savedRanges = null;
            for (int axis = 0; axis < HeadPose.AxisCount; axis++)
                HeadTrackingRuntime.SetAxisRange(
                    axis, savedRanges != null && axis < savedRanges.Length ? savedRanges[axis] : 0);

            Assert.Equal(30, HeadTrackingRuntime.GetAxisRange(HeadPose.AxisX));
            Assert.False(HeadTrackingRuntime.AxisRangeIsPinned(HeadPose.AxisX));
        }

        [Fact]
        public void AShortSavedArrayFillsWhatItHasAndFamiliesTheRest()
        {
            HeadTrackingRuntime.TranslationRangeCm = 30;
            int[] savedRanges = { 0, 0, 45 };
            for (int axis = 0; axis < HeadPose.AxisCount; axis++)
                HeadTrackingRuntime.SetAxisRange(
                    axis, savedRanges != null && axis < savedRanges.Length ? savedRanges[axis] : 0);

            Assert.Equal(45, HeadTrackingRuntime.GetAxisRange(HeadPose.AxisRoll));
            Assert.Equal(30, HeadTrackingRuntime.GetAxisRange(HeadPose.AxisX));
        }

        [Fact]
        public void TheSaveWritesTheOverrideAccessorAndTheLoadReadsTheArray()
        {
            // The wiring is what the dirty-gate trap eats: a value that lives
            // all session and reverts on restart because one of the three
            // sides was missing.
            string svc = RepoFile("PadForge.App", "Services", "SettingsService.cs");
            Assert.Contains("public int[] HeadTrackingAxisRanges", svc);
            Assert.Contains("HeadTrackingRuntime.GetAxisRangeOverride(", svc);
            Assert.Contains("appSettings.HeadTrackingAxisRanges", svc);
        }

        [Fact]
        public void EachPerAxisRangeScalesOnlyItsOwnAxis()
        {
            // One shared range cannot place a tuck and a lean at once, which
            // is the whole reason these exist.
            var pose = new double[HeadPose.PoseCount];
            pose[HeadPose.TY] = -3;   // 3 cm up, stored stick-oriented
            pose[HeadPose.TX] = 3;

            var axes = new int[HeadPose.AxisCount];
            HeadPose.FillAxesPerAxis(pose, axis => axis == HeadPose.AxisY ? 6 : 30, axes);

            // 3 of 6 cm is half deflection on Y. The same 3 cm is a tenth on
            // X, which still reads at 30 cm.
            int yOffset = axes[HeadPose.AxisY] - HeadPose.AxisCenter;
            int xOffset = axes[HeadPose.AxisX] - HeadPose.AxisCenter;
            Assert.True(yOffset > 0 && xOffset > 0);
            Assert.True(yOffset > xOffset * 4,
                        $"Y should be far past X at a fifth of the range: y={yOffset} x={xOffset}");
        }

        [Fact]
        public void TheBoxShowsThePinAndNotTheRangeInForce()
        {
            // Showing the resolved range would put the family's number in all
            // six boxes, where six following axes read as six pinned ones.
            HeadTrackingRuntime.TranslationRangeCm = 30;
            var vm = new DashboardViewModel();

            Assert.Equal(0, vm.HeadTrackingRangeX);
            Assert.Equal(0, vm.HeadTrackingRangeYaw);
        }

        [Fact]
        public void PinningAnAxisAtTheFamilysOwnNumberStillPinsIt()
        {
            // The defect this catches: an early-out that compared the typed
            // value against the RESOLVED range matched, so nothing was
            // written, and the axis moved the next time the family did.
            HeadTrackingRuntime.TranslationRangeCm = 30;
            var vm = new DashboardViewModel();

            vm.HeadTrackingRangeX = 30;
            Assert.True(HeadTrackingRuntime.AxisRangeIsPinned(HeadPose.AxisX));

            HeadTrackingRuntime.TranslationRangeCm = 50;
            Assert.Equal(30, HeadTrackingRuntime.GetAxisRange(HeadPose.AxisX));
        }

        [Fact]
        public void TypingZeroReturnsTheAxisToItsFamilyAndTheBoxKeepsTheZero()
        {
            HeadTrackingRuntime.TranslationRangeCm = 30;
            var vm = new DashboardViewModel();
            vm.HeadTrackingRangeY = 6;
            Assert.Equal(6, vm.HeadTrackingRangeY);

            vm.HeadTrackingRangeY = 0;

            Assert.Equal(0, vm.HeadTrackingRangeY);
            Assert.Equal(30, HeadTrackingRuntime.GetAxisRange(HeadPose.AxisY));
        }

        [Fact]
        public void EachRowsResetClearsThatRowAndTellsItsBox()
        {
            // Six per-row resets, one per axis. A bulk button that cleared all
            // six shipped alongside them and was removed: it duplicated the
            // six, and no other card carries a partial bulk reset.
            var vm = new DashboardViewModel();
            vm.HeadTrackingRangeYaw = 45;
            vm.HeadTrackingRangeZ = 8;

            var told = new System.Collections.Generic.List<string>();
            vm.PropertyChanged += (_, e) => told.Add(e.PropertyName);

            vm.ResetHeadTrackingRangeYawCommand.Execute(null);
            Assert.False(HeadTrackingRuntime.AxisRangeIsPinned(HeadPose.AxisYaw));
            // Clearing one row leaves every other pin where it was.
            Assert.True(HeadTrackingRuntime.AxisRangeIsPinned(HeadPose.AxisZ));
            Assert.Contains(nameof(DashboardViewModel.HeadTrackingRangeYaw), told);
            Assert.DoesNotContain(nameof(DashboardViewModel.HeadTrackingRangeZ), told);

            vm.ResetHeadTrackingRangeZCommand.Execute(null);
            Assert.False(HeadTrackingRuntime.AxisRangeIsPinned(HeadPose.AxisZ));
            Assert.Contains(nameof(DashboardViewModel.HeadTrackingRangeZ), told);
        }

        [Fact]
        public void EveryAxisHasAResetCommandOfItsOwn()
        {
            var vm = new DashboardViewModel();
            var commands = new[]
            {
                vm.ResetHeadTrackingRangeYawCommand, vm.ResetHeadTrackingRangePitchCommand,
                vm.ResetHeadTrackingRangeRollCommand, vm.ResetHeadTrackingRangeXCommand,
                vm.ResetHeadTrackingRangeYCommand, vm.ResetHeadTrackingRangeZCommand,
            };
            Assert.Equal(HeadPose.AxisCount, commands.Length);
            Assert.All(commands, Assert.NotNull);

            for (int axis = 0; axis < HeadPose.AxisCount; axis++)
                HeadTrackingRuntime.SetAxisRange(axis, axis <= HeadPose.AxisRoll ? 45 : 8);
            foreach (var c in commands) c.Execute(null);
            for (int axis = 0; axis < HeadPose.AxisCount; axis++)
                Assert.False(HeadTrackingRuntime.AxisRangeIsPinned(axis));
        }

        [Fact]
        public void EachBoxMarksTheSettingsFileDirtyOrThePinRevertsOnRestart()
        {
            // The dirty-gate trap: the value works all session, the file is
            // never marked dirty, and a normal close discards it.
            string mainWindow = RepoFile("PadForge.App", "MainWindow.xaml.cs");
            foreach (string name in new[]
                     {
                         "HeadTrackingRangeYaw", "HeadTrackingRangePitch", "HeadTrackingRangeRoll",
                         "HeadTrackingRangeX", "HeadTrackingRangeY", "HeadTrackingRangeZ",
                     })
                Assert.Contains("nameof(DashboardViewModel." + name + ")", mainWindow);
        }

        [Fact]
        public void RecenterCountsUpSoTwoInARowBothLand()
        {
            // A flag the source clears would race with the click that set it,
            // and the second click of two would be swallowed.
            int before = HeadTrackingRuntime.RecenterRequests;
            HeadTrackingRuntime.Recenter();
            HeadTrackingRuntime.Recenter();
            Assert.Equal(before + 2, HeadTrackingRuntime.RecenterRequests);
        }

        [Fact]
        public void TheOpenXrSourceDropsEveryBaselineWhenTheCountMoves()
        {
            string src = RepoFile("PadForge.Engine", "Common", "OpenXr", "OpenXrHeadPoseSource.cs");
            int at = src.IndexOf("_recenterRequests()", StringComparison.Ordinal);
            Assert.True(at > 0, "the source must read the recenter counter");
            string body = src.Substring(at, 600);
            // The head and both hands, because a recenter that moved the head
            // and left the controllers on an old zero is worse than none.
            Assert.Contains("baseline.Clear()", body);
            Assert.Contains("leftBaseline.Clear()", body);
            Assert.Contains("rightBaseline.Clear()", body);
        }

        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }
    }
}

using System;
using System.Linq;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The OpenXR runtime picker (issue #403).
    ///
    /// <para>The first version of this shipped to the bench and killed the
    /// app on launch. The collection's getter raised a change notification
    /// for the selected item, whose getter read the collection, which raised
    /// again. That recursion ends as a stack overflow, and a stack overflow
    /// takes the process down with no managed exception, no crash log and
    /// nothing in the event log worth reading. It was found by launching the
    /// deployed build, which is the only thing that would have found
    /// it.</para>
    ///
    /// <para>So the test is the cheap one: read both, in both orders, and
    /// require it to come back.</para>
    /// </summary>
    public class OpenXrRuntimePickerTests
    {
        /// <summary>
        /// Reading these properties the way a BINDING reads them terminates.
        ///
        /// <para>Reading them plainly is not the test. With nothing
        /// subscribed, a change notification goes nowhere and the recursion
        /// that killed the app cannot happen, so a plain read passes against
        /// the broken code and proves nothing. A binding re-reads the
        /// property it was told changed, and that is what closes the
        /// loop.</para>
        ///
        /// <para>The depth is bounded here rather than left to overflow,
        /// because a real stack overflow takes the test host down with it and
        /// reports nothing useful.</para>
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ReadingThemLikeABindingDoesTerminates(bool listFirst)
        {
            var vm = new DashboardViewModel();
            int depth = 0, deepest = 0;

            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(DashboardViewModel.SelectedOpenXrRuntime)) return;
                depth++;
                deepest = Math.Max(deepest, depth);
                // A binding re-reads what it was told about. Bail out well
                // before the stack does, so the failure is a message rather
                // than a dead process.
                if (depth < 32) _ = vm.SelectedOpenXrRuntime;
                depth--;
            };

            if (listFirst)
            {
                Assert.NotEmpty(vm.OpenXrRuntimes);
                Assert.NotNull(vm.SelectedOpenXrRuntime);
            }
            else
            {
                Assert.NotNull(vm.SelectedOpenXrRuntime);
                Assert.NotEmpty(vm.OpenXrRuntimes);
            }

            Assert.True(deepest < 4,
                $"reading the picker re-entered {deepest} deep, which is the recursion that "
                + "takes the process down with no crash log");
        }

        /// <summary>Refreshing is what a user does after installing a
        /// runtime, and it notifies. It must still terminate.</summary>
        [Fact]
        public void RefreshingTerminatesAndKeepsTheDefaultFirst()
        {
            var vm = new DashboardViewModel();
            vm.RefreshOpenXrRuntimes();
            vm.RefreshOpenXrRuntimes();
            Assert.NotEmpty(vm.OpenXrRuntimes);
            Assert.Equal(string.Empty, vm.OpenXrRuntimes[0].ManifestPath);
        }

        /// <summary>The machine's default is always offered, even where no
        /// runtime is installed, so the box is never empty.</summary>
        [Fact]
        public void TheSystemDefaultIsAlwaysAnOption()
        {
            var vm = new DashboardViewModel();
            Assert.Contains(vm.OpenXrRuntimes, r => r.ManifestPath == string.Empty);
            Assert.Equal(string.Empty, vm.SelectedOpenXrRuntime.ManifestPath);
        }

        /// <summary>Choosing one records it, and choosing the default clears
        /// it. Empty means the machine's default, which is what the source
        /// reads as "ask the registry".</summary>
        [Fact]
        public void ChoosingARuntimeRecordsItAndClearsBack()
        {
            string before = PadForge.Common.Input.HeadTrackingRuntime.OpenXrRuntimeManifest;
            try
            {
                var vm = new DashboardViewModel();
                var pick = new DashboardViewModel.OpenXrRuntimeChoice
                {
                    ManifestPath = @"C:\nowhere\runtime.json",
                    Display = "runtime",
                };
                vm.SelectedOpenXrRuntime = pick;
                Assert.Equal(@"C:\nowhere\runtime.json",
                             PadForge.Common.Input.HeadTrackingRuntime.OpenXrRuntimeManifest);

                vm.SelectedOpenXrRuntime = vm.OpenXrRuntimes.First(r => r.ManifestPath == string.Empty);
                Assert.Equal(string.Empty,
                             PadForge.Common.Input.HeadTrackingRuntime.OpenXrRuntimeManifest);
            }
            finally
            {
                PadForge.Common.Input.HeadTrackingRuntime.OpenXrRuntimeManifest = before;
            }
        }

        /// <summary>A runtime the user picked and then uninstalled still
        /// shows, or the box would read as the default while the saved
        /// setting still names the missing one.</summary>
        [Fact]
        public void AChosenRuntimeThatIsGoneStillAppears()
        {
            string before = PadForge.Common.Input.HeadTrackingRuntime.OpenXrRuntimeManifest;
            try
            {
                PadForge.Common.Input.HeadTrackingRuntime.OpenXrRuntimeManifest =
                    @"C:\uninstalled\runtime.json";
                var vm = new DashboardViewModel();
                Assert.Contains(vm.OpenXrRuntimes,
                                r => r.ManifestPath == @"C:\uninstalled\runtime.json");
                Assert.Equal(@"C:\uninstalled\runtime.json", vm.SelectedOpenXrRuntime.ManifestPath);
            }
            finally
            {
                PadForge.Common.Input.HeadTrackingRuntime.OpenXrRuntimeManifest = before;
            }
        }
    }
}

using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using PadForge.Resources.Strings;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The static hooks every pad view model subscribes to, the profile
    /// catalog's reload and a macro's rename, hold it weakly, the way
    /// Strings.CultureChanged does. Held strongly, they kept every view model
    /// a test made alive for the rest of the process, and each culture change
    /// rebuilt all of them, so the culture tests slowed with every class that
    /// ran before them. In the app the main view model holds its sixteen.
    /// No wall clock here: a weak reference says whether a view model
    /// survived a full collection.
    /// </summary>
    [Collection("CultureSwitching")]
    public class PadViewModelLifetimeTests
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference<PadViewModel> MakeUnheld(int padIndex) => new(new PadViewModel(padIndex));

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static (WeakReference<MainViewModel> Main, WeakReference<PadViewModel> Pad) MakeUnheldMain()
        {
            var main = new MainViewModel();
            return (new WeakReference<MainViewModel>(main), new WeakReference<PadViewModel>(main.Pads[3]));
        }

        private static void Collect()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        /// <summary>A pad view model nothing holds is collected, so a culture
        /// change has nothing left to rebuild. One the test holds is still
        /// rebuilt: its Map All text is raised again in the new language.</summary>
        [Fact]
        public void APadViewModelNothingHoldsIsCollected_AndAHeldOneIsStillRebuilt()
        {
            var held = new PadViewModel(2);
            var unheld = MakeUnheld(5);
            Collect();
            Assert.False(unheld.TryGetTarget(out _), "a pad view model nothing holds was kept alive");

            int rebuilt = 0;
            held.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PadViewModel.MapAllButtonText)) rebuilt++;
            };
            var before = CultureInfo.CurrentUICulture;
            try
            {
                Strings.ChangeCulture(CultureInfo.GetCultureInfo("de"));
                Assert.Equal(1, rebuilt);
            }
            finally
            {
                Strings.ChangeCulture(before);
            }
            GC.KeepAlive(held);
        }

        /// <summary>A main view model nothing holds goes with its sixteen pads.</summary>
        [Fact]
        public void AMainViewModelNothingHoldsIsCollectedWithItsPads()
        {
            var (main, pad) = MakeUnheldMain();
            Collect();
            Assert.False(pad.TryGetTarget(out _), "a pad of a main view model nothing holds was kept alive");
            Assert.False(main.TryGetTarget(out _), "a main view model nothing holds was kept alive");
        }

        /// <summary>The weak list itself: an instance handler fires while its
        /// subscriber lives and is gone once the subscriber is collected, an
        /// unsubscribe takes effect at once, and a static handler is kept.</summary>
        [Fact]
        public void TheWeakHandlersKeepNoSubscriberAlive()
        {
            var handlers = new PadForge.Common.WeakHandlers<Action<int>>();
            s_staticHits = 0;
            handlers.Add(StaticHandler);
            var kept = new Counter();
            handlers.Add(kept.Hit);
            var removed = new Counter();
            handlers.Add(removed.Hit);
            handlers.Remove(removed.Hit);
            var dropped = AddUnheld(handlers);

            Collect();
            Assert.False(dropped.TryGetTarget(out _), "a subscriber nothing holds was kept alive");
            var live = handlers.Live();
            Assert.Equal(2, live.Count);
            foreach (var h in live) h(1);
            Assert.Equal(1, kept.Hits);
            Assert.Equal(0, removed.Hits);
            Assert.Equal(1, s_staticHits);
            GC.KeepAlive(kept);
            GC.KeepAlive(removed);
        }

        private static int s_staticHits;
        private static void StaticHandler(int _) => s_staticHits++;

        private sealed class Counter
        {
            public int Hits;
            public void Hit(int _) => Hits++;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference<Counter> AddUnheld(PadForge.Common.WeakHandlers<Action<int>> handlers)
        {
            var counter = new Counter();
            handlers.Add(counter.Hit);
            return new WeakReference<Counter>(counter);
        }
    }
}

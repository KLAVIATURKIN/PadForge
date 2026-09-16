using System;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Two findings from the 2026-09-15 audit that share a theme: a list or a
    /// gate that kept an answer after the thing it described had changed.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AudioPickerAndFeedbackGateTests
    {
        // C248: the retained option kept its old caption.

        /// <summary>The refresh keeps the option INSTANCE on purpose, because
        /// rebuilding the list drops the picker's selected item and the
        /// selection goes with it. It kept the instance's old caption too, so
        /// an endpoint that came back still read as unavailable, a renamed one
        /// kept its old name, and the default row kept the previous culture's
        /// word for it.</summary>
        [Fact]
        public void ARetainedOptionTakesTheFreshCaption()
        {
            var option = new PadViewModel.MirrorSourceOption { Id = "abc", Name = "Speakers (unavailable)" };

            string heard = null;
            option.PropertyChanged += (_, e) => heard = e.PropertyName;
            option.Name = "Speakers";

            Assert.Equal("Speakers", option.Name);
            Assert.Equal(nameof(PadViewModel.MirrorSourceOption.Name), heard);
        }

        /// <summary>The caption has to reach the binding through the object,
        /// because the collection never changes when the instance is kept. An
        /// option with no change notification could not repaint.</summary>
        [Fact]
        public void TheOptionRaisesItsOwnChanges()
        {
            Assert.True(
                typeof(System.ComponentModel.INotifyPropertyChanged)
                    .IsAssignableFrom(typeof(PadViewModel.MirrorSourceOption)),
                "the picker option cannot tell the binding its caption changed");
        }

        // C250: a feedback source the descriptor gate could not see.

        /// <summary>The Deck persona decodes its vendor feature writes into
        /// the slot's feedback pack, so it has a real source with no PID block
        /// on the wire for a descriptor test to find. Hiding the Bass Shakers
        /// tab for it hid a feature that works.</summary>
        [Fact]
        public void TheDeckPersonaReportsADecodedFeedbackSource()
        {
            Assert.True(HMaestroVirtualController.DecodesFeedbackWithoutPidBlock("steam-deck-composite"));
            // Case is not the user's problem.
            Assert.True(HMaestroVirtualController.DecodesFeedbackWithoutPidBlock("Steam-Deck-Composite"));
        }

        /// <summary>The other Valve profiles have no such decode, so they stay
        /// on the descriptor test. Claiming a source they do not have would be
        /// the opposite error.</summary>
        [Theory]
        [InlineData("steam-controller")]
        [InlineData("steam-controller-composite")]
        [InlineData("steam-controller-2")]
        [InlineData("switch-pro")]
        [InlineData(null)]
        [InlineData("")]
        public void ProfilesWithoutADecodeAreNotClaimed(string profileId)
        {
            Assert.False(HMaestroVirtualController.DecodesFeedbackWithoutPidBlock(profileId));
        }

        /// <summary>The gate and the decode ask the same question through the
        /// same predicate, so the gate cannot fall behind the decode.</summary>
        [Fact]
        public void TheProfileIdIsSpelledOnce()
        {
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(d.FullName,
                "PadForge.App", "Common", "Input", "HMaestroVirtualController.cs"));

            int spellings = System.Text.RegularExpressions.Regex.Matches(src, "steam-deck-composite").Count;
            Assert.Equal(1, spellings);
        }
    }
}

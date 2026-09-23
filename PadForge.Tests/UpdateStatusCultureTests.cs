using System.Collections.Generic;
using System.Globalization;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Updates card's status line (#457) is built from Strings.Instance
    /// each time it is read, so a language change reaches it. A stored,
    /// already formatted string kept the old language until the next check,
    /// up to twelve hours later.
    /// </summary>
    [Collection("CultureSwitching")]
    public class UpdateStatusCultureTests
    {
        [Fact]
        public void TheStatusLineFollowsALanguageChange()
        {
            var before = CultureInfo.CurrentUICulture;
            try
            {
                Strings.ChangeCulture(CultureInfo.GetCultureInfo("en"));
                var vm = new SettingsViewModel();
                vm.SetUpdateStatus(() => Strings.Instance.Update_Checking);
                string english = vm.UpdateStatusText;
                var raised = new List<string>();
                vm.PropertyChanged += (s, e) => raised.Add(e.PropertyName);

                Strings.ChangeCulture(CultureInfo.GetCultureInfo("de"));

                Assert.NotEqual(english, vm.UpdateStatusText);
                Assert.Equal(Strings.Instance.Update_Checking, vm.UpdateStatusText);
                Assert.Contains(nameof(SettingsViewModel.UpdateStatusText), raised);

                vm.SetUpdateStatus(null);
                Assert.Equal(string.Empty, vm.UpdateStatusText);
            }
            finally
            {
                Strings.ChangeCulture(before);
            }
        }

        /// <summary>A failure PadForge words itself is worded when the line is
        /// read, so the reason changes language with the sentence around it. A
        /// reason from Windows or the network stays as it came.</summary>
        [Fact]
        public void AFailureReasonPadForgeWordsFollowsALanguageChange()
        {
            var before = CultureInfo.CurrentUICulture;
            try
            {
                Strings.ChangeCulture(CultureInfo.GetCultureInfo("en"));
                var stalled = UpdateController.FailureText(new UpdateDownloadStalledException());
                var helper = UpdateController.FailureText(new UpdateHelperException(HelperFailure.Exited, "3"));
                var windows = UpdateController.FailureText(new System.IO.IOException("Access is denied."));
                string stalledEnglish = stalled(), helperEnglish = helper();
                Assert.Contains(Strings.Instance.Update_DownloadStalled, stalledEnglish);

                Strings.ChangeCulture(CultureInfo.GetCultureInfo("de"));

                Assert.NotEqual(stalledEnglish, stalled());
                Assert.Contains(Strings.Instance.Update_DownloadStalled, stalled());
                Assert.NotEqual(helperEnglish, helper());
                Assert.Contains(string.Format(Strings.Instance.Update_HelperExited_Format, "3"), helper());
                Assert.Contains("Access is denied.", windows());
            }
            finally
            {
                Strings.ChangeCulture(before);
            }
        }
    }
}

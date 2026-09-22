using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Services
{
    /// <summary>
    /// Runs the Updates card in Settings (#457): the automatic checks, Check
    /// Now, the background download for Install Updates Automatically, and
    /// Install and Restart. UI thread only. The network and file work lives
    /// in <see cref="UpdateService"/>.
    ///
    /// <para>The automatic check runs 20 seconds after launch, so it never
    /// competes with startup, and every 12 hours after that. The check
    /// interval and the startup delay follow HandheldCompanion's
    /// UpdateManager (a check at start plus a timer) at a slower cadence,
    /// since GitHub allows 60 unauthenticated API calls an hour per
    /// address.</para>
    /// </summary>
    internal sealed class UpdateController : IDisposable
    {
        private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);

        private readonly MainViewModel _vm;
        private readonly Action _exitForUpdate;
        private readonly CancellationTokenSource _cts = new();
        private DispatcherTimer _timer;
        private bool _started;
        private bool _busy;
        private UpdateOffer _offer;
        /// <summary>The downloaded exe for <see cref="_offer"/>, verified
        /// this session. Install and Restart reuses it.</summary>
        private StagedUpdate _staged;

        public UpdateController(MainViewModel vm, Action exitForUpdate)
        {
            _vm = vm ?? throw new ArgumentNullException(nameof(vm));
            _exitForUpdate = exitForUpdate ?? throw new ArgumentNullException(nameof(exitForUpdate));
        }

        private SettingsViewModel Settings => _vm.Settings;

        public void Start()
        {
            if (_started) return;
            _started = true;
            Settings.CheckForUpdatesNowRequested += (s, e) => _ = CheckAsync(userInitiated: true);
            Settings.InstallUpdateRequested += (s, e) => _ = InstallAsync();
            Settings.OpenReleaseNotesRequested += (s, e) => OpenReleaseNotes();
            Settings.PropertyChanged += OnSettingsChanged;

            _ = UpdateService.CleanupStagingAsync();

            if (App.StartupArgs != null
                && Array.Exists(App.StartupArgs, a => string.Equals(a, UpdateService.UpdatedSwitch, StringComparison.OrdinalIgnoreCase)))
                _vm.SetStatus(string.Format(Strings.Instance.Update_Updated_Format, BuildIdentity.Display));
            else if (UpdateService.LastPendingFailure != null)
                _vm.SetStatus(string.Format(Strings.Instance.Update_PendingFailed_Format,
                    UpdateService.LastPendingFailure), persist: true);

            _timer = new DispatcherTimer { Interval = FirstCheckDelay };
            _timer.Tick += (s, e) =>
            {
                _timer.Interval = CheckInterval;
                if (Settings.CheckForUpdatesAutomatically)
                    _ = CheckAsync(userInitiated: false);
            };
            _timer.Start();
        }

        private void OnSettingsChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(SettingsViewModel.IncludePreReleaseUpdates):
                    // An offer or a staged build from the other channel no
                    // longer answers the question the card asks.
                    ClearOffer();
                    UpdateService.DeletePending();
                    Settings.UpdateStatusText = string.Empty;
                    if (Settings.CheckForUpdatesAutomatically)
                        _ = CheckAsync(userInitiated: false);
                    break;
                case nameof(SettingsViewModel.InstallUpdatesAutomatically):
                    if (!Settings.InstallUpdatesAutomatically)
                        UpdateService.DeletePending();
                    else if (_offer != null)
                        _ = StageInBackgroundAsync(_offer);
                    break;
                case nameof(SettingsViewModel.CheckForUpdatesAutomatically):
                    if (!Settings.CheckForUpdatesAutomatically)
                        UpdateService.DeletePending();
                    else
                        _ = CheckAsync(userInitiated: false);
                    break;
            }
        }

        private void ClearOffer()
        {
            _offer = null;
            _staged = null;
            Settings.IsUpdateAvailable = false;
        }

        public async Task CheckAsync(bool userInitiated)
        {
            if (_busy) return;
            SetBusy(true);
            if (userInitiated)
                Settings.UpdateStatusText = Strings.Instance.Update_Checking;
            bool preReleases = Settings.IncludePreReleaseUpdates;
            UpdateCheckOutcome outcome;
            UpdateOffer offer;
            string error;
            try
            {
                (outcome, offer, error) = await UpdateService.CheckAsync(preReleases, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                SetBusy(false);
            }

            // Include Pre-Releases changed while this check was out, so its
            // answer is for the other channel. Ask again for this one.
            if (preReleases != Settings.IncludePreReleaseUpdates)
            {
                await CheckAsync(userInitiated);
                return;
            }

            var s = Strings.Instance;
            switch (outcome)
            {
                case UpdateCheckOutcome.Available:
                    bool isNew = _offer == null || _offer.StageKey != offer.StageKey;
                    if (isNew)
                    {
                        _offer = offer;
                        _staged = null;
                    }
                    Settings.IsUpdateAvailable = true;
                    Settings.UpdateStatusText = AvailableText(offer);
                    if (!userInitiated && isNew)
                        _vm.SetStatus(string.Format(s.Update_AvailableStatusBar_Format, offer.DisplayVersion), persist: true);
                    if (Settings.InstallUpdatesAutomatically && Settings.CheckForUpdatesAutomatically)
                        await StageInBackgroundAsync(_offer);
                    break;
                case UpdateCheckOutcome.UpToDate:
                    ClearOffer();
                    Settings.UpdateStatusText = string.Format(s.Update_UpToDate_Format, BuildIdentity.Display);
                    break;
                case UpdateCheckOutcome.NoBuildForThisPc:
                    ClearOffer();
                    Settings.UpdateStatusText = s.Update_NoBuildForThisPc;
                    break;
                case UpdateCheckOutcome.RateLimited:
                    Settings.UpdateStatusText = s.Update_RateLimited;
                    break;
                default:
                    Settings.UpdateStatusText = string.Format(s.Update_CheckFailed_Format, error ?? string.Empty);
                    break;
            }
        }

        private static string AvailableText(UpdateOffer offer) =>
            string.Format(offer.IsPreRelease
                    ? Strings.Instance.Update_PreReleaseAvailable_Format
                    : Strings.Instance.Update_Available_Format,
                offer.DisplayVersion);

        /// <summary>Install Updates Automatically: fetch and verify now,
        /// install at the next launch (App.OnStartup).</summary>
        private async Task StageInBackgroundAsync(UpdateOffer offer)
        {
            if (_busy || offer == null) return;
            var pending = UpdateService.ReadPending();
            if (pending != null && pending.DisplayVersion == offer.DisplayVersion && File.Exists(pending.ExePath))
            {
                Settings.UpdateStatusText = string.Format(Strings.Instance.Update_ReadyNextStart_Format, offer.DisplayVersion);
                return;
            }
            var staged = await DownloadAsync(offer);
            if (staged == null || !ReferenceEquals(offer, _offer)) return;
            try
            {
                UpdateService.WritePending(offer, staged);
                Settings.UpdateStatusText = string.Format(Strings.Instance.Update_ReadyNextStart_Format, offer.DisplayVersion);
            }
            catch (Exception ex)
            {
                Settings.UpdateStatusText = string.Format(Strings.Instance.Update_InstallFailed_Format, ex.Message);
            }
        }

        /// <summary>Downloads with progress on the card. Null when it failed,
        /// in which case the card already says why.</summary>
        private async Task<StagedUpdate> DownloadAsync(UpdateOffer offer)
        {
            if (_staged != null && File.Exists(_staged.ExePath) && ReferenceEquals(offer, _offer))
                return _staged;
            SetBusy(true);
            Settings.UpdateProgress = 0;
            Settings.IsUpdateDownloading = true;
            Settings.UpdateStatusText = string.Format(Strings.Instance.Update_Downloading_Format, 0);
            try
            {
                var progress = new Progress<int>(p =>
                {
                    Settings.UpdateProgress = p;
                    Settings.UpdateStatusText = string.Format(Strings.Instance.Update_Downloading_Format, p);
                });
                var staged = await UpdateService.DownloadAsync(offer, progress, _cts.Token);
                if (ReferenceEquals(offer, _offer)) _staged = staged;
                return staged;
            }
            catch (UpdateVerificationException)
            {
                Settings.UpdateStatusText = Strings.Instance.Update_VerifyFailed;
                return null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                Settings.UpdateStatusText = string.Format(Strings.Instance.Update_InstallFailed_Format, ex.Message);
                return null;
            }
            finally
            {
                Settings.IsUpdateDownloading = false;
                SetBusy(false);
            }
        }

        public async Task InstallAsync()
        {
            var offer = _offer;
            if (offer == null || _busy) return;
            var staged = await DownloadAsync(offer);
            if (staged == null) return;
            try
            {
                // A manual install replaces whatever the automatic path staged.
                UpdateService.DeletePending();
                UpdateService.StartInstaller(staged, App.StartupArgs);
            }
            catch (UpdateVerificationException)
            {
                // Changed on disk since it was written. Drop it, so the next
                // Install and Restart downloads it again.
                UpdateService.DiscardStaged(staged);
                _staged = null;
                Settings.UpdateStatusText = Strings.Instance.Update_VerifyFailed;
                return;
            }
            catch (Exception ex)
            {
                Settings.UpdateStatusText = string.Format(Strings.Instance.Update_InstallFailed_Format, ex.Message);
                return;
            }
            _exitForUpdate();
        }

        private void OpenReleaseNotes()
        {
            string url = _offer?.ReleasePageUrl;
            if (string.IsNullOrEmpty(url) || !url.StartsWith("https://github.com/" + UpdateService.Repo + "/", StringComparison.OrdinalIgnoreCase))
                return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* no browser registered */ }
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            Settings.IsUpdateBusy = busy;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _timer?.Stop();
            Settings.PropertyChanged -= OnSettingsChanged;
        }
    }
}

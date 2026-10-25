using System;
using System.Globalization;
using Microsoft.Win32;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Common.Input.KeyboardScreens;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.Views;

namespace PadForge
{
    public partial class MainWindow
    {
        /// <summary>The Keyboard Screens card's buttons (issue #519): a kind's
        /// dialog, Mad Catz's SDK fetched at the user's click, a
        /// Strike7API.dll the user has, and the Visual C++ 2008 runtime that
        /// library links.</summary>
        private void WireKeyboardScreens()
        {
            var settings = _viewModel.Settings;
            settings.ConfigureKeyboardScreenRequested += (_, kindName) =>
            {
                if (Enum.TryParse(kindName, out KeyboardScreenKind kind)) ConfigureKeyboardScreen(kind);
            };

            settings.DownloadStrike7LibraryRequested += async (_, _) =>
            {
                await RunDriverOperationAsync(
                    Strings.Instance.Status_DownloadingStrike7,
                    () => DriverInstaller.InstallStrike7Library(
                        KeyboardScreenService.DownloadedStrike7Library, KeyboardScreenService.Strike7LibrarySha256),
                    () => { });
                // The download is the library from now on, in place of a copy
                // picked before.
                if (System.IO.File.Exists(KeyboardScreenService.DownloadedStrike7Library)
                    && !string.IsNullOrEmpty(settings.Strike7LibraryPath))
                {
                    settings.Strike7LibraryPath = null;
                    _settingsService.MarkDirty();
                }
            };

            settings.PickStrike7LibraryRequested += (_, _) =>
            {
                var dialog = new OpenFileDialog { Filter = Strings.Instance.KeyboardScreens_Strike7LibraryFilter };
                if (dialog.ShowDialog(this) != true) return;
                settings.Strike7LibraryPath = IconPackageManager.MakeStoredPath(dialog.FileName);
                _settingsService.MarkDirty();
                _viewModel.SetStatus(string.Format(CultureInfo.CurrentCulture,
                    Strings.Instance.Status_Strike7LibraryPicked_Format, System.IO.Path.GetFileName(dialog.FileName)));
            };

            settings.InstallVc2008RuntimeRequested += async (_, _) =>
            {
                await RunDriverOperationAsync(
                    Strings.Instance.Status_InstallingVc2008,
                    DriverInstaller.InstallVc2008Runtime,
                    () => { });
            };
        }

        /// <summary>Opens one kind's dialog and keeps what was saved: the
        /// kind's mode, picture and pan, and the active profile's picture.</summary>
        private void ConfigureKeyboardScreen(KeyboardScreenKind kind)
        {
            var service = _inputService?.KeyboardScreens;
            if (service == null) return;
            string profileId = SettingsManager.ActiveProfileId;
            var profile = string.IsNullOrEmpty(profileId) ? null : SettingsManager.Profiles.Find(p => p.Id == profileId);
            string profilePicture = profile != null ? profile.KeyboardScreenPicture : _viewModel.Settings.DefaultProfileKeyboardScreenPicture;
            var choice = KeyboardScreenDialog.Show(this, service, kind, service.Get(kind),
                DreamcastScreenService.ActiveProfileName(), profilePicture, service.StatusLine(kind));
            if (choice == null) return;
            if (profile != null) profile.KeyboardScreenPicture = choice.ProfilePicture;
            else _viewModel.Settings.DefaultProfileKeyboardScreenPicture = choice.ProfilePicture;
            service.Update(kind, d =>
            {
                d.Mode = choice.Mode;
                d.Picture = choice.Picture;
                d.PanX = choice.PanX;
                d.PanY = choice.PanY;
            });
            _viewModel.SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.Instance.Status_KeyboardScreen_Format,
                KeyboardScreenService.KindName(kind), KeyboardScreenService.ModeName(choice.Mode)));
        }
    }
}

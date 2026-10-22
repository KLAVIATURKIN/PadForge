using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using HIDMaestro;
using PadForge.Resources.Strings;

namespace PadForge.Views
{
    /// <summary>
    /// The removal of HIDMaestro from the PC (#518), confirmed against
    /// HIDMaestro's own preview: each item a removal would take, each one it
    /// would keep and why, and the programs that would block it. The preview
    /// reads the PC off the UI thread and changes nothing. Uninstall stays
    /// off until a preview finds something to take and no program blocks
    /// it. The same window, with Close alone, lists what a removal could not
    /// take (<see cref="ShowReport"/>).
    /// </summary>
    public partial class UninstallHIDMaestroDialog : Wpf.Ui.Controls.FluentWindow
    {
        /// <summary>One item of a report, as the list shows it.</summary>
        public sealed class EntryRow
        {
            public string Kind { get; init; }
            public string Location { get; init; }

            /// <summary>Why the item stays, could not be read, or was not
            /// removed. Null for an item the removal takes.</summary>
            public string Detail { get; init; }
        }

        private readonly Func<bool, Task<HMUninstallReport>> _preview;
        private int _generation;
        private bool _confirmed;

        private UninstallHIDMaestroDialog(Func<bool, Task<HMUninstallReport>> preview)
        {
            _preview = preview;
            InitializeComponent();
            // FluentWindow sets ExtendsContentIntoTitleBar, which zeroes
            // WindowChrome.CaptionHeight, and this dialog declares no
            // <ui:TitleBar>, so the window is dragged by its inert chrome, as
            // ConfirmDialog is.
            MouseLeftButtonDown += (_, __) => { try { DragMove(); } catch { } };
            Title = Strings.Instance.HIDMaestroUninstall_Title;
            Loaded += (_, _) => CancelButton.Focus();
        }

        /// <summary>Shows the preview and returns true only when the user
        /// clicked Uninstall, with the usbip-win2 choice the last preview
        /// was read with.</summary>
        public static bool Confirm(Window owner, Func<bool, Task<HMUninstallReport>> preview, out bool includeUsbip)
        {
            var dlg = new UninstallHIDMaestroDialog(preview);
            Place(dlg, owner);
            dlg.Loaded += (_, _) => dlg.RunPreview();
            dlg.ShowDialog();
            includeUsbip = dlg.IncludeUsbipBox.IsChecked == true;
            return dlg._confirmed;
        }

        /// <summary>Lists what a removal could not take, under its status
        /// line, with Close alone.</summary>
        public static void ShowReport(Window owner, HMUninstallReport report, string statusLine)
        {
            var dlg = new UninstallHIDMaestroDialog(null);
            Place(dlg, owner);
            dlg.MessageText.Text = statusLine;
            dlg.UsbipPanel.Visibility = Visibility.Collapsed;
            dlg.BusyPanel.Visibility = Visibility.Collapsed;
            dlg.RefreshButton.Visibility = Visibility.Collapsed;
            dlg.ActionButton.Visibility = Visibility.Collapsed;
            dlg.CancelButton.Content = Strings.Instance.Common_Close;
            dlg.ShowEntries(RowsFor(report));
            dlg.ShowDialog();
        }

        private static void Place(Window dlg, Window owner)
        {
            if (owner != null) dlg.Owner = owner;
            else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        /// <summary>The rows for a report. A preview lists every item, with
        /// the reason beside each one that stays or could not be read. A
        /// removal lists only the items it could not take.</summary>
        internal static IReadOnlyList<EntryRow> RowsFor(HMUninstallReport report)
        {
            var rows = new List<EntryRow>();
            foreach (var e in report.Entries)
            {
                if (!report.IsPreview && e.Outcome != HMUninstallOutcome.Failed) continue;
                string format = e.Outcome switch
                {
                    HMUninstallOutcome.Kept => Strings.Instance.HIDMaestroUninstall_Stays_Format,
                    HMUninstallOutcome.Failed => report.IsPreview
                        ? Strings.Instance.HIDMaestroUninstall_CannotRead_Format
                        : Strings.Instance.HIDMaestroUninstall_NotRemoved_Format,
                    _ => null,
                };
                rows.Add(new EntryRow
                {
                    Kind = e.Kind,
                    Location = e.Location,
                    Detail = format != null && !string.IsNullOrEmpty(e.Reason) ? string.Format(format, e.Reason) : null,
                });
            }
            return rows;
        }

        /// <summary>True when a removal after this preview has something to
        /// do: no program blocks it, and it found an item to take or a part
        /// of the PC it could not read, which the removal reads again
        /// (HMUninstall docs/UNINSTALL.md).</summary>
        internal static bool CanRemove(HMUninstallReport report) =>
            report != null
            && report.Status != HMUninstallStatus.Blocked
            && report.Entries.Any(e => e.Outcome is HMUninstallOutcome.WouldRemove or HMUninstallOutcome.Failed);

        private async void RunPreview()
        {
            int generation = ++_generation;
            ActionButton.IsEnabled = false;
            RefreshButton.IsEnabled = false;
            IncludeUsbipBox.IsEnabled = false;
            BusyPanel.Visibility = Visibility.Visible;
            NoteText.Visibility = Visibility.Collapsed;
            ProgramList.Visibility = Visibility.Collapsed;
            EntryList.Visibility = Visibility.Collapsed;

            HMUninstallReport report = null;
            string failure = null;
            try { report = await _preview(IncludeUsbipBox.IsChecked == true); }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine($"HIDMAESTRO removal preview failed: {ex.GetType().Name}: {ex.Message}");
                failure = ex.Message;
            }
            // A preview that ends after the window closed, or after a newer
            // one started, has nothing to show.
            if (generation != _generation || !IsLoaded) return;

            BusyPanel.Visibility = Visibility.Collapsed;
            RefreshButton.IsEnabled = true;
            IncludeUsbipBox.IsEnabled = true;
            if (report == null)
            {
                ShowNote(string.Format(Strings.Instance.Status_DriverOperationFailed_Format, failure ?? string.Empty), caution: true);
                return;
            }

            Services.HIDMaestroRemoval.Log(report);
            if (report.Status == HMUninstallStatus.Blocked)
            {
                ShowNote(Strings.Instance.HIDMaestroUninstall_Blocked, caution: true);
                ProgramList.ItemsSource = report.RunningPrograms
                    .Select(p => Services.HIDMaestroRemoval.ProgramName(p, fullPath: true)).ToList();
                ProgramList.Visibility = Visibility.Visible;
            }
            else if (!CanRemove(report))
            {
                ShowNote(Strings.Instance.HIDMaestroUninstall_Nothing, caution: false);
            }
            ShowEntries(RowsFor(report));
            ActionButton.IsEnabled = CanRemove(report);
        }

        private void ShowNote(string text, bool caution)
        {
            NoteText.Text = text;
            NoteText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                caution ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush");
            NoteText.Visibility = Visibility.Visible;
        }

        private void ShowEntries(IReadOnlyList<EntryRow> rows)
        {
            EntryList.ItemsSource = rows;
            EntryList.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void IncludeUsbipBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_preview != null && IsLoaded) RunPreview();
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e) => RunPreview();

        private void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            _confirmed = true;
            DialogResult = true;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The screen-reader guard over PadForge.App's XAML. A static audit finds
    /// each control a screen reader would announce without a usable name,
    /// sorts it by the fix it needs, and holds every file and class at a
    /// ceiling. A new offender fails the suite, and each phase of the
    /// accessibility work lowers the ceilings it clears.
    ///
    /// <para>WPF names a control from AutomationProperties.Name, then
    /// LabeledBy, then the element's plain text
    /// (FrameworkElementAutomationPeer.GetNameCore), so a button holding a
    /// Segoe MDL2 glyph is named with the glyph's code and a tooltip is only
    /// help text (GetHelpTextCore). A TextBox or NumberBox takes a name only
    /// from Name or LabeledBy, never from its placeholder. An item is named by
    /// its container's peer and then by the item's ToString(), and
    /// DisplayMemberPath does not change that (ItemAutomationPeer.GetNameCore,
    /// ContentControl.PrepareContentControl).</para>
    ///
    /// <para>UI built in C# (the sidebar controller cards, the Add Controller
    /// popup) is invisible here. tools/a11y_uia_audit.ps1 covers it at
    /// runtime.</para>
    ///
    /// <para>Set PADFORGE_A11Y_AUDIT_OUT to a file path to write every finding
    /// as JSON, with the measured ceiling table, for the next phase's work
    /// list.</para>
    /// </summary>
    public class AccessibilityXamlTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        // The counts this test measured when it landed (HEAD 475cc2c4,
        // 2026-10-22). Counts, not lines, since lines drift. Each phase lowers
        // the ones it clears, and a file or class missing here holds at zero.
        private static readonly Dictionary<(string File, string Class), int> Ceilings = new()
        {
            [("PadForge.App/Controls/CurveEditor.xaml", XamlA11yAudit.MouseOnly)] = 2,
            [("PadForge.App/MainWindow.xaml", XamlA11yAudit.BtnNoTooltip)] = 2,
            [("PadForge.App/MainWindow.xaml", XamlA11yAudit.ImageNoName)] = 1,
            [("PadForge.App/MainWindow.xaml", XamlA11yAudit.MouseOnly)] = 1,
            [("PadForge.App/Views/AboutPage.xaml", XamlA11yAudit.ImageNoName)] = 1,
            [("PadForge.App/Views/BlissBoxPlayerDialog.xaml", XamlA11yAudit.InSiblingLabel)] = 1,
            [("PadForge.App/Views/ColorPickerControl.xaml", XamlA11yAudit.MouseOnly)] = 4,
            [("PadForge.App/Views/ConfirmDialog.xaml", XamlA11yAudit.BtnNoTooltip)] = 1,
            [("PadForge.App/Views/ControllerModel2DView.xaml", XamlA11yAudit.BtnTooltipKey)] = 1,
            [("PadForge.App/Views/ControllerModel2DView.xaml", XamlA11yAudit.InSiblingLabel)] = 1,
            [("PadForge.App/Views/ControllerModelView.xaml", XamlA11yAudit.BtnTooltipKey)] = 1,
            [("PadForge.App/Views/ControllerModelView.xaml", XamlA11yAudit.InSiblingLabel)] = 1,
            [("PadForge.App/Views/Controls/ProfilePill.xaml", XamlA11yAudit.MouseOnly)] = 1,
            [("PadForge.App/Views/CopyFromDialog.xaml", XamlA11yAudit.BtnTooltipKey)] = 1,
            [("PadForge.App/Views/DashboardPage.xaml", XamlA11yAudit.BtnStyleTooltip)] = 9,
            [("PadForge.App/Views/DashboardPage.xaml", XamlA11yAudit.BtnTooltipKey)] = 3,
            [("PadForge.App/Views/DashboardPage.xaml", XamlA11yAudit.ImageNoName)] = 2,
            [("PadForge.App/Views/DashboardPage.xaml", XamlA11yAudit.InNothing)] = 4,
            [("PadForge.App/Views/DashboardPage.xaml", XamlA11yAudit.InPlaceholder)] = 2,
            [("PadForge.App/Views/DashboardPage.xaml", XamlA11yAudit.InSiblingLabel)] = 17,
            [("PadForge.App/Views/DashboardPage.xaml", XamlA11yAudit.ItemsDisplayMemberPath)] = 1,
            [("PadForge.App/Views/DashboardPage.xaml", XamlA11yAudit.ItemsUnnamed)] = 7,
            [("PadForge.App/Views/DashboardPage.xaml", XamlA11yAudit.MouseOnly)] = 1,
            [("PadForge.App/Views/DevicesPage.xaml", XamlA11yAudit.BtnTooltipKey)] = 4,
            [("PadForge.App/Views/DevicesPage.xaml", XamlA11yAudit.InSiblingLabel)] = 1,
            [("PadForge.App/Views/DevicesPage.xaml", XamlA11yAudit.ItemsUnnamed)] = 13,
            [("PadForge.App/Views/DevicesPage.xaml", XamlA11yAudit.MouseOnly)] = 10,
            [("PadForge.App/Views/DreamcastScreenDialog.xaml", XamlA11yAudit.BtnNoTooltip)] = 1,
            [("PadForge.App/Views/DreamcastScreenDialog.xaml", XamlA11yAudit.ImageNoName)] = 1,
            [("PadForge.App/Views/DreamcastScreenDialog.xaml", XamlA11yAudit.InSiblingLabel)] = 1,
            [("PadForge.App/Views/IconPicker.xaml", XamlA11yAudit.BtnNoTooltip)] = 1,
            [("PadForge.App/Views/IconPicker.xaml", XamlA11yAudit.ImageNoName)] = 1,
            [("PadForge.App/Views/IconPicker.xaml", XamlA11yAudit.ItemsUnnamed)] = 1,
            [("PadForge.App/Views/LearnHandheldButtonDialog.xaml", XamlA11yAudit.BtnTooltipKey)] = 1,
            [("PadForge.App/Views/LearnHandheldButtonDialog.xaml", XamlA11yAudit.InSiblingLabel)] = 2,
            [("PadForge.App/Views/ManageProfilesDialog.xaml", XamlA11yAudit.BtnTooltipKey)] = 1,
            [("PadForge.App/Views/PadPage.xaml", XamlA11yAudit.BtnTooltipKey)] = 301,
            [("PadForge.App/Views/PadPage.xaml", XamlA11yAudit.BtnTooltipOther)] = 13,
            [("PadForge.App/Views/PadPage.xaml", XamlA11yAudit.ImageNoName)] = 3,
            [("PadForge.App/Views/PadPage.xaml", XamlA11yAudit.InNothing)] = 96,
            [("PadForge.App/Views/PadPage.xaml", XamlA11yAudit.InSiblingLabel)] = 466,
            [("PadForge.App/Views/PadPage.xaml", XamlA11yAudit.InTooltipOnly)] = 56,
            [("PadForge.App/Views/PadPage.xaml", XamlA11yAudit.ItemsDisplayMemberPath)] = 56,
            [("PadForge.App/Views/PadPage.xaml", XamlA11yAudit.ItemsUnnamed)] = 42,
            [("PadForge.App/Views/PadPage.xaml", XamlA11yAudit.MouseOnly)] = 18,
            [("PadForge.App/Views/PadPage.xaml", XamlA11yAudit.ToggleNoText)] = 2,
            [("PadForge.App/Views/PairDeviceDialog.xaml", XamlA11yAudit.BtnTooltipKey)] = 3,
            [("PadForge.App/Views/PairDeviceDialog.xaml", XamlA11yAudit.InNothing)] = 4,
            [("PadForge.App/Views/PickSoundDialog.xaml", XamlA11yAudit.BtnTooltipKey)] = 1,
            [("PadForge.App/Views/ProfileDialog.xaml", XamlA11yAudit.BtnTooltipKey)] = 1,
            [("PadForge.App/Views/ProfileDialog.xaml", XamlA11yAudit.InNothing)] = 1,
            [("PadForge.App/Views/ProfileDialog.xaml", XamlA11yAudit.InSiblingLabel)] = 1,
            [("PadForge.App/Views/ProfilesPage.xaml", XamlA11yAudit.BtnTooltipOther)] = 2,
            [("PadForge.App/Views/ProfilesPage.xaml", XamlA11yAudit.ImageNoName)] = 1,
            [("PadForge.App/Views/ProfilesPage.xaml", XamlA11yAudit.InNothing)] = 3,
            [("PadForge.App/Views/ProfilesPage.xaml", XamlA11yAudit.ItemsDisplayMemberPath)] = 2,
            [("PadForge.App/Views/ProfilesPage.xaml", XamlA11yAudit.ItemsUnnamed)] = 3,
            [("PadForge.App/Views/RegisterNfcTagDialog.xaml", XamlA11yAudit.BtnTooltipKey)] = 1,
            [("PadForge.App/Views/RegisterNfcTagDialog.xaml", XamlA11yAudit.InSiblingLabel)] = 1,
            [("PadForge.App/Views/RegisterVoicePhraseDialog.xaml", XamlA11yAudit.BtnTooltipKey)] = 1,
            [("PadForge.App/Views/RegisterVoicePhraseDialog.xaml", XamlA11yAudit.InSiblingLabel)] = 4,
            [("PadForge.App/Views/RemoteAssignmentsDialog.xaml", XamlA11yAudit.BtnTooltipKey)] = 1,
            [("PadForge.App/Views/RemoteAssignmentsDialog.xaml", XamlA11yAudit.InNothing)] = 1,
            [("PadForge.App/Views/RemoteLinkPairDialog.xaml", XamlA11yAudit.BtnTooltipKey)] = 1,
            [("PadForge.App/Views/RemoteLinkPasswordDialog.xaml", XamlA11yAudit.BtnTooltipKey)] = 1,
            [("PadForge.App/Views/RemoteLinkPasswordDialog.xaml", XamlA11yAudit.InNothing)] = 2,
            [("PadForge.App/Views/SettingsPage.xaml", XamlA11yAudit.InNothing)] = 2,
            [("PadForge.App/Views/SettingsPage.xaml", XamlA11yAudit.InSiblingLabel)] = 2,
            [("PadForge.App/Views/SettingsPage.xaml", XamlA11yAudit.InTooltipOnly)] = 2,
            [("PadForge.App/Views/SettingsPage.xaml", XamlA11yAudit.ItemsUnnamed)] = 2,
            [("PadForge.App/Views/ShiftActivatorDialog.xaml", XamlA11yAudit.BtnTooltipKey)] = 7,
            [("PadForge.App/Views/ShiftActivatorDialog.xaml", XamlA11yAudit.BtnTooltipOther)] = 4,
            [("PadForge.App/Views/ShiftActivatorDialog.xaml", XamlA11yAudit.ImageNoName)] = 1,
            [("PadForge.App/Views/ShiftActivatorDialog.xaml", XamlA11yAudit.InNothing)] = 16,
            [("PadForge.App/Views/ShiftActivatorDialog.xaml", XamlA11yAudit.InSiblingLabel)] = 2,
            [("PadForge.App/Views/ShiftActivatorDialog.xaml", XamlA11yAudit.InTooltipOnly)] = 1,
            [("PadForge.App/Views/ShiftLayerFlyout.xaml", XamlA11yAudit.ImageNoName)] = 1,
            [("PadForge.App/Views/StarterProfilesDialog.xaml", XamlA11yAudit.BtnTooltipKey)] = 1,
            [("PadForge.App/Views/TouchpadGestureRecorderDialog.xaml", XamlA11yAudit.BtnTooltipKey)] = 1,
            [("PadForge.App/Views/TouchpadGestureRecorderDialog.xaml", XamlA11yAudit.InSiblingLabel)] = 2,
            [("PadForge.App/Views/TouchpadGestureRecorderDialog.xaml", XamlA11yAudit.MouseOnly)] = 2,
            [("PadForge.App/Views/TouchpadOverlay.xaml", XamlA11yAudit.MouseOnly)] = 4,
            [("PadForge.App/Views/WorkshopBrowseDialog.xaml", XamlA11yAudit.BtnTooltipKey)] = 1,
            [("PadForge.App/Views/WorkshopBrowseDialog.xaml", XamlA11yAudit.ImageNoName)] = 4,
            [("PadForge.App/Views/WorkshopBrowseDialog.xaml", XamlA11yAudit.InNothing)] = 2,
            [("PadForge.App/Views/WorkshopBrowseDialog.xaml", XamlA11yAudit.ItemsUnnamed)] = 1,
            [("PadForge.App/Views/WorkshopBrowseDialog.xaml", XamlA11yAudit.MouseOnly)] = 6,
        };

        // Findings that stay, each with its reason, matched by file, class and
        // identity (the tag with its AutomationId, x:Name or binding).
        private static readonly (string File, string Class, string Identity, string Reason)[] Allowlist =
        {
        };

        private static bool Allowed(XamlA11yAudit.Finding f)
            => Allowlist.Any(a => a.File == f.File && a.Class == f.Class && a.Identity == f.Identity);

        [Fact]
        public void EveryFileAndClass_StaysAtItsCeiling()
        {
            var findings = XamlA11yAudit.Measure(RepoRoot());
            var counted = findings.Where(f => !Allowed(f)).ToList();
            var measured = counted.GroupBy(f => (f.File, f.Class)).ToDictionary(g => g.Key, g => g.Count());

            string dump = Environment.GetEnvironmentVariable("PADFORGE_A11Y_AUDIT_OUT");
            if (!string.IsNullOrEmpty(dump))
                WriteDump(dump, findings, measured);

            var over = new StringBuilder();
            var under = new StringBuilder();
            foreach (var (key, count) in measured.OrderBy(k => k.Key.File, StringComparer.Ordinal).ThenBy(k => k.Key.Class, StringComparer.Ordinal))
            {
                int ceiling = Ceilings.TryGetValue(key, out int c) ? c : 0;
                if (count > ceiling)
                {
                    over.AppendLine($"  {key.File} {key.Class}: {count}, ceiling {ceiling}");
                    foreach (var f in counted.Where(f => f.File == key.File && f.Class == key.Class).Take(15))
                        over.AppendLine($"    line {f.Line}: {f.Tag} {f.Key ?? f.Detail}");
                }
                else if (count < ceiling)
                    under.AppendLine($"  {key.File} {key.Class}: {ceiling} -> {count}");
            }
            foreach (var (key, ceiling) in Ceilings)
                if (ceiling > 0 && !measured.ContainsKey(key))
                    under.AppendLine($"  {key.File} {key.Class}: {ceiling} -> 0");

            Assert.True(over.Length == 0 && under.Length == 0,
                (over.Length > 0 ? "New screen-reader offenders. Name each control, or allowlist one with its reason:\n" + over : "")
                + (under.Length > 0 ? "Fixed findings. Lower these ceilings:\n" + under : ""));
        }

        [Fact]
        public void EveryAllowlistEntry_StillMatchesAFinding()
        {
            var findings = XamlA11yAudit.Measure(RepoRoot());
            var stale = Allowlist.Where(a => !findings.Any(f => f.File == a.File && f.Class == a.Class && f.Identity == a.Identity))
                .Select(a => $"  {a.File} {a.Class} {a.Identity}").ToList();
            Assert.True(stale.Count == 0, "Allowlist entries that match nothing. Remove them:\n" + string.Join("\n", stale));
        }

        private static void WriteDump(string path, List<XamlA11yAudit.Finding> findings,
            Dictionary<(string File, string Class), int> measured)
        {
            var table = new StringBuilder();
            foreach (var (key, count) in measured.OrderBy(k => k.Key.File, StringComparer.Ordinal).ThenBy(k => k.Key.Class, StringComparer.Ordinal))
                table.AppendLine($"            [(\"{key.File}\", XamlA11yAudit.{XamlA11yAudit.ConstantName(key.Class)})] = {count},");
            var doc = new
            {
                summary = findings.GroupBy(f => f.Class).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
                ceilings = table.ToString(),
                findings,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        }

        // ── The audit itself, on XAML written here ──

        private static List<XamlA11yAudit.Finding> Audit(string body, string resources = "")
        {
            string xaml =
                "<UserControl xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"\n" +
                "             xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n" +
                "             xmlns:ui=\"http://schemas.lepo.co/wpfui/2022/xaml\"\n" +
                "             xmlns:reset=\"clr-namespace:PadForge.Controls\"\n" +
                "             xmlns:strings=\"clr-namespace:PadForge.Resources.Strings\">\n" +
                "  <UserControl.Resources>\n" + resources + "\n  </UserControl.Resources>\n" +
                body + "\n</UserControl>";
            return XamlA11yAudit.Measure(new[] { ("PadForge.App/Views/Probe.xaml", xaml) });
        }

        private const string Key = "{Binding Common_Close, Source={x:Static strings:Strings.Instance}}";

        [Fact]
        public void AButtonWithoutText_IsSortedByWhereItsNameWouldComeFrom()
        {
            var f = Audit(
                "<StackPanel>\n" +
                "  <Button Content=\"&#xE711;\" ToolTip=\"" + Key + "\"/>\n" +
                "  <Button Content=\"&#xE711;\" ToolTip=\"{Binding StatusText}\"/>\n" +
                "  <Button><TextBlock Text=\"&#xE700;\"/></Button>\n" +
                "  <Button Content=\"Close\"/>\n" +
                "  <Button Content=\"{Binding Label}\"/>\n" +
                "  <Button Content=\"&#xE711;\" AutomationProperties.Name=\"" + Key + "\"/>\n" +
                "</StackPanel>");
            Assert.Equal(new[] { XamlA11yAudit.BtnTooltipKey, XamlA11yAudit.BtnTooltipOther, XamlA11yAudit.BtnNoTooltip },
                f.Select(x => x.Class));
            Assert.Equal("Common_Close", f[0].Key);
        }

        [Fact]
        public void AStyleThatNamesTheButton_CountsThroughBasedOn_AndTriggerTooltipsAreStateText()
        {
            var f = Audit(
                "<StackPanel>\n" +
                "  <Button Style=\"{StaticResource Tight}\" Content=\"&#xE711;\"/>\n" +
                "  <Button Style=\"{StaticResource Stateful}\" Content=\"&#xE7E8;\"/>\n" +
                "</StackPanel>",
                "    <Style x:Key=\"Reset\" TargetType=\"Button\">\n" +
                "      <Setter Property=\"AutomationProperties.Name\" Value=\"{Binding ToolTip, RelativeSource={RelativeSource Self}}\"/>\n" +
                "    </Style>\n" +
                "    <Style x:Key=\"Tight\" TargetType=\"Button\" BasedOn=\"{StaticResource Reset}\"/>\n" +
                "    <Style x:Key=\"Stateful\" TargetType=\"Button\">\n" +
                "      <Style.Triggers><DataTrigger Binding=\"{Binding On}\" Value=\"True\"><Setter Property=\"ToolTip\" Value=\"{Binding OnText}\"/></DataTrigger></Style.Triggers>\n" +
                "    </Style>");
            var only = Assert.Single(f);
            Assert.Equal(XamlA11yAudit.BtnStyleTooltip, only.Class);
            Assert.Equal(17, only.Line);
        }

        [Fact]
        public void ControlTemplateParts_AreLeftToTheControlThatOwnsThem()
        {
            var f = Audit(
                "<ContentControl>\n" +
                "  <ContentControl.Template><ControlTemplate TargetType=\"ContentControl\">\n" +
                "    <Button Content=\"&#xE711;\"/>\n" +
                "  </ControlTemplate></ContentControl.Template>\n" +
                "</ContentControl>");
            Assert.Empty(f);
        }

        [Fact]
        public void AnInput_TakesTheLabelBesideIt_InItsOwnGridRow_OrTheResetButtonsSettingLabel()
        {
            var f = Audit(
                "<StackPanel>\n" +
                "  <Grid>\n" +
                "    <TextBlock Grid.Row=\"0\" Text=\"{Binding Dash_Port, Source={x:Static strings:Strings.Instance}}\"/>\n" +
                "    <ui:NumberBox Grid.Row=\"0\" Value=\"{Binding Port}\"/>\n" +
                "    <ui:NumberBox Grid.Row=\"1\" Value=\"{Binding Other}\"/>\n" +
                "  </Grid>\n" +
                "  <StackPanel Orientation=\"Horizontal\">\n" +
                "    <ui:NumberBox Value=\"{Binding Range}\"/>\n" +
                "    <reset:SettingResetButton SettingLabel=\"{Binding Head_Range, Source={x:Static strings:Strings.Instance}}\"/>\n" +
                "  </StackPanel>\n" +
                "</StackPanel>");
            Assert.Equal(new[] { XamlA11yAudit.InSiblingLabel, XamlA11yAudit.InNothing, XamlA11yAudit.InSiblingLabel },
                f.Select(x => x.Class));
            Assert.Equal("Dash_Port", f[0].Key);
            Assert.Equal("Head_Range", f[2].Key);
        }

        [Fact]
        public void AnInputWithoutALabel_IsSortedByWhatItHas_AndANamedOneIsNotCounted()
        {
            var f = Audit(
                "<StackPanel>\n" +
                "  <TextBox ToolTip=\"{Binding Help}\"/>\n" +
                "  <ui:TextBox PlaceholderText=\"{Binding Hint}\"/>\n" +
                "  <ComboBox SelectedItem=\"{Binding Mode}\"/>\n" +
                "  <Slider AutomationProperties.Name=\"" + Key + "\"/>\n" +
                "  <CheckBox/>\n" +
                "  <CheckBox Content=\"{Binding Label}\"/>\n" +
                "</StackPanel>");
            Assert.Equal(new[] { XamlA11yAudit.InTooltipOnly, XamlA11yAudit.InPlaceholder, XamlA11yAudit.InNothing, XamlA11yAudit.ToggleNoText },
                f.Select(x => x.Class));
        }

        [Fact]
        public void ListsImagesAndMouseHandlers_AreCountedUnlessTheirItemsOrElementsAreNamed()
        {
            var f = Audit(
                "<StackPanel>\n" +
                "  <ListBox ItemsSource=\"{Binding Devices}\"/>\n" +
                "  <ListBox ItemsSource=\"{Binding Peers}\" DisplayMemberPath=\"Name\"/>\n" +
                "  <ListBox ItemsSource=\"{Binding Slots}\">\n" +
                "    <ListBox.ItemContainerStyle><Style TargetType=\"ListBoxItem\"><Setter Property=\"AutomationProperties.Name\" Value=\"{Binding Title}\"/></Style></ListBox.ItemContainerStyle>\n" +
                "  </ListBox>\n" +
                "  <Image Source=\"logo.png\"/>\n" +
                "  <Image Source=\"qr.png\" AutomationProperties.Name=\"" + Key + "\"/>\n" +
                "  <Border MouseLeftButtonDown=\"Chip_Click\"/>\n" +
                "  <Button MouseLeftButtonDown=\"Ok_Down\" Content=\"OK\"/>\n" +
                "</StackPanel>");
            Assert.Equal(new[] { XamlA11yAudit.ItemsUnnamed, XamlA11yAudit.ItemsDisplayMemberPath, XamlA11yAudit.ImageNoName, XamlA11yAudit.MouseOnly },
                f.Select(x => x.Class));
            Assert.Equal("Border|MouseLeftButtonDown:Chip_Click", f[3].Identity);
        }
    }

    /// <summary>
    /// The static screen-reader audit: a port of the 2026-10-06 audit script
    /// (memory a11y-audit-tooling-scripts-2026-10-06). Keyed styles resolve
    /// through BasedOn, ControlTemplate parts are skipped, and each control
    /// without a name gets the fix class it needs and the Strings key to
    /// reuse.
    /// </summary>
    internal static class XamlA11yAudit
    {
        /// <summary>A button whose tooltip binds a Strings key: bind its name
        /// to the same key.</summary>
        internal const string BtnTooltipKey = "BTN_TOOLTIP_KEY";
        /// <summary>A button whose tooltip is not a Strings key.</summary>
        internal const string BtnTooltipOther = "BTN_TOOLTIP_OTHER";
        /// <summary>A button whose tooltip comes from style setters or
        /// triggers (state text): it needs a stable name.</summary>
        internal const string BtnStyleTooltip = "BTN_STYLE_TOOLTIP";
        /// <summary>A button with no text and no tooltip.</summary>
        internal const string BtnNoTooltip = "BTN_NO_TOOLTIP";
        /// <summary>An input with a visible label beside it, or a reset
        /// button's SettingLabel after it.</summary>
        internal const string InSiblingLabel = "IN_SIBLING_LABEL";
        /// <summary>An input whose only text is a tooltip sentence.</summary>
        internal const string InTooltipOnly = "IN_TOOLTIP_ONLY";
        /// <summary>An input with only placeholder text.</summary>
        internal const string InPlaceholder = "IN_PLACEHOLDER";
        /// <summary>An input with no label source at all.</summary>
        internal const string InNothing = "IN_NOTHING";
        /// <summary>A check box, radio button or switch with no text.</summary>
        internal const string ToggleNoText = "TOGGLE_NO_TEXT";
        /// <summary>A list bound to ItemsSource whose items read their
        /// ToString().</summary>
        internal const string ItemsUnnamed = "ITEMS_UNNAMED";
        /// <summary>A list with DisplayMemberPath: the closed value reads text,
        /// the items still read ToString().</summary>
        internal const string ItemsDisplayMemberPath = "ITEMS_DISPLAYMEMBERPATH";
        /// <summary>An image without a name.</summary>
        internal const string ImageNoName = "IMAGE_NO_NAME";
        /// <summary>A mouse handler on an element that is not a control.</summary>
        internal const string MouseOnly = "MOUSE_ONLY";

        internal sealed record Finding(string File, string Class, int Line, string Tag, string Key, string Detail, string Identity);

        internal static string ConstantName(string cls) => cls switch
        {
            BtnTooltipKey => nameof(BtnTooltipKey),
            BtnTooltipOther => nameof(BtnTooltipOther),
            BtnStyleTooltip => nameof(BtnStyleTooltip),
            BtnNoTooltip => nameof(BtnNoTooltip),
            InSiblingLabel => nameof(InSiblingLabel),
            InTooltipOnly => nameof(InTooltipOnly),
            InPlaceholder => nameof(InPlaceholder),
            InNothing => nameof(InNothing),
            ToggleNoText => nameof(ToggleNoText),
            ItemsUnnamed => nameof(ItemsUnnamed),
            ItemsDisplayMemberPath => nameof(ItemsDisplayMemberPath),
            ImageNoName => nameof(ImageNoName),
            MouseOnly => nameof(MouseOnly),
            _ => cls,
        };

        private static readonly HashSet<string> Buttons = new() { "Button", "ToggleButton", "RepeatButton", "HyperlinkButton", "Anchor", "SplitButton", "DropDownButton" };
        private static readonly HashSet<string> Inputs = new() { "TextBox", "NumberBox", "PasswordBox", "ComboBox", "Slider", "AutoSuggestBox", "RichTextBox" };
        private static readonly HashSet<string> Toggles = new() { "CheckBox", "RadioButton", "ToggleSwitch" };
        private static readonly HashSet<string> Lists = new() { "ListBox", "ListView", "ItemsControl", "DataGrid", "TreeView", "ComboBox" };
        private static readonly string[] MouseEvents =
            { "MouseLeftButtonDown", "MouseLeftButtonUp", "MouseDown", "MouseUp", "PreviewMouseLeftButtonDown", "PreviewMouseDown", "PreviewMouseLeftButtonUp" };
        private static readonly HashSet<string> Controlish = new(Buttons.Concat(Inputs).Concat(Toggles)
            .Concat(new[] { "MenuItem", "ListBoxItem", "ListViewItem", "TabItem", "Thumb", "ScrollBar" }));
        private static readonly Regex Pua = new(@"[\uE000-\uF8FF]");
        private static readonly Regex StrKey = new(@"\{Binding\s+(?:Path=)?(\w+),\s*Source=\{x:Static\s+\w+:Strings\.Instance\}\}");
        private static readonly Regex StyleRef = new(@"\{(?:StaticResource|DynamicResource)\s+(\w+)\}");

        private sealed class StyleInfo
        {
            public bool Name;
            public string Content;
            public string ToolTip;
            public bool ToolTipTrigger;
            public string BasedOn;
            public StyleInfo Copy() => (StyleInfo)MemberwiseClone();
        }

        /// <summary>Audits every XAML file under PadForge.App, build output
        /// left out, in ordinal path order.</summary>
        internal static List<Finding> Measure(string repoRoot)
        {
            string app = Path.Combine(repoRoot, "PadForge.App");
            var files = Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories)
                .Where(p => !Path.GetRelativePath(app, p).Split(Path.DirectorySeparatorChar).SkipLast(1).Any(s => s == "obj" || s == "bin"))
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => (Path.GetRelativePath(repoRoot, p).Replace('\\', '/'), File.ReadAllText(p)))
                .ToList();
            return Measure(files);
        }

        internal static List<Finding> Measure(IReadOnlyList<(string Rel, string Text)> files)
        {
            var trees = files.Select(f => (f.Rel, Root: XDocument.Parse(f.Text, LoadOptions.SetLineInfo).Root)).ToList();

            // The first definition of a key wins, as the files are ordered.
            var keyed = new Dictionary<string, StyleInfo>(StringComparer.Ordinal);
            foreach (var (_, root) in trees)
                foreach (var e in root.DescendantsAndSelf())
                    if (Local(e) == "Style" && Attr(e, "Key") is { Length: > 0 } k && !keyed.ContainsKey(k))
                        keyed[k] = StyleInfoOf(e);

            var findings = new List<Finding>();
            foreach (var (rel, root) in trees)
            {
                foreach (var n in root.DescendantsAndSelf())
                {
                    if (n.Ancestors().Any(a => Local(a) == "ControlTemplate"))
                        continue;
                    string tag = Local(n);
                    int line = ((IXmlLineInfo)n).LineNumber;
                    StyleInfo st = null;
                    bool named = false;
                    if (Buttons.Contains(tag) || Inputs.Contains(tag) || Toggles.Contains(tag))
                    {
                        st = EffectiveStyle(n, keyed);
                        named = HasName(n, st);
                    }

                    void Add(string cls, string key, string detail, string extra)
                        => findings.Add(new Finding(rel, cls, line, tag, key, detail, Identity(n, extra)));

                    if (Buttons.Contains(tag) && !named && string.IsNullOrEmpty(TextOf(n, st)))
                    {
                        var (tt, src) = ToolTipOf(n, st);
                        string cls = tt == null ? BtnNoTooltip
                            : src == "style" ? BtnStyleTooltip
                            : StrKey.IsMatch(tt) ? BtnTooltipKey
                            : BtnTooltipOther;
                        Add(cls, KeyOf(tt), tt, Attr(n, "Click") ?? Attr(n, "Command") ?? KeyOf(tt) ?? tt);
                    }

                    if (Inputs.Contains(tag) && !named)
                    {
                        string lab = SiblingLabel(n);
                        var (tt, _) = ToolTipOf(n, st);
                        string ph = Attr(n, "PlaceholderText");
                        string cls = !string.IsNullOrEmpty(lab) ? InSiblingLabel
                            : !string.IsNullOrEmpty(tt) ? InTooltipOnly
                            : !string.IsNullOrEmpty(ph) ? InPlaceholder
                            : InNothing;
                        string bound = Attr(n, "Text") ?? Attr(n, "Value") ?? Attr(n, "SelectedItem") ?? Attr(n, "SelectedValue");
                        Add(cls, KeyOf(lab) ?? KeyOf(tt) ?? KeyOf(ph), lab ?? tt ?? ph ?? bound, bound);
                    }

                    if (Toggles.Contains(tag) && !named && string.IsNullOrEmpty(TextOf(n, st)))
                        Add(ToggleNoText, null, null, Attr(n, "IsChecked"));

                    if (Lists.Contains(tag) && Attr(n, "ItemsSource") is { Length: > 0 } items)
                    {
                        bool setter = n.DescendantsAndSelf().Any(c => Local(c) == "Setter"
                            && (Attr(c, "Property") ?? "").EndsWith("AutomationProperties.Name", StringComparison.Ordinal));
                        if (!setter)
                            Add(string.IsNullOrEmpty(Attr(n, "DisplayMemberPath")) ? ItemsUnnamed : ItemsDisplayMemberPath, null, items, items);
                    }

                    if (tag == "Image" && AttrAuto(n, "Name") == null)
                        Add(ImageNoName, null, Attr(n, "Source"), Attr(n, "Source"));

                    if (!Controlish.Contains(tag))
                        foreach (string ev in MouseEvents)
                            if (Attr(n, ev) is { Length: > 0 } handler)
                                Add(MouseOnly, null, ev + ":" + handler, ev + ":" + handler);
                }
            }
            return findings;
        }

        private static string Local(XElement e) => e.Name.LocalName;

        private static string Attr(XElement e, string name)
            => e.Attributes().FirstOrDefault(a => !a.IsNamespaceDeclaration && a.Name.LocalName == name)?.Value;

        private static string AttrAuto(XElement e, string prop)
            => e.Attributes().FirstOrDefault(a => a.Name.LocalName.EndsWith("AutomationProperties." + prop, StringComparison.Ordinal))?.Value;

        private static string OwnText(XElement e) => string.Concat(e.Nodes().OfType<XText>().Select(t => t.Value));

        private static string Row(XElement e) => Attr(e, "Grid.Row") is { Length: > 0 } r ? r : "0";

        private static string KeyOf(string text)
            => text != null && StrKey.Match(text) is { Success: true } m ? m.Groups[1].Value : null;

        /// <summary>A line-independent handle for the allowlist: the tag with
        /// the AutomationId, the x:Name, or the element's own binding.</summary>
        private static string Identity(XElement n, string extra)
            => Local(n) + "|" + (AttrAuto(n, "AutomationId") ?? Attr(n, "Name") ?? extra ?? "");

        private static StyleInfo StyleInfoOf(XElement style)
        {
            var info = new StyleInfo();
            if (Attr(style, "BasedOn") is string bo && StyleRef.Match(bo) is { Success: true } m)
                info.BasedOn = m.Groups[1].Value;
            foreach (var c in style.Elements())
            {
                if (Local(c) == "Setter")
                {
                    string prop = Attr(c, "Property") ?? "";
                    string val = Attr(c, "Value");
                    if (prop.EndsWith("AutomationProperties.Name", StringComparison.Ordinal))
                        info.Name = true;
                    else if (prop == "Content" && val != null)
                        info.Content = val;
                    else if (prop == "ToolTip" && val != null)
                        info.ToolTip = val;
                }
                if (Local(c).EndsWith("Triggers", StringComparison.Ordinal)
                    && c.DescendantsAndSelf().Any(t => Local(t) == "Setter" && Attr(t, "Property") == "ToolTip"))
                    info.ToolTipTrigger = true;
            }
            return info;
        }

        private static StyleInfo Resolve(StyleInfo info, Dictionary<string, StyleInfo> keyed, int depth = 0)
        {
            var o = info.Copy();
            if (info.BasedOn != null && depth < 10 && keyed.TryGetValue(info.BasedOn, out var b))
            {
                var bs = Resolve(b, keyed, depth + 1);
                o.Name = o.Name || bs.Name;
                o.Content ??= bs.Content;
                o.ToolTip ??= bs.ToolTip;
                o.ToolTipTrigger = o.ToolTipTrigger || bs.ToolTipTrigger;
            }
            return o;
        }

        private static StyleInfo EffectiveStyle(XElement n, Dictionary<string, StyleInfo> keyed)
        {
            if (Attr(n, "Style") is string s && StyleRef.Match(s) is { Success: true } m && keyed.TryGetValue(m.Groups[1].Value, out var ks))
                return Resolve(ks, keyed);
            foreach (var c in n.Elements())
                if (Local(c).EndsWith(".Style", StringComparison.Ordinal))
                    foreach (var st in c.Elements())
                        if (Local(st) == "Style")
                            return Resolve(StyleInfoOf(st), keyed);
            return new StyleInfo();
        }

        private static bool HasName(XElement n, StyleInfo st)
        {
            if (AttrAuto(n, "Name") != null || AttrAuto(n, "LabeledBy") != null)
                return true;
            if (n.Elements().Any(c => Local(c) is "AutomationProperties.Name" or "AutomationProperties.LabeledBy"))
                return true;
            return st.Name;
        }

        /// <summary>The text a screen reader would take from the content, or
        /// null for a glyph, a blank or no content. Only the first content
        /// child is read.</summary>
        private static string TextOf(XElement n, StyleInfo st)
        {
            string content = Attr(n, "Content") ?? st.Content;
            if (content != null)
            {
                if (content.StartsWith("{", StringComparison.Ordinal))
                    return Pua.IsMatch(content) ? null : content;
                return Pua.IsMatch(content) || content.Trim().Length == 0 ? null : content;
            }
            foreach (var c in n.Elements())
            {
                if (Local(c).Contains('.'))
                    continue;
                foreach (var d in c.DescendantsAndSelf())
                {
                    if (Local(d) is "TextBlock" or "Run" or "AccessText" or "Label")
                    {
                        string t = Attr(d, "Text");
                        if (string.IsNullOrEmpty(t))
                            t = OwnText(d).Trim();
                        if (t.Length > 0 && !Pua.IsMatch(t))
                            return t;
                    }
                }
                return null;
            }
            return null;
        }

        private static (string Text, string Source) ToolTipOf(XElement n, StyleInfo st)
        {
            if (Attr(n, "ToolTip") is string t)
                return (t, "attr");
            if (n.Elements().Any(c => Local(c).EndsWith(".ToolTip", StringComparison.Ordinal)))
                return ("(element)", "element");
            if (st.ToolTip != null || st.ToolTipTrigger)
                return (st.ToolTip ?? "(trigger)", "style");
            return (null, null);
        }

        /// <summary>The nearest earlier label sibling in the same Grid row,
        /// skipping other inputs so a slider's value box finds the slider's
        /// label, then the SettingLabel of a reset button later in the
        /// row.</summary>
        private static string SiblingLabel(XElement n)
        {
            var p = n.Parent;
            if (p == null)
                return null;
            var sibs = p.Elements().ToList();
            int idx = sibs.IndexOf(n);
            string myRow = Row(n);
            bool grid = Local(p) == "Grid";
            for (int i = idx - 1; i >= 0; i--)
            {
                var s = sibs[i];
                string tag = Local(s);
                if (tag.Contains('.'))
                    continue;
                if (grid && Row(s) != myRow)
                    continue;
                if (Inputs.Contains(tag) || tag == "SettingResetButton")
                    continue;
                foreach (var d in s.DescendantsAndSelf())
                {
                    if (Local(d) is "TextBlock" or "Label")
                    {
                        string t = Attr(d, "Text");
                        if (string.IsNullOrEmpty(t))
                            t = Attr(d, "Content");
                        if (string.IsNullOrEmpty(t))
                            t = OwnText(d).Trim();
                        if (t.Length > 0 && !Pua.IsMatch(t) && t != "%" && StrKey.IsMatch(t))
                            return t;
                    }
                }
                break;
            }
            for (int i = idx + 1; i < sibs.Count; i++)
            {
                var s = sibs[i];
                string tag = Local(s);
                if (tag.Contains('.'))
                    continue;
                if (grid && Row(s) != myRow)
                    continue;
                if (tag == "SettingResetButton" && Attr(s, "SettingLabel") is { Length: > 0 } label)
                    return label;
                if (Inputs.Contains(tag))
                    break;
            }
            return null;
        }
    }
}

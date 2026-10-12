using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Menus;
using PadForge.Models2D;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    /// <summary>
    /// Paste and Copy From between two virtual controllers of different
    /// layouts. The Mappings page says both translate the mappings by
    /// position: an Xbox slot's A lands on raw button 0 of an Extended slot,
    /// its left stick X on raw axis 0, its D-pad on hat 0. The position
    /// tables are MappingTranslation's.
    ///
    /// <para>Each test drives the calls MainWindow.OnCopySettings,
    /// OnPasteSettings and OnCopyFrom make, in their order, on real view
    /// models, then the push the autosave timer runs, and reads the target
    /// slot's MappingSet: the rows the grid shows, the engine evaluates and
    /// the settings file stores.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class CrossLayoutPasteTests : IDisposable
    {
        private const int SourceSlot = 0;
        private const int TargetSlot = 1;
        private static readonly Guid SourcePad = new("c1055001-1111-2222-3333-444444444444");
        private static readonly Guid TargetPad = new("c1055002-1111-2222-3333-444444444444");
        private static readonly Guid PadModel = new("c1055003-1111-2222-3333-444444444444");

        private const VirtualControllerType Xbox = VirtualControllerType.Xbox;
        private const VirtualControllerType PlayStation = VirtualControllerType.PlayStation;
        private const VirtualControllerType Nintendo = VirtualControllerType.Nintendo;
        private const VirtualControllerType Extended = VirtualControllerType.Extended;
        private const VirtualControllerType Midi = VirtualControllerType.Midi;
        private const VirtualControllerType KeyboardMouse = VirtualControllerType.KeyboardMouse;
        private const VirtualControllerType Vr = VirtualControllerType.Vr;

        private readonly ITestOutputHelper _output;
        private readonly SettingsCollection _savedSettings = SettingsManager.UserSettings;
        private readonly DeviceCollection _savedDevices = SettingsManager.UserDevices;
        private readonly List<ProfileData> _savedProfiles = SettingsManager.Profiles;
        private readonly string _savedActive = SettingsManager.ActiveProfileId;
        private readonly ProfileData _savedPendingDefault = SettingsManager.PendingDefaultSnapshot;
        private readonly bool[] _savedCreated = SettingsManager.SlotCreated;
        private readonly bool[] _savedEnabled = SettingsManager.SlotEnabled;
        private readonly MappingSet[] _savedSets = SettingsManager.SlotMappingSets;
        private readonly List<int>[] _savedOrders = Orders();
        private readonly string[] _savedWires = { SettingsManager.GetWireStamp(SourceSlot), SettingsManager.GetWireStamp(TargetSlot) };
        private readonly Action _savedAfterRefresh = SettingsService.AfterMappingSetsRefreshed;
        private readonly bool _savedStale = InputService.VmMappingsStale;
        private readonly bool _savedSuppress = InputService.SuppressMappingEditPush;

        private MainViewModel _vm;
        private SettingsService _settings;
        private InputService _input;

        public CrossLayoutPasteTests(ITestOutputHelper output) => _output = output;

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.Profiles = _savedProfiles;
            SettingsManager.ActiveProfileId = _savedActive;
            SettingsManager.PendingDefaultSnapshot = _savedPendingDefault;
            SettingsManager.SlotCreated = _savedCreated;
            SettingsManager.SlotEnabled = _savedEnabled;
            SettingsManager.SlotMappingSets = _savedSets;
            SetOrders(_savedOrders);
            SettingsManager.StampNintendoWire(SourceSlot, _savedWires[0]);
            SettingsManager.StampNintendoWire(TargetSlot, _savedWires[1]);
            SettingsService.AfterMappingSetsRefreshed = _savedAfterRefresh;
            InputService.VmMappingsStale = _savedStale;
            InputService.SuppressMappingEditPush = _savedSuppress;
        }

        private static List<int>[] Orders() => new[]
        {
            SettingsManager.XboxSlotOrder, SettingsManager.PlayStationSlotOrder,
            SettingsManager.NintendoSlotOrder, SettingsManager.ExtendedSlotOrder,
            SettingsManager.KeyboardMouseSlotOrder, SettingsManager.MidiSlotOrder,
            SettingsManager.VrSlotOrder,
        };

        private static void SetOrders(List<int>[] orders)
        {
            SettingsManager.XboxSlotOrder = orders[0];
            SettingsManager.PlayStationSlotOrder = orders[1];
            SettingsManager.NintendoSlotOrder = orders[2];
            SettingsManager.ExtendedSlotOrder = orders[3];
            SettingsManager.KeyboardMouseSlotOrder = orders[4];
            SettingsManager.MidiSlotOrder = orders[5];
            SettingsManager.VrSlotOrder = orders[6];
        }

        // The two slots.

        /// <summary>A source slot and a target slot of the given layouts, one
        /// pad of the same model on each, both grids loaded.</summary>
        private void Arrange(VirtualControllerType sourceType, VirtualControllerType targetType)
        {
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.Profiles = new List<ProfileData>();
            SettingsManager.ActiveProfileId = null;
            SettingsManager.PendingDefaultSnapshot = null;
            SettingsManager.SlotCreated = new bool[InputManager.MaxPads];
            SettingsManager.SlotEnabled = new bool[InputManager.MaxPads];
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            SetOrders(Enumerable.Range(0, 7).Select(_ => new List<int>()).ToArray());
            SettingsManager.StampNintendoWire(SourceSlot, null);
            SettingsManager.StampNintendoWire(TargetSlot, null);
            InputService.VmMappingsStale = false;
            InputService.SuppressMappingEditPush = false;

            _vm = new MainViewModel();
            _settings = new SettingsService(_vm);
            _input = new InputService(_vm) { SettingsService = _settings };

            AddSlot(SourceSlot, sourceType);
            AddSlot(TargetSlot, targetType);
            AddDevice(SourcePad, PadModel, SourceSlot);
            AddDevice(TargetPad, PadModel, TargetSlot);
            _input.RefreshDeviceList();
            Select(SourceSlot, SourcePad);
            Select(TargetSlot, TargetPad);
        }

        private void AddSlot(int slot, VirtualControllerType type)
        {
            SettingsManager.SlotCreated[slot] = SettingsManager.SlotEnabled[slot] = true;
            SettingsManager.SlotOrders.GetOrderFor(type).Add(slot);
            SettingsManager.SlotMappingSets[slot] = new MappingSet();
            var pad = _vm.Pads[slot];
            if (pad.OutputType != type) pad.OutputType = type;
            else pad.ProfileId = InputManager.GetDefaultProfileId(type);
            if (type == Extended)
            {
                // Two sticks and two triggers interleave as the gamepad order
                // does, and seventeen buttons give every gamepad button a row.
                pad.ExtendedConfig.TriggerCount = 0;
                pad.ExtendedConfig.ThumbstickCount = 2;
                pad.ExtendedConfig.TriggerCount = 2;
                pad.ExtendedConfig.PovCount = 1;
                pad.ExtendedConfig.ButtonCount = 17;
            }
            pad.RebuildMappings();
            Assert.True(pad.MappingsViewLoaded);
        }

        /// <summary>Assigns a device to a slot. A device already known, one
        /// that sits on another slot too, keeps its one device record.</summary>
        private static PadSetting AddDevice(Guid id, Guid product, int slot)
        {
            if (!SettingsManager.UserDevices.Items.Any(d => d.InstanceGuid == id))
            {
                SettingsManager.UserDevices.Items.Add(new UserDevice
                {
                    InstanceGuid = id, ProductGuid = product, IsOnline = true,
                    ProductName = "Cross-layout test pad", InstanceName = "Cross-layout test pad",
                    CapType = InputDeviceType.Gamepad, CapAxeCount = 6, CapButtonCount = 18, CapPovCount = 1,
                });
            }
            var setting = new PadSetting();
            var row = new UserSetting { InstanceGuid = id, ProductGuid = product, MapTo = slot };
            row.SetPadSetting(setting);
            SettingsManager.UserSettings.Items.Add(row);
            return setting;
        }

        private void Select(int slot, Guid device)
        {
            var pad = _vm.Pads[slot];
            pad.SelectedMappedDevice = pad.MappedDevices.Single(d => d.InstanceGuid == device);
        }

        private static bool IsRaw(VirtualControllerType type)
            => type is VirtualControllerType.Extended or VirtualControllerType.Nintendo;

        private static MappingSource Source(Guid device, string descriptor, bool invert = false)
            => new() { DeviceGuid = device.ToString(), Descriptor = descriptor, Invert = invert };

        private static MappingRow Row(string target, params MappingSource[] sources)
            => new() { Target = target, LayerMask = "Base", Sources = sources.ToList() };

        /// <summary>A two-button axis: a positive leg, and a negative leg on
        /// the same device with the opposite Invert.</summary>
        private static MappingSource[] Legs(Guid device, string positive, string negative)
            => new[] { Source(device, positive), Source(device, negative, invert: true) };

        private static MappingRow Formula(string target, string expression, params MappingSource[] sources)
        {
            var row = Row(target, sources);
            row.CombineMode = "Custom";
            row.CombineExpression = expression;
            return row;
        }

        /// <summary>Stores a slot's rows and reloads its grid from them.</summary>
        private void SetRows(int slot, params MappingRow[] rows)
        {
            SettingsManager.SlotMappingSets[slot].Rows = rows.ToList();
            InputService.RefreshMappingsToViewModel(_vm.Pads[slot]);
        }

        // The handlers' calls.

        /// <summary>The payload MainWindow.OnCopySettings puts on the
        /// clipboard, built by its calls in its order.</summary>
        private string Copy(int slot)
        {
            var pad = _vm.Pads[slot];
            var ps = _input.GetCurrentPadSetting(slot);
            ps = SlotAppearancePersistence.CreateClipboardSetting(pad, ps);
            var type = pad.OutputType;
            bool raw = IsRaw(type);
            ps.SlotProfileId = pad.ProfileId;

            ps.SlotMultiSourceRows = InputService.ExtractAllRowsForSlot(slot);
            ps.DeviceScopedMultiSourceRows = null;

            var configs = SlotAppearancePersistence.FilterClipboardDeviceConfigs(ps,
                _settings.BuildDeviceConfigSnapshotForSlot(slot));
            if (configs != null && configs.Length > 0)
                ps.SlotDeviceConfigsJson = System.Text.Json.JsonSerializer.Serialize(configs);
            if (_settings.BuildExtendedConfigSnapshotForSlot(slot) is { } extended)
                ps.SlotExtendedConfigJson = System.Text.Json.JsonSerializer.Serialize(extended);
            if (_settings.BuildMidiConfigSnapshotForSlot(slot) is { } midi)
                ps.SlotMidiConfigJson = System.Text.Json.JsonSerializer.Serialize(midi);
            if (_settings.BuildKbmConfigSnapshotForSlot(slot) is { } kbm)
                ps.SlotKbmConfigJson = System.Text.Json.JsonSerializer.Serialize(kbm);

            ps.SlotShiftActivatorsJson = InputService.BuildShiftLayerSnapshotJson(slot);
            ps.SlotMenusJson = InputService.BuildMenusSnapshotJson(slot);
            ps.SlotSetExtrasJson = InputService.BuildSlotSetExtrasJson(slot);

            var macros = new List<MacroData>(pad.Macros.Count);
            foreach (var macro in pad.Macros)
                macros.Add(SettingsService.BuildMacroDataForMacro(macro, slot));
            ps.SlotMacrosJson = SettingsService.SerializeMacrosToClipboard(macros.ToArray());

            var perDevice = InputService.BuildPerDeviceSettingsSnapshot(slot, type, raw);
            ps.SlotPerDeviceSettingsJson = System.Text.Json.JsonSerializer.Serialize(
                perDevice ?? Array.Empty<PerDeviceSettingsEntry>());

            return ps.ToJson(type, raw);
        }

        /// <summary>MainWindow.OnPasteSettings from the payload on, by its
        /// calls in its order. The status text and the shaker renderer's
        /// kick are left out: neither changes a setting.</summary>
        private void Paste(int slot, string json)
        {
            var pad = _vm.Pads[slot];
            var ps = PadSetting.FromJson(json, out VirtualControllerType sourceType, out bool sourceRaw);
            Assert.NotNull(ps);

            SlotAppearancePersistence.ApplyClipboardSetting(pad, ps);

            var targetType = pad.OutputType;
            bool targetRaw = IsRaw(targetType);
            bool sameLayout = MappingTranslation.IsSameLayout(sourceType, sourceRaw, targetType, targetRaw);

            InputService.ApplySlotRowsFromClipboard(slot, ps, sourceType, sourceRaw, InputService.SlotLayoutOf(pad));
            if (!string.IsNullOrEmpty(ps.SlotShiftActivatorsJson) && sameLayout)
                InputService.ApplyShiftLayerSnapshotJson(slot, ps.SlotShiftActivatorsJson);
            if (!string.IsNullOrEmpty(ps.SlotMenusJson) && sameLayout)
                InputService.ApplyMenusSnapshotJson(slot, ps.SlotMenusJson);
            if (!string.IsNullOrEmpty(ps.SlotSetExtrasJson))
                InputService.ApplySlotSetExtrasJson(slot, ps.SlotSetExtrasJson, sameLayout);
            MainWindow.ApplySlotMacrosFromClipboard(pad, ps.SlotMacrosJson);

            _input.ApplyPadSettingToCurrentDeviceTranslated(slot, ps,
                sourceType, sourceRaw, targetType, targetRaw);

            if (!string.IsNullOrEmpty(ps.SlotDeviceConfigsJson))
                _settings.ApplyDeviceSlotConfigsToSlot(slot, SlotAppearancePersistence.FilterClipboardDeviceConfigs(ps,
                    System.Text.Json.JsonSerializer.Deserialize<DeviceSlotConfigData[]>(ps.SlotDeviceConfigsJson)));
            if (!string.IsNullOrEmpty(ps.SlotExtendedConfigJson))
                _settings.ApplyExtendedConfigToSlot(slot,
                    System.Text.Json.JsonSerializer.Deserialize<ExtendedSlotConfigData>(ps.SlotExtendedConfigJson));
            if (!string.IsNullOrEmpty(ps.SlotMidiConfigJson))
                _settings.ApplyMidiConfigToSlot(slot,
                    System.Text.Json.JsonSerializer.Deserialize<MidiSlotConfigData>(ps.SlotMidiConfigJson));
            if (!string.IsNullOrEmpty(ps.SlotKbmConfigJson))
                _settings.ApplyKbmConfigToSlot(slot,
                    System.Text.Json.JsonSerializer.Deserialize<KbmSlotConfigData>(ps.SlotKbmConfigJson));

            if (!string.IsNullOrEmpty(ps.SlotPerDeviceSettingsJson))
                _input.ApplyPerDeviceSettingsToSlot(slot,
                    System.Text.Json.JsonSerializer.Deserialize<PerDeviceSettingsEntry[]>(ps.SlotPerDeviceSettingsJson),
                    sourceType, sourceRaw, targetType, targetRaw);

            ReloadCards(pad);
            _settings.MarkDirty();
        }

        /// <summary>MainWindow.OnCopyFrom from the picked entry on, by its
        /// calls in its order. The entry is the one its list builds for the
        /// source slot: the slot's selected device and that device's live
        /// settings.</summary>
        private void CopyFrom(int targetSlot, int sourceSlot)
        {
            _input.FlushAllPadViewModels();

            var source = _vm.Pads[sourceSlot];
            var pad = _vm.Pads[targetSlot];
            Guid donor = source.SelectedMappedDevice.InstanceGuid;
            var entry = new CopyFromDialog.DeviceEntry
            {
                InstanceGuid = donor,
                PadSetting = SettingsManager.FindSettingByInstanceGuidAndSlot(donor, sourceSlot).GetPadSetting(),
                OutputType = source.OutputType,
                IsExtended = IsRaw(source.OutputType),
                SourceSlot = sourceSlot,
            };

            var targetType = pad.OutputType;
            bool targetRaw = IsRaw(targetType);

            if (entry.SourceSlot >= 0 && entry.SourceSlot != pad.PadIndex
                && MappingTranslation.IsSameLayout(entry.OutputType, entry.IsExtended, targetType, targetRaw))
            {
                InputService.ReplaceSlotMappingSet(pad.PadIndex, entry.SourceSlot);
                entry.PadSetting.DeviceScopedMultiSourceRows = null;
            }
            else if (entry.SourceSlot >= 0 && entry.SourceSlot != pad.PadIndex
                && entry.SourceSlot < _vm.Pads.Count)
            {
                InputService.TranslateSlotMappingSet(pad.PadIndex, entry.SourceSlot,
                    InputService.SlotLayoutOf(_vm.Pads[entry.SourceSlot]), InputService.SlotLayoutOf(pad));
                entry.PadSetting.DeviceScopedMultiSourceRows = null;
            }
            else if (entry.SourceSlot >= 0)
            {
                entry.PadSetting.DeviceScopedMultiSourceRows =
                    InputService.ExtractDeviceScopedRowsForSlot(entry.SourceSlot, entry.InstanceGuid);
            }

            Guid? targetDeviceOverride = null;
            lock (SettingsManager.UserSettings.SyncRoot)
            {
                foreach (var u in SettingsManager.UserSettings.Items)
                    if (u != null && u.InstanceGuid == entry.InstanceGuid && u.MapTo == pad.PadIndex)
                    { targetDeviceOverride = entry.InstanceGuid; break; }
            }

            MainWindow.ApplyCopyFromSettings(_input, _settings, pad, entry, targetType, targetRaw, targetDeviceOverride);

            ReloadCards(pad);
            _settings.MarkDirty();
        }

        /// <summary>The reloads both handlers end on.</summary>
        private static void ReloadCards(PadViewModel pad)
        {
            pad.RebuildLayerTabs(SettingsManager.SlotMappingSets[pad.PadIndex]?.ShiftActivators);
            pad.ReloadMenus();
            pad.ReloadRumbleAudio();
            pad.ReloadSocd();
            pad.ReloadKeepAwake();
        }

        private void Transfer(bool copyFrom)
        {
            if (copyFrom) CopyFrom(TargetSlot, SourceSlot);
            else Paste(TargetSlot, Copy(SourceSlot));
        }

        /// <summary>What the autosave timer runs on each tick while settings
        /// are dirty (SettingsService.MarkDirtyOnUiThread), before the file
        /// is written.</summary>
        private void AutosavePush()
        {
            _settings.UpdatePadSettingsFromViewModels();
            _settings.PushUiExtraSourcesIntoSlotMappingSets();
        }

        // Reading the target.

        private static List<MappingRow> BaseRows(int slot)
            => (SettingsManager.SlotMappingSets[slot]?.Rows ?? new List<MappingRow>())
                .Where(r => r != null && (r.LayerMask ?? "Base") == "Base"
                    && r.Sources != null && r.Sources.Count > 0)
                .ToList();

        private static MappingRow BaseRow(int slot, string target)
            => BaseRows(slot).SingleOrDefault(r => r.Target == target);

        private static string Describe(MappingRow row)
            => row.Target + " <- " + string.Join(", ", row.Sources.Select(s =>
                (s.Invert ? "-" : "") + s.Descriptor
                + (string.Equals(s.DeviceGuid, TargetPad.ToString(), StringComparison.OrdinalIgnoreCase) ? " (target pad)"
                    : string.Equals(s.DeviceGuid, SourcePad.ToString(), StringComparison.OrdinalIgnoreCase) ? " (source pad)"
                    : " (" + s.DeviceGuid + ")")));

        private void PrintRows(string title, int slot)
        {
            _output.WriteLine(title);
            var rows = BaseRows(slot);
            if (rows.Count == 0) _output.WriteLine("  (no Base row has a source)");
            foreach (var row in rows) _output.WriteLine("  " + Describe(row));
        }

        /// <summary>One source binding and the target name the documented
        /// translation gives it, null where the target layout has no
        /// counterpart.</summary>
        public sealed record Binding(string Target, string Descriptor, string Expected);

        private static string Translated(string target, VirtualControllerType from, VirtualControllerType to)
        {
            var position = MappingTranslation.GetPosition(target, from, IsRaw(from));
            return position == null ? null : MappingTranslation.GetPropertyName(position, to, IsRaw(to));
        }

        // The docs table's row for these layouts: "gamepad button N lands on
        // raw button N, axis N on raw axis N, and the D-pad on hat 0".
        private static readonly Binding[] XboxOntoExtended =
        {
            new("ButtonA", "Button 0", "RawBtn0"),
            new("ButtonY", "Button 3", "RawBtn3"),
            new("ButtonStart", "Button 7", "RawBtn7"),
            new("ButtonGuide", "Button 10", "RawBtn10"),
            new("DPadUp", "POV 0 Up", "RawPov0Up"),
            new("DPadRight", "POV 0 Right", "RawPov0Right"),
            new("LeftThumbAxisX", "Axis 0", "RawAxis0"),
            new("LeftTrigger", "Axis 2", "RawAxis2"),
            new("RightThumbAxisY", "Axis 4", "RawAxis4"),
            new("RightTrigger", "Axis 5", "RawAxis5"),
        };

        // The same row read the other way. The gamepad order ends at button
        // 16 and axis 5 and has one D-pad, so raw button 17, raw axis 6 and
        // a second hat have no counterpart.
        private static readonly Binding[] ExtendedOntoXbox =
        {
            new("RawBtn0", "Button 0", "ButtonA"),
            new("RawBtn1", "Button 1", "ButtonB"),
            new("RawBtn4", "Button 4", "LeftShoulder"),
            new("RawBtn6", "Button 6", "ButtonBack"),
            new("RawBtn9", "Button 9", "RightThumbButton"),
            new("RawPov0Down", "POV 0 Down", "DPadDown"),
            new("RawAxis1", "Axis 1", "LeftThumbAxisY"),
            new("RawAxis2", "Axis 2", "LeftTrigger"),
            new("RawAxis3", "Axis 3", "RightThumbAxisX"),
            new("RawAxis5", "Axis 5", "RightTrigger"),
            new("RawBtn17", "Button 17", null),
            new("RawAxis6", "Axis 6", null),
            new("RawPov1Up", "POV 1 Up", null),
        };

        // MappingTranslation's MIDI table: button N is note N, axis N is CC
        // N, and MIDI has no D-pad.
        private static readonly Binding[] XboxOntoMidi =
        {
            new("ButtonA", "Button 0", "MidiNote0"),
            new("ButtonB", "Button 1", "MidiNote1"),
            new("ButtonGuide", "Button 10", "MidiNote10"),
            new("LeftThumbAxisX", "Axis 0", "MidiCC0"),
            new("LeftTrigger", "Axis 2", "MidiCC2"),
            new("RightTrigger", "Axis 5", "MidiCC5"),
            new("DPadUp", "POV 0 Up", null),
        };

        // MappingTranslation's Keyboard + Mouse table: the five mouse buttons,
        // then Space, E, R, G, Q, C, the arrow keys for the D-pad, and mouse
        // X, mouse Y, scroll and horizontal scroll for axes 0 to 3.
        private static readonly Binding[] XboxOntoKeyboardMouse =
        {
            new("ButtonA", "Button 0", "KbmMBtn0"),
            new("ButtonB", "Button 1", "KbmMBtn1"),
            new("LeftShoulder", "Button 4", "KbmMBtn4"),
            new("RightShoulder", "Button 5", "KbmKey20"),
            new("ButtonGuide", "Button 10", "KbmKey43"),
            new("ButtonShare", "Button 11", null),
            new("DPadUp", "POV 0 Up", "KbmKey26"),
            new("DPadDown", "POV 0 Down", "KbmKey28"),
            new("LeftThumbAxisX", "Axis 0", "KbmMouseX"),
            new("LeftThumbAxisY", "Axis 1", "KbmMouseY"),
            new("LeftTrigger", "Axis 2", "KbmScroll"),
            new("RightThumbAxisX", "Axis 3", "KbmScrollH"),
            new("RightThumbAxisY", "Axis 4", null),
            new("RightTrigger", "Axis 5", null),
        };

        /// <summary>Puts the bindings on the source slot, moves them to the
        /// target slot through Paste or Copy From, runs the autosave's push
        /// and checks each one against the documented translation.</summary>
        private void CheckTranslation(VirtualControllerType from, VirtualControllerType to,
            Binding[] bindings, bool copyFrom)
        {
            Arrange(from, to);
            SetRows(SourceSlot, bindings.Select(b => Row(b.Target, Source(SourcePad, b.Descriptor))).ToArray());
            PrintRows($"source slot ({from}):", SourceSlot);

            Transfer(copyFrom);
            AutosavePush();
            PrintRows($"target slot ({to}) after {(copyFrom ? "Copy From" : "Paste")} and the autosave push:", TargetSlot);

            var grid = _vm.Pads[TargetSlot].Mappings;
            foreach (var binding in bindings)
            {
                // The expected name is the docs table's, and MappingTranslation's.
                Assert.Equal(binding.Expected, Translated(binding.Target, from, to));
                if (binding.Expected == null) continue;

                var row = BaseRow(TargetSlot, binding.Expected);
                Assert.True(row != null,
                    $"{binding.Target} <- {binding.Descriptor} did not reach {binding.Expected} on the target slot");
                var source = Assert.Single(row.Sources);
                Assert.Equal(binding.Descriptor, source.Descriptor);
                Assert.Equal(TargetPad.ToString(), source.DeviceGuid, ignoreCase: true);

                // Where the grid has the row, it shows the binding too, read
                // as a half axis where the row's source is (a trigger on a
                // two-sided axis, ATriggerOnATwoSidedAxisRestsAtTheMiddle).
                var item = grid.SingleOrDefault(m => m.TargetSettingName == binding.Expected);
                if (item != null)
                {
                    Assert.Equal(binding.Descriptor, PadForge.Engine.Common.Mapping.SourceCoercion.StripLegacyPrefix(
                        item.SourceDescriptor, out _, out bool half));
                    Assert.Equal(source.HalfAxis, half);
                }
            }

            // A binding with no counterpart is dropped, never carried under
            // its source layout's name.
            foreach (var binding in bindings.Where(b => b.Expected == null))
                Assert.DoesNotContain(BaseRows(TargetSlot), r => r.Sources.Any(s => s.Descriptor == binding.Descriptor));
        }

        // The control: one layout on both sides.

        /// <summary>The harness sees a copy. On a shared layout both Paste
        /// and Copy From install the copied rows, each source moved to the
        /// target slot's pad of the same model.</summary>
        [Theory]
        [InlineData(Xbox, false)]
        [InlineData(Xbox, true)]
        [InlineData(PlayStation, false)]
        [InlineData(PlayStation, true)]
        public void OnASharedLayoutTheRowsAreCopied(VirtualControllerType targetType, bool copyFrom)
        {
            Arrange(Xbox, targetType);
            SetRows(SourceSlot,
                Row("ButtonA", Source(SourcePad, "Button 0")),
                Row("LeftThumbAxisX", Source(SourcePad, "Axis 0")),
                Row("DPadUp", Source(SourcePad, "POV 0 Up")));
            SetRows(TargetSlot, Row("ButtonA", Source(TargetPad, "Button 9")));
            Assert.True(MappingTranslation.IsSameLayout(Xbox, false, targetType, false));

            Transfer(copyFrom);
            AutosavePush();
            PrintRows($"target slot ({targetType}) after {(copyFrom ? "Copy From" : "Paste")} and the autosave push:", TargetSlot);

            foreach (var (target, descriptor) in new[] { ("ButtonA", "Button 0"), ("LeftThumbAxisX", "Axis 0"), ("DPadUp", "POV 0 Up") })
            {
                var source = Assert.Single(BaseRow(TargetSlot, target).Sources);
                Assert.Equal(descriptor, source.Descriptor);
                Assert.Equal(TargetPad.ToString(), source.DeviceGuid, ignoreCase: true);
                Assert.Equal(descriptor, _vm.Pads[TargetSlot].Mappings.Single(m => m.TargetSettingName == target).SourceDescriptor);
            }
        }

        // The claim: another layout on the target.

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AnXboxSlotLandsOnAnExtendedSlotByPosition(bool copyFrom)
            => CheckTranslation(Xbox, Extended, XboxOntoExtended, copyFrom);

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AnExtendedSlotLandsOnAnXboxSlotByPosition(bool copyFrom)
            => CheckTranslation(Extended, Xbox, ExtendedOntoXbox, copyFrom);

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AnXboxSlotLandsOnAMidiSlotByPosition(bool copyFrom)
            => CheckTranslation(Xbox, Midi, XboxOntoMidi, copyFrom);

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AnXboxSlotLandsOnAKeyboardMouseSlotByPosition(bool copyFrom)
            => CheckTranslation(Xbox, KeyboardMouse, XboxOntoKeyboardMouse, copyFrom);

        /// <summary>Where the translated binding goes. The copy writes it
        /// into the receiving pad's own settings (PadSetting.CopyFromTranslated),
        /// and the grid, the engine and the file read the slot's rows. The
        /// rows must carry it once the autosave has pushed, and the pad's
        /// settings must agree with them.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheTranslatedBindingIsInTheRowsOnceTheAutosaveHasPushed(bool copyFrom)
        {
            Arrange(Xbox, Extended);
            SetRows(SourceSlot, Row("ButtonA", Source(SourcePad, "Button 0")));
            SetRows(TargetSlot, Row("RawBtn5", Source(TargetPad, "Button 9")));
            var targetSetting = SettingsManager.FindSettingByInstanceGuidAndSlot(TargetPad, TargetSlot).GetPadSetting();

            Transfer(copyFrom);
            _output.WriteLine($"after {(copyFrom ? "Copy From" : "Paste")}, before the autosave push:");
            _output.WriteLine($"  target pad's own setting RawBtn0 = \"{targetSetting.GetRawMapping("RawBtn0")}\"");
            PrintRows("  target slot rows:", TargetSlot);

            AutosavePush();
            _output.WriteLine("after the autosave push:");
            _output.WriteLine($"  target pad's own setting RawBtn0 = \"{targetSetting.GetRawMapping("RawBtn0")}\"");
            PrintRows("  target slot rows:", TargetSlot);

            var row = BaseRow(TargetSlot, "RawBtn0");
            Assert.True(row != null, "the translated binding never reached the target slot's rows");
            Assert.Equal("Button 0", Assert.Single(row.Sources).Descriptor);
            Assert.Equal("Button 0", targetSetting.GetRawMapping("RawBtn0"));
        }

        // The docs table, pair by pair.

        /// <summary>"Xbox to PlayStation: A to Cross, B to Circle, X to
        /// Square, Y to Triangle, LB to L1, RB to R1", and back. The two
        /// share one layout and its names, so the table is copied whole and
        /// each binding shows under the other family's label.</summary>
        [Theory]
        [InlineData(Xbox, PlayStation, false)]
        [InlineData(Xbox, PlayStation, true)]
        [InlineData(PlayStation, Xbox, false)]
        [InlineData(PlayStation, Xbox, true)]
        public void XboxAndPlayStationBindingsKeepTheirPlaces(
            VirtualControllerType from, VirtualControllerType to, bool copyFrom)
        {
            string[] targets = { "ButtonA", "ButtonB", "ButtonX", "ButtonY", "LeftShoulder", "RightShoulder" };
            string[] playStationLabels = { "\u2715", "\u25CB", "\u25FB", "\u25B3", "L1", "R1" };
            string[] xboxLabels = { "A", "B", "X", "Y" };
            Arrange(from, to);
            SetRows(SourceSlot, targets.Select((t, i) => Row(t, Source(SourcePad, $"Button {i}"))).ToArray());

            Transfer(copyFrom);
            AutosavePush();

            for (int i = 0; i < targets.Length; i++)
            {
                Assert.Equal(targets[i], Translated(targets[i], from, to));
                var source = Assert.Single(BaseRow(TargetSlot, targets[i]).Sources);
                Assert.Equal($"Button {i}", source.Descriptor);
                Assert.Equal(TargetPad.ToString(), source.DeviceGuid, ignoreCase: true);

                var item = GridItem(TargetSlot, targets[i]);
                Assert.Equal($"Button {i}", item.SourceDescriptor);
                if (to == PlayStation) Assert.Equal(playStationLabels[i], item.TargetLabel);
                else if (i < xboxLabels.Length) Assert.Equal(xboxLabels[i], item.TargetLabel);
            }
        }

        // Slots whose raw positions carry gamepad roles: the two Switch pads
        // and the three Valve pads. Their rows land by role, the way the
        // automap binds a pad there, through NintendoPreviewMap's table.

        /// <summary>Puts a slot on a profile and reloads its grid.</summary>
        private void Profile(int slot, string profile)
        {
            var pad = _vm.Pads[slot];
            if (pad.ProfileId != profile)
            {
                pad.ProfileId = profile;
                pad.RebuildMappings();
            }
            Assert.Equal(profile, pad.ProfileId);
            Assert.True(pad.MappingsViewLoaded);
        }

        /// <summary>The raw target the table gives a gamepad target on a
        /// lettered wire: the first of its position's roles the wire has, in
        /// the wire's own table.</summary>
        private static string ByTable(string gamepadTarget, string profile)
        {
            var position = NintendoPreviewMap.GamepadPositions.Single(p => p.Target == gamepadTarget);
            foreach (string role in position.Roles(NintendoPreviewMap.IsValve(profile)))
                if (NintendoPreviewMap.ToRaw(role, profile) is { } raw) return raw;
            return null;
        }

        /// <summary>The table against the Switch Pro's wire, name by name:
        /// south to south, Back on Minus, Start on Plus, the stick clicks on
        /// the stick clicks, Guide on Home, Share on Capture, the triggers on
        /// ZL and ZR, the right stick on raw axes 2 and 3, and the D-pad on
        /// the hat.</summary>
        [Theory]
        [InlineData("ButtonA", "RawBtn0", "ButtonB")]
        [InlineData("ButtonB", "RawBtn1", "ButtonA")]
        [InlineData("ButtonX", "RawBtn2", "ButtonY")]
        [InlineData("ButtonY", "RawBtn3", "ButtonX")]
        [InlineData("LeftShoulder", "RawBtn4", "LeftShoulder")]
        [InlineData("ButtonBack", "RawBtn8", "ButtonBack")]
        [InlineData("ButtonStart", "RawBtn9", "ButtonStart")]
        [InlineData("LeftThumbButton", "RawBtn10", "LeftThumbButton")]
        [InlineData("RightThumbButton", "RawBtn11", "RightThumbButton")]
        [InlineData("ButtonGuide", "RawBtn12", "ButtonGuide")]
        [InlineData("ButtonShare", "RawBtn13", "ButtonShare")]
        [InlineData("LeftTrigger", "RawBtn6", "LeftTrigger")]
        [InlineData("RightTrigger", "RawBtn7", "RightTrigger")]
        [InlineData("LeftThumbAxisX", "RawAxis0", "LeftThumbAxisX")]
        [InlineData("RightThumbAxisX", "RawAxis2", "RightThumbAxisX")]
        [InlineData("RightThumbAxisY", "RawAxis3", "RightThumbAxisY")]
        [InlineData("DPadUp", "RawPov0Up", "DPadUp")]
        public void TheTableGivesTheSwitchProEachControlByItsRole(string gamepad, string raw, string role)
        {
            Assert.Equal(raw, ByTable(gamepad, "switch-pro"));
            Assert.Equal(role, NintendoPreviewMap.ToPreview(raw, "switch-pro"));
        }

        /// <summary>A gamepad slot lands on a lettered slot by role: each
        /// binding goes to the role its position takes on that wire, as the
        /// automap binds a pad there. A position the wire has no role for is
        /// dropped. Every expected name is the table's.</summary>
        [Theory]
        [InlineData(Xbox, Nintendo, "switch-pro", false)]
        [InlineData(Xbox, Nintendo, "switch-pro", true)]
        [InlineData(PlayStation, Nintendo, "switch-pro", false)]
        [InlineData(PlayStation, Nintendo, "switch-pro", true)]
        [InlineData(Xbox, Nintendo, "switch2-pro-controller", false)]
        [InlineData(Xbox, Nintendo, "switch2-pro-controller", true)]
        [InlineData(Xbox, Extended, "steam-deck", false)]
        [InlineData(Xbox, Extended, "steam-deck", true)]
        [InlineData(PlayStation, Extended, "steam-controller", false)]
        [InlineData(PlayStation, Extended, "steam-controller", true)]
        [InlineData(Xbox, Extended, "steam-controller-2", false)]
        [InlineData(Xbox, Extended, "steam-controller-2", true)]
        public void AGamepadSlotLandsOnALetteredSlotByRole(VirtualControllerType from, VirtualControllerType to,
            string profile, bool copyFrom)
        {
            Arrange(from, to);
            Profile(TargetSlot, profile);
            var targets = _vm.Pads[SourceSlot].Mappings.Select(m => m.TargetSettingName)
                .Where(t => NintendoPreviewMap.GamepadPositions.Any(p => p.Target == t)).ToList();
            SetRows(SourceSlot, targets.Select((t, i) => Row(t, Source(SourcePad, $"Button {i}"))).ToArray());

            Transfer(copyFrom);
            AutosavePush();

            int landed = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                string descriptor = $"Button {i}";
                string expected = ByTable(targets[i], profile);
                if (expected == null)
                {
                    Assert.DoesNotContain(BaseRows(TargetSlot), r => r.Sources.Any(s => s.Descriptor == descriptor));
                    continue;
                }
                var row = BaseRow(TargetSlot, expected);
                Assert.True(row != null, $"{targets[i]} <- {descriptor} did not reach {expected}");
                Assert.Equal(descriptor, Assert.Single(row.Sources).Descriptor);
                landed++;
            }
            Assert.True(landed >= 20, landed + " of " + targets.Count + " bindings landed");
        }

        /// <summary>The other way, by the same table: each raw binding goes to
        /// the gamepad target whose position takes its role, the one the
        /// gamepad slot shows where two share a position (Share on an Xbox
        /// pad, the mic button on a DualSense). A raw position with no
        /// gamepad role is dropped, never carried by its index.</summary>
        [Theory]
        [InlineData(Nintendo, "switch-pro", Xbox, false)]
        [InlineData(Nintendo, "switch-pro", Xbox, true)]
        [InlineData(Nintendo, "switch-pro", PlayStation, false)]
        [InlineData(Nintendo, "switch-pro", PlayStation, true)]
        [InlineData(Nintendo, "switch2-pro-controller", Xbox, false)]
        [InlineData(Nintendo, "switch2-pro-controller", Xbox, true)]
        [InlineData(Extended, "steam-deck", Xbox, false)]
        [InlineData(Extended, "steam-deck", Xbox, true)]
        [InlineData(Extended, "steam-controller", PlayStation, false)]
        [InlineData(Extended, "steam-controller", PlayStation, true)]
        [InlineData(Extended, "steam-controller-2", Xbox, false)]
        [InlineData(Extended, "steam-controller-2", Xbox, true)]
        public void ALetteredSlotLandsOnAGamepadSlotByRole(VirtualControllerType from, string profile,
            VirtualControllerType to, bool copyFrom)
        {
            Arrange(from, to);
            Profile(SourceSlot, profile);
            var raws = _vm.Pads[SourceSlot].Mappings.Select(m => m.TargetSettingName)
                .Where(t => t.StartsWith("Raw", StringComparison.Ordinal)).ToList();
            SetRows(SourceSlot, raws.Select((t, i) => Row(t, Source(SourcePad, $"Button {i}"))).ToArray());
            var shown = _vm.Pads[TargetSlot].Mappings.Select(m => m.TargetSettingName).ToHashSet();

            Transfer(copyFrom);
            AutosavePush();

            int landed = 0;
            for (int i = 0; i < raws.Count; i++)
            {
                string descriptor = $"Button {i}";
                var candidates = NintendoPreviewMap.GamepadPositions
                    .Where(p => p.Target != null && ByTable(p.Target, profile) == raws[i])
                    .Select(p => p.Target).Distinct().ToList();
                string expected = candidates.FirstOrDefault(shown.Contains) ?? candidates.FirstOrDefault();
                if (expected == null)
                {
                    Assert.DoesNotContain(BaseRows(TargetSlot), r => r.Sources.Any(s => s.Descriptor == descriptor));
                    continue;
                }
                var row = BaseRow(TargetSlot, expected);
                Assert.True(row != null, $"{raws[i]} <- {descriptor} did not reach {expected}");
                Assert.Equal(descriptor, Assert.Single(row.Sources).Descriptor);
                landed++;
            }
            Assert.True(landed >= 18, landed + " of " + raws.Count + " bindings landed");
        }

        /// <summary>Capture is the Share button on an Xbox slot and the mic
        /// button on a DualSense, the target each one shows.</summary>
        [Theory]
        [InlineData(Xbox, "ButtonShare", false)]
        [InlineData(Xbox, "ButtonShare", true)]
        [InlineData(PlayStation, "ButtonMute", false)]
        [InlineData(PlayStation, "ButtonMute", true)]
        public void CaptureLandsOnTheMiscButtonTheGamepadSlotShows(VirtualControllerType to, string expected, bool copyFrom)
        {
            Arrange(Nintendo, to);
            string capture = ByTable("ButtonShare", "switch-pro");
            Assert.Equal(capture, ByTable("ButtonMute", "switch-pro"));
            SetRows(SourceSlot, Row(capture, Source(SourcePad, "Button 11")));

            Transfer(copyFrom);
            AutosavePush();

            Assert.Equal("Button 11", Assert.Single(BaseRow(TargetSlot, expected).Sources).Descriptor);
        }

        /// <summary>A MIDI slot's note N and CC N are the gamepad order's
        /// button N and axis N, so they reach a lettered slot through the same
        /// roles: note 6 is Back and lands on Minus, CC 3 is the right stick's
        /// X axis. Notes 11 and 12 stand for the Share button and the mic
        /// button, one position, and the first keeps it.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AMidiSlotLandsOnASwitchProByRole(bool copyFrom)
        {
            Arrange(Midi, Nintendo);
            SetRows(SourceSlot,
                Row("MidiNote6", Source(SourcePad, "Button 6")),
                Row("MidiCC3", Source(SourcePad, "Axis 3")),
                Row("MidiNote11", Source(SourcePad, "Button 11")),
                Row("MidiNote12", Source(SourcePad, "Button 12")));

            Transfer(copyFrom);
            AutosavePush();

            Assert.Equal("Button 6", Assert.Single(BaseRow(TargetSlot, ByTable("ButtonBack", "switch-pro")).Sources).Descriptor);
            Assert.Equal("Axis 3", Assert.Single(BaseRow(TargetSlot, ByTable("RightThumbAxisX", "switch-pro")).Sources).Descriptor);
            Assert.Equal("Button 11", Assert.Single(BaseRow(TargetSlot, ByTable("ButtonShare", "switch-pro")).Sources).Descriptor);
            Assert.DoesNotContain(SettingsManager.SlotMappingSets[TargetSlot].Rows,
                r => r.Sources.Any(s => s.Descriptor == "Button 12"));
        }

        /// <summary>A DualSense Edge's paddles and Fn buttons are SDL's paddle
        /// positions: a Steam Deck takes them on R4, L4, R5 and L5, and the
        /// 2015 pad takes the paddles on its grips and has nothing for the Fn
        /// buttons.</summary>
        [Theory]
        [InlineData("steam-deck", false)]
        [InlineData("steam-deck", true)]
        [InlineData("steam-controller", false)]
        [InlineData("steam-controller", true)]
        public void AnEdgesPaddlesLandOnAValvePadsRearButtons(string profile, bool copyFrom)
        {
            Arrange(PlayStation, Extended);
            Profile(SourceSlot, "dualsense-edge-composite");
            Profile(TargetSlot, profile);
            SetRows(SourceSlot,
                Row("RightPaddle", Source(SourcePad, "Button 12")),
                Row("LeftPaddle", Source(SourcePad, "Button 13")),
                Row("RightFunction", Source(SourcePad, "Button 14")),
                Row("LeftFunction", Source(SourcePad, "Button 15")));

            Transfer(copyFrom);
            AutosavePush();

            // The table's names, checked against the wire's own roles.
            string On(string role) => "RawBtn" + NintendoPreviewMap.IndexOf(profile, role);
            bool deck = profile == "steam-deck";
            Assert.Equal(On(deck ? "Paddle1" : "RightGrip"), ByTable("RightPaddle", profile));
            Assert.Equal(On(deck ? "Paddle2" : "LeftGrip"), ByTable("LeftPaddle", profile));
            Assert.Equal(deck ? On("Paddle3") : null, ByTable("RightFunction", profile));
            Assert.Equal(deck ? On("Paddle4") : null, ByTable("LeftFunction", profile));

            string[] targets = { "RightPaddle", "LeftPaddle", "RightFunction", "LeftFunction" };
            for (int i = 0; i < targets.Length; i++)
            {
                string descriptor = "Button " + (12 + i);
                string expected = ByTable(targets[i], profile);
                if (expected == null)
                    Assert.DoesNotContain(SettingsManager.SlotMappingSets[TargetSlot].Rows,
                        r => r.Sources.Any(s => s.Descriptor == descriptor));
                else
                    Assert.Equal(descriptor, Assert.Single(BaseRow(TargetSlot, expected).Sources).Descriptor);
            }
        }

        /// <summary>A Paste reads the copied slot's raw rows by the wire its
        /// profile names, which the payload carries: Minus is raw button 8 on
        /// a Switch Pro and 14 on a Switch 2 Pro. A payload from before the
        /// profile rode along reads a Nintendo slot as a Switch Pro, the
        /// type's default wire, where raw button 14 has no role.</summary>
        [Fact]
        public void APasteReadsTheCopiedSlotsWireFromThePayload()
        {
            Arrange(Nintendo, Xbox);
            Profile(SourceSlot, "switch2-pro-controller");
            string minus = ByTable("ButtonBack", "switch2-pro-controller");
            Assert.Equal("RawBtn14", minus);
            Assert.DoesNotContain(NintendoPreviewMap.GamepadPositions,
                p => p.Target != null && ByTable(p.Target, "switch-pro") == minus);
            SetRows(SourceSlot, Row(minus, Source(SourcePad, "Button 6")));
            string json = Copy(SourceSlot);
            Assert.Contains("switch2-pro-controller", json);

            Paste(TargetSlot, json);
            AutosavePush();
            Assert.Equal("Button 6", Assert.Single(BaseRow(TargetSlot, "ButtonBack").Sources).Descriptor);

            var old = PadSetting.FromJson(json, out VirtualControllerType type, out bool raw);
            old.SlotProfileId = null;
            SetRows(TargetSlot);
            Paste(TargetSlot, old.ToJson(type, raw));
            AutosavePush();
            Assert.DoesNotContain(BaseRows(TargetSlot), r => r.Sources.Any(s => s.Descriptor == "Button 6"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void APlayStationSlotLandsOnAnExtendedSlotByPosition(bool copyFrom)
            => CheckTranslation(PlayStation, Extended, XboxOntoExtended, copyFrom);

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AnExtendedSlotLandsOnAPlayStationSlotByPosition(bool copyFrom)
            => CheckTranslation(Extended, PlayStation, ExtendedOntoXbox, copyFrom);

        /// <summary>"Nintendo and Extended share a raw layout, so rows between
        /// them copy index for index."</summary>
        [Theory]
        [InlineData(Nintendo, Extended, false)]
        [InlineData(Nintendo, Extended, true)]
        [InlineData(Extended, Nintendo, false)]
        [InlineData(Extended, Nintendo, true)]
        public void NintendoAndExtendedRowsCopyIndexForIndex(
            VirtualControllerType from, VirtualControllerType to, bool copyFrom)
        {
            string[] targets = { "RawBtn0", "RawBtn5", "RawBtn13", "RawAxis0", "RawAxis2", "RawPov0Up" };
            Arrange(from, to);
            SetRows(SourceSlot, targets.Select((t, i) => Row(t, Source(SourcePad, $"Button {i}"))).ToArray());
            Assert.True(MappingTranslation.IsSameLayout(from, true, to, true));

            Transfer(copyFrom);
            AutosavePush();

            for (int i = 0; i < targets.Length; i++)
            {
                var source = Assert.Single(BaseRow(TargetSlot, targets[i]).Sources);
                Assert.Equal($"Button {i}", source.Descriptor);
                Assert.Equal(TargetPad.ToString(), source.DeviceGuid, ignoreCase: true);
            }
        }

        // MappingTranslation's VR table: A and B on the right hand, X and Y
        // on the left, the shoulders on the grip clicks, Back and Start on
        // System, the stick clicks on the stick clicks, and the gamepad's
        // six axes in their order. A VR pair has no D-pad, its buttons end
        // at position 15, and its two grips are axes a gamepad has no place
        // for.
        private static readonly Binding[] XboxOntoVr =
        {
            new("ButtonA", "Button 0", "VrRA"),
            new("ButtonB", "Button 1", "VrRB"),
            new("ButtonX", "Button 2", "VrLA"),
            new("ButtonY", "Button 3", "VrLB"),
            new("LeftShoulder", "Button 4", "VrLGripClick"),
            new("ButtonStart", "Button 7", "VrRSystem"),
            new("RightThumbButton", "Button 9", "VrRStickClick"),
            new("LeftThumbAxisX", "Axis 0", "VrLStickX"),
            new("LeftTrigger", "Axis 2", "VrLTrigger"),
            new("RightThumbAxisY", "Axis 4", "VrRStickY"),
            new("RightTrigger", "Axis 5", "VrRTrigger"),
            new("RightFunction", "Button 16", null),
            new("DPadUp", "POV 0 Up", null),
        };

        private static readonly Binding[] VrOntoXbox =
        {
            new("VrRA", "Button 0", "ButtonA"),
            new("VrLA", "Button 2", "ButtonX"),
            new("VrRGripClick", "Button 5", "RightShoulder"),
            new("VrLStickClick", "Button 8", "LeftThumbButton"),
            new("VrLStickY", "Axis 1", "LeftThumbAxisY"),
            new("VrLTrigger", "Axis 2", "LeftTrigger"),
            new("VrRStickX", "Axis 3", "RightThumbAxisX"),
            new("VrRTrigger", "Axis 5", "RightTrigger"),
            new("VrLGrip", "Axis 6", null),
        };

        // The MIDI and Keyboard + Mouse tables read the other way. MIDI names
        // any note and any CC, so what is dropped is what the gamepad order
        // has no place for. A key off the Keyboard + Mouse table has no
        // position.
        private static readonly Binding[] MidiOntoXbox =
        {
            new("MidiNote0", "Button 0", "ButtonA"),
            new("MidiNote9", "Button 9", "RightThumbButton"),
            new("MidiNote16", "Button 16", "RightFunction"),
            new("MidiNote17", "Button 17", null),
            new("MidiCC0", "Axis 0", "LeftThumbAxisX"),
            new("MidiCC2", "Axis 2", "LeftTrigger"),
            new("MidiCC5", "Axis 5", "RightTrigger"),
            new("MidiCC6", "Axis 6", null),
        };

        private static readonly Binding[] KeyboardMouseOntoXbox =
        {
            new("KbmMBtn0", "Button 0", "ButtonA"),
            new("KbmMBtn4", "Button 4", "LeftShoulder"),
            new("KbmKey20", "Button 5", "RightShoulder"),
            new("KbmKey43", "Button 10", "ButtonGuide"),
            new("KbmKey26", "POV 0 Up", "DPadUp"),
            new("KbmKey27", "POV 0 Right", "DPadRight"),
            new("KbmMouseX", "Axis 0", "LeftThumbAxisX"),
            new("KbmMouseY", "Axis 1", "LeftThumbAxisY"),
            new("KbmScroll", "Axis 2", "LeftTrigger"),
            new("KbmScrollH", "Axis 3", "RightThumbAxisX"),
            new("KbmKey57", "Button 12", null),
        };

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AMidiSlotLandsOnAnXboxSlotByPosition(bool copyFrom)
            => CheckTranslation(Midi, Xbox, MidiOntoXbox, copyFrom);

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AKeyboardMouseSlotLandsOnAnXboxSlotByPosition(bool copyFrom)
            => CheckTranslation(KeyboardMouse, Xbox, KeyboardMouseOntoXbox, copyFrom);

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AnXboxSlotLandsOnAVrSlotByPosition(bool copyFrom)
            => CheckTranslation(Xbox, Vr, XboxOntoVr, copyFrom);

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AVrSlotLandsOnAnXboxSlotByPosition(bool copyFrom)
            => CheckTranslation(Vr, Xbox, VrOntoXbox, copyFrom);

        // Each part of the target slot, across layouts.

        private MappingItem GridItem(int slot, string target)
            => _vm.Pads[slot].Mappings.Single(m => m.TargetSettingName == target);

        private static string[] Descriptors(MappingRow row)
            => row.Sources.Select(s => s.Descriptor).ToArray();

        /// <summary>Reloads a slot's grid and layer tabs after its set was
        /// edited in place.</summary>
        private void Reload(int slot)
        {
            _vm.Pads[slot].RebuildLayerTabs(SettingsManager.SlotMappingSets[slot].ShiftActivators);
            InputService.RefreshMappingsToViewModel(_vm.Pads[slot]);
        }

        /// <summary>The Base rows are replaced. A target row the copied slot
        /// has no binding for goes, as on a shared layout, and so does one
        /// the position table cannot name.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheTargetsOwnBaseRowsAreReplaced(bool copyFrom)
        {
            Arrange(Xbox, Extended);
            SetRows(SourceSlot, Row("ButtonA", Source(SourcePad, "Button 0")));
            SetRows(TargetSlot,
                Row("RawBtn0", Source(TargetPad, "Button 8")),
                Row("RawBtn5", Source(TargetPad, "Button 9")),
                Row("RawPov1Up", Source(TargetPad, "POV 1 Up")));
            Assert.Null(MappingTranslation.GetPosition("RawPov1Up", Extended, true));

            Transfer(copyFrom);
            AutosavePush();

            var row = Assert.Single(BaseRows(TargetSlot));
            Assert.Equal("RawBtn0", row.Target);
            Assert.Equal("Button 0", Assert.Single(row.Sources).Descriptor);
            Assert.DoesNotContain(SettingsManager.SlotMappingSets[TargetSlot].Rows, r => r.Target == "RawPov1Up");
        }

        // Rows the position table does not place.

        private const string Gyro = MappingSetMigrator.MotionGyroTarget;
        private const string Accel = MappingSetMigrator.MotionAccelTarget;
        private const string Pitch = MappingSetMigrator.MotionPitchTarget;
        private const string Yaw = MappingSetMigrator.MotionYawTarget;

        /// <summary>A touchpad or a motion row has no position. Where the
        /// target slot has no row of its name it is dropped, here on a plain
        /// Extended slot, as the combined D-pad row is everywhere. Copied the
        /// other way, the slot brings none, and a PlayStation target's own
        /// went with the rest of its Base.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ARowOffTheTableIsDroppedWhereTheTargetHasNoRowOfItsName(bool copyFrom)
        {
            Arrange(PlayStation, Extended);
            SetRows(SourceSlot,
                Row("ButtonB", Source(SourcePad, "Button 1")),
                Row("TouchpadX1", Source(SourcePad, "Touchpad 0 Finger 0 X")),
                Row(Gyro, Source(SourcePad, MappingSetMigrator.MotionGyroSourceDescriptor)),
                Row(Pitch, Source(SourcePad, "Axis 1")),
                Row("DPad", Source(SourcePad, "POV 0")));
            Assert.DoesNotContain(InputService.SlotLayoutOf(_vm.Pads[TargetSlot]).Grid,
                name => MappingTranslation.GetPosition(name, Extended, true) == null);
            Transfer(copyFrom);
            AutosavePush();
            Assert.Equal("RawBtn1", Assert.Single(BaseRows(TargetSlot)).Target);

            Arrange(Extended, PlayStation);
            SetRows(SourceSlot, Row("RawBtn1", Source(SourcePad, "Button 1")));
            SetRows(TargetSlot,
                Row("ButtonA", Source(TargetPad, "Button 9")),
                Row("TouchpadX1", Source(TargetPad, "Touchpad 0 Finger 0 X")),
                Row(Gyro, Source(TargetPad, MappingSetMigrator.MotionGyroSourceDescriptor)));
            Transfer(copyFrom);
            AutosavePush();
            Assert.Equal("ButtonB", Assert.Single(BaseRows(TargetSlot)).Target);
        }

        /// <summary>A PlayStation slot and a Nintendo slot both have the
        /// motion rows, under one name on each. They cross under it with
        /// their sources and settings, as on a shared layout, and the
        /// target's own go with the rest of its Base.</summary>
        [Theory]
        [InlineData(PlayStation, Nintendo, false)]
        [InlineData(PlayStation, Nintendo, true)]
        [InlineData(Nintendo, PlayStation, false)]
        [InlineData(Nintendo, PlayStation, true)]
        public void MotionRowsCrossUnderTheirOwnNames(
            VirtualControllerType from, VirtualControllerType to, bool copyFrom)
        {
            Arrange(from, to);
            var lean = Row(Pitch, Legs(SourcePad, "Button 4", "Button 5"));
            lean.MotionResponse = MappingRow.MotionResponseAngle;
            lean.MotionAngle = 45;
            SetRows(SourceSlot,
                Row(Gyro, Source(SourcePad, MappingSetMigrator.MotionGyroSourceDescriptor)),
                lean);
            SetRows(TargetSlot,
                Row(Accel, Source(TargetPad, MappingSetMigrator.MotionAccelSourceDescriptor)),
                Row(Yaw, Source(TargetPad, "Axis 3")));
            Assert.Null(MappingTranslation.GetPosition(Gyro, from, IsRaw(from)));
            Assert.Null(MappingTranslation.GetPosition(Pitch, from, IsRaw(from)));
            var grid = InputService.SlotLayoutOf(_vm.Pads[TargetSlot]).Grid;
            Assert.Contains(Gyro, grid);
            Assert.Contains(Pitch, grid);
            Assert.Null(MappingTranslation.GetPosition(Gyro, to, IsRaw(to)));
            Assert.Null(MappingTranslation.GetPosition(Pitch, to, IsRaw(to)));

            Transfer(copyFrom);
            AutosavePush();

            var gyro = Assert.Single(BaseRow(TargetSlot, Gyro).Sources);
            Assert.Equal(MappingSetMigrator.MotionGyroSourceDescriptor, gyro.Descriptor);
            Assert.Equal(TargetPad.ToString(), gyro.DeviceGuid, ignoreCase: true);
            var pitch = BaseRow(TargetSlot, Pitch);
            Assert.Equal(new[] { "Button 4", "Button 5" }, Descriptors(pitch));
            Assert.True(pitch.Sources[1].Invert);
            Assert.Equal(MappingRow.MotionResponseAngle, pitch.MotionResponse);
            Assert.Equal(45, pitch.MotionAngle);

            Assert.Null(BaseRow(TargetSlot, Accel));
            Assert.Null(BaseRow(TargetSlot, Yaw));
        }

        /// <summary>A Motion Gyro row whose only controller has no
        /// counterpart on the target slot is left out, as on a shared layout.
        /// An empty one would switch the channel off, and with none the
        /// motion auto-map fills the row for the slot's own devices.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AMotionRowEmptiedByTheCopyIsLeftOut(bool copyFrom)
        {
            Arrange(PlayStation, Nintendo);
            AddDevice(KeysOnSource, KeysModel, SourceSlot);
            _input.RefreshDeviceList();
            SetRows(SourceSlot,
                Row("ButtonA", Source(SourcePad, "Button 0")),
                Row(Gyro, Source(KeysOnSource, MappingSetMigrator.MotionGyroSourceDescriptor)));
            SetRows(TargetSlot, Row(Gyro, Source(TargetPad, MappingSetMigrator.MotionGyroSourceDescriptor)));

            Transfer(copyFrom);
            Assert.DoesNotContain(SettingsManager.SlotMappingSets[TargetSlot].Rows, r => r.Target == Gyro);
            AutosavePush();

            Assert.Equal("Button 0", Assert.Single(BaseRow(TargetSlot, "RawBtn0").Sources).Descriptor);
            Assert.DoesNotContain(SettingsManager.SlotMappingSets[TargetSlot].Rows, r => r.Target == Gyro);
        }

        /// <summary>A Steam Deck slot is an Extended slot with the two
        /// trackpads as touchpad rows, the names a PlayStation slot's
        /// touchpad has. Those cross. The Deck's pad clicks are raw buttons,
        /// so it has no row for the PlayStation touchpad's click, which is
        /// dropped.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TouchpadRowsCrossBetweenAPlayStationSlotAndAValveOne(bool copyFrom)
        {
            Arrange(PlayStation, Extended);
            var deck = _vm.Pads[TargetSlot];
            deck.ProfileId = "steam-deck";
            deck.RebuildMappings();
            Assert.True(deck.MappingsViewLoaded);
            Assert.Contains(deck.Mappings, m => m.TargetSettingName == "TouchpadX1");
            Assert.DoesNotContain(deck.Mappings, m => m.TargetSettingName == "TouchpadClick");
            SetRows(SourceSlot,
                Row("ButtonA", Source(SourcePad, "Button 0")),
                Row("TouchpadX1", Source(SourcePad, "Axis 3")),
                Row("TouchpadClick", Source(SourcePad, "Button 13")));
            SetRows(TargetSlot, Row("TouchpadY1", Source(TargetPad, "Axis 4")));

            Transfer(copyFrom);
            AutosavePush();

            Assert.Equal("Button 0", Assert.Single(BaseRow(TargetSlot, "RawBtn0").Sources).Descriptor);
            var pad = Assert.Single(BaseRow(TargetSlot, "TouchpadX1").Sources);
            Assert.Equal("Axis 3", pad.Descriptor);
            Assert.Equal(TargetPad.ToString(), pad.DeviceGuid, ignoreCase: true);
            Assert.Equal("Axis 3", GridItem(TargetSlot, "TouchpadX1").SourceDescriptor);
            Assert.Null(BaseRow(TargetSlot, "TouchpadY1"));
            Assert.DoesNotContain(SettingsManager.SlotMappingSets[TargetSlot].Rows, r => r.Target == "TouchpadClick");
        }

        /// <summary>A clean slot copied onto a busy one of another layout
        /// leaves a clean Base.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ACleanSlotLeavesACleanBase(bool copyFrom)
        {
            Arrange(Xbox, Extended);
            SetRows(TargetSlot,
                Row("RawBtn0", Source(TargetPad, "Button 8")),
                Row("RawAxis1", Legs(TargetPad, "Button 2", "Button 3")));
            Assert.Equal(2, BaseRows(TargetSlot).Count);

            Transfer(copyFrom);
            AutosavePush();

            Assert.Empty(BaseRows(TargetSlot));
            Assert.Equal("", GridItem(TargetSlot, "RawBtn0").SourceDescriptor);
        }

        /// <summary>True when the engine presses the target slot's button
        /// target with the given buttons of its pad held, its sticks centered,
        /// its triggers released and no shift layer engaged.</summary>
        private bool Pressed(string target, params int[] held)
        {
            var state = new CustomInputState();
            Array.Fill(state.Axis, 32768);
            state.Axis[2] = state.Axis[5] = 0;
            Array.Fill(state.Povs, -1);
            foreach (int button in held) state.Buttons[button] = true;
            SettingsManager.FindDeviceByInstanceGuid(TargetPad).InputState = state;
            InputManager.EndDeviceStateMemo();
            InputManager.ClearAllShiftRuntime();
            return InputManager.TryEvaluateMappingSetButton(state, SettingsManager.SlotMappingSets[TargetSlot],
                TargetPad.ToString(), TargetSlot, target, 50, out bool pressed) && pressed;
        }

        /// <summary>What does not cross stays the target's own: the rows on
        /// its shift layers, its activators, its menus, its Base tab's name
        /// and its SOCD pairs. The copied slot's layer rows, activators,
        /// menus and SOCD pairs stay behind. Rumble audio and Keep Awake name
        /// no target and cross. The set stays the same object, and its rows
        /// are published as one new list. A kept layer row whose own Base
        /// row went with the rest of the Base is left as it would be after
        /// that Base row was cleared by hand.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheTargetKeepsItsShiftLayersMenusAndSocdAndTakesNone(bool copyFrom)
        {
            Arrange(Xbox, Extended);
            var source = SettingsManager.SlotMappingSets[SourceSlot];
            source.Rows = new List<MappingRow>
            {
                Row("ButtonA", Source(SourcePad, "Button 0")),
                OnLayer("Shift2", Row("ButtonB", Source(SourcePad, "Button 1"))),
            };
            source.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = SourcePad.ToString(), Descriptor = "Button 6", LayerMask = "Shift2", LayerName = "Pit",
            });
            source.Menus.Add(new MenuDefinitionEntry { MenuId = 4, Name = "Source menu" });
            source.BaseLayerName = "Source base";
            source.BaseColor = "#FF112233";
            source.BaseIcon = "S";
            source.Authoritative = true;
            source.WorkshopGyroEngageDescriptor = "Button 9";
            source.SocdMode = "Neutral";
            source.SocdPairs = "DPadLeft:DPadRight";
            source.RumbleAudio = new RumbleAudioConfig { Enabled = true, EndpointId = "endpoint-a", MasterGainPercent = 73 };
            source.KeepAwakeEnabled = true;
            source.KeepAwakeAxis = "RX";
            source.KeepAwakeDeflection = 30;
            Reload(SourceSlot);

            var target = SettingsManager.SlotMappingSets[TargetSlot];
            var layerRow = OnLayer("Shift1", Row("RawBtn3", Source(TargetPad, "Button 12")));
            target.Rows = new List<MappingRow>
            {
                Row("RawBtn5", Source(TargetPad, "Button 9")),
                Row("RawBtn3", Source(TargetPad, "Button 11")),
                layerRow,
            };
            target.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = TargetPad.ToString(), Descriptor = "Button 7", LayerMask = "Shift1", LayerName = "Old layer",
            });
            target.Menus.Add(new MenuDefinitionEntry { MenuId = 9, Name = "Old menu" });
            target.BaseLayerName = "Old base";
            target.BaseColor = "#FF445566";
            target.BaseIcon = "O";
            target.Authoritative = true;
            target.WorkshopGyroEngageDescriptor = "Button 6";
            target.SocdMode = "LastWins";
            target.SocdPairs = "12:13";
            target.RumbleAudio = new RumbleAudioConfig { Enabled = true, EndpointId = "endpoint-b" };
            Reload(TargetSlot);
            var rowsBefore = target.Rows;
            Assert.True(Pressed("RawBtn3", 11));

            Transfer(copyFrom);
            // Nothing of the copied slot's layer row is on the target, on any
            // layer. Read before the push, which would write the grid's blank
            // cell over a row that landed on Base.
            Assert.DoesNotContain(target.Rows, r => r.Sources.Any(s => s.Descriptor == "Button 1"));
            AutosavePush();

            // A reader that took the list before the copy still holds the old
            // table whole.
            Assert.Same(target, SettingsManager.SlotMappingSets[TargetSlot]);
            Assert.NotSame(rowsBefore, target.Rows);
            Assert.Equal("Button 9",
                Assert.Single(Assert.Single(rowsBefore, r => r.Target == "RawBtn5").Sources).Descriptor);
            Assert.Contains(layerRow, rowsBefore);

            Assert.Equal("Button 0", Assert.Single(BaseRow(TargetSlot, "RawBtn0").Sources).Descriptor);
            Assert.Null(BaseRow(TargetSlot, "RawBtn5"));

            Assert.Null(BaseRow(TargetSlot, "RawBtn1"));
            Assert.Same(layerRow, Assert.Single(target.Rows, r => r.LayerMask != "Base"));
            Assert.Equal("Shift1", layerRow.LayerMask);
            Assert.Equal("Button 12", Assert.Single(layerRow.Sources).Descriptor);

            // The layer row's own Base row is gone. Outside its layer neither
            // the old Base button nor the layer's presses the target, and the
            // pad's own setting holds no binding for it to fall back on.
            Assert.Null(BaseRow(TargetSlot, "RawBtn3"));
            Assert.False(Pressed("RawBtn3", 11, 12));
            Assert.True(string.IsNullOrEmpty(SettingsManager.FindSettingByInstanceGuidAndSlot(TargetPad, TargetSlot)
                .GetPadSetting().GetRawMapping("RawBtn3")));
            Assert.Equal("Old layer", Assert.Single(target.ShiftActivators).LayerName);
            Assert.Equal("Old menu", Assert.Single(target.Menus).Name);
            Assert.Equal("Old base", target.BaseLayerName);
            Assert.Equal("#FF445566", target.BaseColor);
            Assert.Equal("O", target.BaseIcon);
            Assert.True(target.Authoritative);
            Assert.Equal("Button 6", target.WorkshopGyroEngageDescriptor);
            Assert.Equal("LastWins", target.SocdMode);
            Assert.Equal("12:13", target.SocdPairs);

            Assert.Equal("endpoint-a", target.RumbleAudio.EndpointId);
            Assert.Equal(73, target.RumbleAudio.MasterGainPercent);
            Assert.True(target.KeepAwakeEnabled);
            Assert.Equal("RX", target.KeepAwakeAxis);
            Assert.Equal(30, target.KeepAwakeDeflection);
        }

        /// <summary>With a shift layer open in the grid, the copy still lands
        /// on Base, and the autosave's push of the open layer leaves both the
        /// layer's row and the new Base row as they are.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ACopyWhileAShiftLayerIsOpenStillLandsOnBase(bool copyFrom)
        {
            Arrange(Xbox, Extended);
            SetRows(SourceSlot, Row("ButtonA", Source(SourcePad, "Button 0")));
            var target = SettingsManager.SlotMappingSets[TargetSlot];
            target.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = TargetPad.ToString(), Descriptor = "Button 7", LayerMask = "Shift1",
            });
            target.Rows = new List<MappingRow>
            {
                Row("RawBtn0", Source(TargetPad, "Button 8")),
                OnLayer("Shift1", Row("RawBtn0", Source(TargetPad, "Button 12"))),
            };
            Reload(TargetSlot);
            var pad = _vm.Pads[TargetSlot];
            pad.ActiveLayerMask = "Shift1";
            Assert.Equal("Button 12", GridItem(TargetSlot, "RawBtn0").SourceDescriptor);

            Transfer(copyFrom);
            AutosavePush();

            Assert.Equal("Shift1", pad.ActiveLayerMask);
            Assert.Equal("Button 12", GridItem(TargetSlot, "RawBtn0").SourceDescriptor);
            var rows = target.Rows.Where(r => r.Target == "RawBtn0").ToList();
            Assert.Equal("Button 0", Assert.Single(rows.Single(r => r.LayerMask == "Base").Sources).Descriptor);
            Assert.Equal("Button 12", Assert.Single(rows.Single(r => r.LayerMask == "Shift1").Sources).Descriptor);
        }

        /// <summary>A row crosses with every source and setting. Each source
        /// moves to its own controller where that sits on the target slot too,
        /// else to the slot's controller of the same model, and one whose
        /// model the slot lacks is dropped, the shared-layout rule.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SourcesFollowTheirControllerOrItsModel(bool copyFrom)
        {
            Arrange(Xbox, Extended);
            AddDevice(KeysOnSource, KeysModel, SourceSlot);
            AddDevice(OnBothSlots, OnBothModel, SourceSlot);
            AddDevice(OnBothSlots, OnBothModel, TargetSlot);
            _input.RefreshDeviceList();

            var tuned = Source(SourcePad, "Button 0");
            tuned.DeadZone = 30;
            tuned.GateDescriptor = "Button 8";
            var row = Row("ButtonA", tuned, Source(KeysOnSource, "Button 30"), Source(OnBothSlots, "Button 2"));
            row.CombineMode = "AND";
            var summed = Row("LeftThumbAxisX", Source(SourcePad, "Axis 0"), Source(OnBothSlots, "Axis 3"));
            summed.CombineMode = "Sum";
            summed.Sources[1].Sensitivity = 1.5;
            SetRows(SourceSlot, row, summed);

            Transfer(copyFrom);
            AutosavePush();

            var button = BaseRow(TargetSlot, "RawBtn0");
            Assert.Equal("AND", button.CombineMode);
            Assert.Equal(new[] { "Button 0", "Button 2" }, Descriptors(button));
            Assert.Equal(TargetPad.ToString(), button.Sources[0].DeviceGuid, ignoreCase: true);
            Assert.Equal(30, button.Sources[0].DeadZone);
            Assert.Equal("Button 8", button.Sources[0].GateDescriptor);
            Assert.Equal(OnBothSlots.ToString(), button.Sources[1].DeviceGuid, ignoreCase: true);

            var axis = BaseRow(TargetSlot, "RawAxis0");
            Assert.Equal("Sum", axis.CombineMode);
            Assert.Equal(new[] { "Axis 0", "Axis 3" }, Descriptors(axis));
            Assert.Equal(1.5, axis.Sources[1].Sensitivity);
        }

        // The negative leg of a two-button axis.

        /// <summary>How far the engine's trigger read pulls the target slot's
        /// row with the given buttons of its pad held: 0 released, 1 full.</summary>
        private double TriggerPull(string target, params int[] held)
        {
            var state = new CustomInputState();
            Array.Fill(state.Axis, 32768);
            state.Axis[2] = state.Axis[5] = 0;
            Array.Fill(state.Povs, -1);
            foreach (int button in held) state.Buttons[button] = true;
            SettingsManager.FindDeviceByInstanceGuid(TargetPad).InputState = state;
            InputManager.EndDeviceStateMemo();
            InputManager.ClearAllShiftRuntime();
            Assert.True(InputManager.TryEvaluateMappingSetRawTrigger(state, SettingsManager.SlotMappingSets[TargetSlot],
                TargetPad.ToString(), TargetSlot, target, out short value));
            return (value - short.MinValue) / 65535.0;
        }

        /// <summary>A trigger has no negative side. Where a two-sided axis
        /// lands on one, its negative leg is dropped and the positive leg
        /// lands alone, as the Neg field was dropped: kept, the leg would
        /// hold the trigger full at rest.</summary>
        [Theory]
        [InlineData(KeyboardMouse, "KbmScroll", "LeftTrigger", false)]
        [InlineData(KeyboardMouse, "KbmScroll", "LeftTrigger", true)]
        [InlineData(Midi, "MidiCC5", "RightTrigger", false)]
        [InlineData(Midi, "MidiCC5", "RightTrigger", true)]
        [InlineData(Extended, "RawAxis2", "LeftTrigger", false)]
        [InlineData(Extended, "RawAxis2", "LeftTrigger", true)]
        public void ANegativeLegIsDroppedWhereTheAxisLandsOnATrigger(
            VirtualControllerType from, string axis, string trigger, bool copyFrom)
        {
            Arrange(from, Xbox);
            if (from == Extended) NoTriggers(SourceSlot);
            SetRows(SourceSlot, Row(axis, Legs(SourcePad, "Button 4", "Button 5")));

            Transfer(copyFrom);
            AutosavePush();

            var source = Assert.Single(BaseRow(TargetSlot, trigger).Sources);
            Assert.Equal("Button 4", source.Descriptor);
            Assert.False(source.Invert);
            Assert.Equal(0.0, TriggerPull(trigger));
            Assert.Equal(1.0, TriggerPull(trigger, 4));
            Assert.Equal(0.0, TriggerPull(trigger, 5));
        }

        /// <summary>A button has no negative side either. A Switch pad's ZL
        /// and ZR are buttons, and the trigger positions land on them by
        /// role, so a two-sided axis there drops its negative leg too: kept,
        /// the leg would hold the button down at rest.</summary>
        [Theory]
        [InlineData(KeyboardMouse, "KbmScroll", "LeftTrigger", false)]
        [InlineData(KeyboardMouse, "KbmScroll", "LeftTrigger", true)]
        [InlineData(Midi, "MidiCC5", "RightTrigger", false)]
        [InlineData(Midi, "MidiCC5", "RightTrigger", true)]
        public void ANegativeLegIsDroppedWhereTheAxisLandsOnASwitchButton(
            VirtualControllerType from, string axis, string trigger, bool copyFrom)
        {
            Arrange(from, Nintendo);
            string button = ByTable(trigger, "switch-pro");
            Assert.StartsWith("RawBtn", button);
            SetRows(SourceSlot, Row(axis, Legs(SourcePad, "Button 4", "Button 5")));

            Transfer(copyFrom);
            AutosavePush();

            var source = Assert.Single(BaseRow(TargetSlot, button).Sources);
            Assert.Equal("Button 4", source.Descriptor);
            Assert.False(source.Invert);
            Assert.False(Pressed(button));
            Assert.True(Pressed(button, 4));
            Assert.False(Pressed(button, 5));
        }

        /// <summary>The same where the target's own layout makes a raw axis a
        /// trigger. On its stick axes the pair lands whole.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ANegativeLegIsDroppedOnARawTriggerAndKeptOnARawStick(bool copyFrom)
        {
            Arrange(Midi, Extended);
            SetRows(SourceSlot,
                Row("MidiCC0", Legs(SourcePad, "Button 2", "Button 3")),
                Row("MidiCC2", Legs(SourcePad, "Button 4", "Button 5")));
            Assert.True(InputService.SlotLayoutOf(_vm.Pads[TargetSlot]).IsTrigger("RawAxis2"));
            Assert.False(InputService.SlotLayoutOf(_vm.Pads[TargetSlot]).IsTrigger("RawAxis0"));

            Transfer(copyFrom);
            AutosavePush();

            Assert.Equal(new[] { "Button 2", "Button 3" }, Descriptors(BaseRow(TargetSlot, "RawAxis0")));
            Assert.Equal(new[] { "Button 4" }, Descriptors(BaseRow(TargetSlot, "RawAxis2")));
            Assert.Equal(0.0, TriggerPull("RawAxis2"));
        }

        [Theory]
        [InlineData(Extended, "RawAxis1", false)]
        [InlineData(Extended, "RawAxis1", true)]
        [InlineData(Midi, "MidiCC1", false)]
        [InlineData(Midi, "MidiCC1", true)]
        [InlineData(KeyboardMouse, "KbmMouseY", false)]
        [InlineData(KeyboardMouse, "KbmMouseY", true)]
        public void ANegativeLegLandsWhereTheTargetIsTwoSided(
            VirtualControllerType to, string expected, bool copyFrom)
        {
            Arrange(Xbox, to);
            SetRows(SourceSlot, Row("LeftThumbAxisY", Legs(SourcePad, "Button 4", "Button 5")));

            Transfer(copyFrom);
            AutosavePush();

            var row = BaseRow(TargetSlot, expected);
            Assert.Equal(new[] { "Button 4", "Button 5" }, Descriptors(row));
            Assert.False(row.Sources[0].Invert);
            Assert.True(row.Sources[1].Invert);
        }

        /// <summary>A trigger's row lands on a trigger with every source. Its
        /// second source is no negative leg, whatever its Invert: here it is a
        /// Stick Trim row's trim stick. The copied slot's own layout says
        /// which of its raw axes are triggers, in the payload for a Paste.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ATriggerRowLandsOnATriggerWithEverySource(bool copyFrom)
        {
            Arrange(Extended, Xbox);
            var trim = Row("RawAxis2", Source(SourcePad, "Axis 2"), Source(SourcePad, "Axis 4", invert: true));
            trim.CombineMode = "StickTrim";
            trim.TrimDeadzone = 30;
            trim.TrimRate = 80;
            SetRows(SourceSlot, trim);
            Assert.True(InputService.SlotLayoutOf(_vm.Pads[SourceSlot]).IsTrigger("RawAxis2"));

            Transfer(copyFrom);
            AutosavePush();

            var row = BaseRow(TargetSlot, "LeftTrigger");
            Assert.Equal(new[] { "Axis 2", "Axis 4" }, Descriptors(row));
            Assert.True(row.Sources[1].Invert);
            Assert.Equal("StickTrim", row.CombineMode);
            Assert.Equal(30, row.TrimDeadzone);
            Assert.Equal(80, row.TrimRate);
        }

        // A Custom row's letters.

        private MappingSource[] PairAndOne()
            => new[] { Source(SourcePad, "Button 13"), Source(SourcePad, "Button 14", invert: true), Source(SourcePad, "Axis 3") };

        /// <summary>A stick axis folds the pair into the letter a. Dropped
        /// from it on a trigger, the leg takes no letter with it: b is still
        /// the third source.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AFormulaKeepsItsLettersWhereItsFoldedPairLandsOnATrigger(bool copyFrom)
        {
            Arrange(Extended, Xbox);
            NoTriggers(SourceSlot);
            SetRows(SourceSlot, Formula("RawAxis2", "a * 0.5 + b", PairAndOne()));
            Assert.Equal(2, GridItem(SourceSlot, "RawAxis2").PositionalSourceCount);

            Transfer(copyFrom);
            AutosavePush();

            var row = BaseRow(TargetSlot, "LeftTrigger");
            Assert.Equal(new[] { "Button 13", "Axis 3" }, Descriptors(row));
            Assert.Equal("Custom", row.CombineMode);
            Assert.Equal("a * 0.5 + b", row.CombineExpression);
            Assert.Equal(2, GridItem(TargetSlot, "LeftTrigger").PositionalSourceCount);
        }

        /// <summary>A MIDI CC and a trigger give each source its own letter.
        /// On a stick axis the first two would fold into one, so the row
        /// keeps them apart and the formula reads what it read. The trigger
        /// is a gamepad's, or a raw axis the copied slot's own layout makes
        /// one.</summary>
        [Theory]
        [InlineData(Midi, "MidiCC0", Xbox, "LeftThumbAxisX", false)]
        [InlineData(Midi, "MidiCC0", Xbox, "LeftThumbAxisX", true)]
        [InlineData(Xbox, "LeftTrigger", Extended, "RawAxis2", false)]
        [InlineData(Xbox, "LeftTrigger", Extended, "RawAxis2", true)]
        [InlineData(Extended, "RawAxis0", Xbox, "LeftThumbAxisX", false)]
        [InlineData(Extended, "RawAxis0", Xbox, "LeftThumbAxisX", true)]
        public void AFormulaKeepsItsLettersWhereAPairReadApartLandsOnAStickAxis(VirtualControllerType from,
            string sourceTarget, VirtualControllerType to, string expected, bool copyFrom)
        {
            Arrange(from, to);
            if (to == Extended) NoTriggers(TargetSlot);
            if (from == Extended)
            {
                // No stick and two triggers: raw axes 0 and 1 are the triggers.
                var pad = _vm.Pads[SourceSlot];
                pad.ExtendedConfig.ThumbstickCount = 0;
                pad.RebuildMappings();
                Assert.True(InputService.SlotLayoutOf(pad).IsTrigger(sourceTarget));
            }
            SetRows(SourceSlot, Formula(sourceTarget, "a + b + c", PairAndOne()));
            Assert.Equal(3, GridItem(SourceSlot, sourceTarget).PositionalSourceCount);

            Transfer(copyFrom);
            AutosavePush();

            var row = BaseRow(TargetSlot, expected);
            Assert.Equal(new[] { "Button 13", "Button 14", "Axis 3" }, Descriptors(row));
            Assert.Equal("Custom", row.CombineMode);
            Assert.Equal("a + b + c", row.CombineExpression);
            Assert.True(row.SuppressBipolarPair);
            Assert.Equal(3, GridItem(TargetSlot, expected).PositionalSourceCount);
        }

        /// <summary>Between two controls of one kind a Custom row crosses
        /// whole: a stick axis onto a stick axis with its pair folded, a
        /// trigger onto a trigger with each source its own letter.</summary>
        [Theory]
        [InlineData(Xbox, "LeftThumbAxisX", Extended, "RawAxis0", 2, false)]
        [InlineData(Xbox, "LeftThumbAxisX", Extended, "RawAxis0", 2, true)]
        [InlineData(Extended, "RawAxis2", Xbox, "LeftTrigger", 3, false)]
        [InlineData(Extended, "RawAxis2", Xbox, "LeftTrigger", 3, true)]
        [InlineData(Xbox, "LeftTrigger", Extended, "RawAxis2", 3, false)]
        [InlineData(Xbox, "LeftTrigger", Extended, "RawAxis2", 3, true)]
        public void AFormulaCrossesWholeBetweenControlsOfOneKind(VirtualControllerType from, string sourceTarget,
            VirtualControllerType to, string expected, int letters, bool copyFrom)
        {
            Arrange(from, to);
            SetRows(SourceSlot, Formula(sourceTarget, "a * 0.5 + b", PairAndOne()));
            Assert.Equal(letters, GridItem(SourceSlot, sourceTarget).PositionalSourceCount);

            Transfer(copyFrom);
            AutosavePush();

            var row = BaseRow(TargetSlot, expected);
            Assert.Equal(new[] { "Button 13", "Button 14", "Axis 3" }, Descriptors(row));
            Assert.True(row.Sources[1].Invert);
            Assert.Equal("Custom", row.CombineMode);
            Assert.Equal("a * 0.5 + b", row.CombineExpression);
            Assert.False(row.SuppressBipolarPair);
            Assert.Equal(letters, GridItem(TargetSlot, expected).PositionalSourceCount);
        }

        /// <summary>Where the letters cannot be kept, the row lands without
        /// its formula, as the fields carried it. A folded pair on a MIDI CC,
        /// which cannot fold it: the two legs. A pair read apart on a
        /// trigger, where dropping the leg would move every later letter and
        /// keeping it would hold the trigger full: the first source.</summary>
        [Theory]
        [InlineData(Xbox, "LeftThumbAxisX", Midi, "MidiCC0", 2, false)]
        [InlineData(Xbox, "LeftThumbAxisX", Midi, "MidiCC0", 2, true)]
        [InlineData(Midi, "MidiCC2", Xbox, "LeftTrigger", 1, false)]
        [InlineData(Midi, "MidiCC2", Xbox, "LeftTrigger", 1, true)]
        public void AFormulaIsLeftBehindWhereItsLettersCannotBeKept(VirtualControllerType from, string sourceTarget,
            VirtualControllerType to, string expected, int sources, bool copyFrom)
        {
            Arrange(from, to);
            SetRows(SourceSlot, Formula(sourceTarget, "a * 0.5 + b", PairAndOne()));

            Transfer(copyFrom);
            var row = SettingsManager.SlotMappingSets[TargetSlot].Rows.Single(r => r.Target == expected);
            Assert.Equal(new[] { "Button 13", "Button 14" }.Take(sources), Descriptors(row));
            Assert.Equal("", row.CombineMode);
            Assert.Equal("", row.CombineExpression);
            Assert.False(row.SuppressBipolarPair);

            AutosavePush();
            row = BaseRow(TargetSlot, expected);
            Assert.Equal(sources, row.Sources.Count);
            Assert.NotEqual("Custom", row.CombineMode);
            Assert.Equal("", row.CombineExpression);
            if (to == Xbox) Assert.Equal(0.0, TriggerPull(expected));
        }

        /// <summary>The same on a Switch pad's ZR, a button the trigger
        /// position lands on by role: dropping the leg would move every later
        /// letter, and keeping it would hold the button down. The row lands
        /// without its formula, its first source alone.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AFormulaIsLeftBehindOnASwitchButton(bool copyFrom)
        {
            Arrange(Midi, Nintendo);
            string zr = ByTable("RightTrigger", "switch-pro");
            SetRows(SourceSlot, Formula("MidiCC5", "a * 0.5 + b", PairAndOne()));

            Transfer(copyFrom);
            AutosavePush();

            var row = BaseRow(TargetSlot, zr);
            Assert.Equal(new[] { "Button 13" }, Descriptors(row));
            Assert.NotEqual("Custom", row.CombineMode);
            Assert.Equal("", row.CombineExpression);
            Assert.False(Pressed(zr));
            Assert.True(Pressed(zr, 13));
        }

        /// <summary>A copy marks a Custom row whose first two sources are
        /// separate arguments that only look like a pair
        /// (SuppressBipolarPair). Such a row has no negative leg: it crosses
        /// whole onto any control, a trigger included, with every letter.</summary>
        [Theory]
        [InlineData(Extended, "RawAxis2", Xbox, "LeftTrigger", false)]
        [InlineData(Extended, "RawAxis2", Xbox, "LeftTrigger", true)]
        [InlineData(Xbox, "LeftThumbAxisX", Midi, "MidiCC0", false)]
        [InlineData(Xbox, "LeftThumbAxisX", Midi, "MidiCC0", true)]
        [InlineData(Xbox, "LeftThumbAxisX", Extended, "RawAxis0", false)]
        [InlineData(Xbox, "LeftThumbAxisX", Extended, "RawAxis0", true)]
        public void AFormulaWithSeparateArgumentsCrossesWhole(VirtualControllerType from, string sourceTarget,
            VirtualControllerType to, string expected, bool copyFrom)
        {
            Arrange(from, to);
            if (from == Extended) NoTriggers(SourceSlot);
            var formula = Formula(sourceTarget, "a + b * 0.5 + c", PairAndOne());
            formula.SuppressBipolarPair = true;
            SetRows(SourceSlot, formula);
            Assert.Equal(3, GridItem(SourceSlot, sourceTarget).PositionalSourceCount);

            Transfer(copyFrom);
            AutosavePush();

            var row = BaseRow(TargetSlot, expected);
            Assert.Equal(new[] { "Button 13", "Button 14", "Axis 3" }, Descriptors(row));
            Assert.True(row.Sources[1].Invert);
            Assert.Equal("Custom", row.CombineMode);
            Assert.Equal("a + b * 0.5 + c", row.CombineExpression);
            Assert.True(row.SuppressBipolarPair);
            Assert.Equal(3, GridItem(TargetSlot, expected).PositionalSourceCount);
        }

        /// <summary>With the leg gone, the source that followed it sits next
        /// to the positive leg and can look like its pair. It is none: where
        /// neither has a controller on the target slot, each letter keeps a
        /// blank of its own, and the second blank is not rewritten into the
        /// first one's negative leg.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void WhatFollowedADroppedLegIsNoNewLeg(bool copyFrom)
        {
            Arrange(Extended, Xbox);
            NoTriggers(SourceSlot);
            AddDevice(KeysOnSource, KeysModel, SourceSlot);
            _input.RefreshDeviceList();
            SetRows(SourceSlot, Formula("RawAxis2", "a + b",
                Source(KeysOnSource, "Button 13"), Source(KeysOnSource, "Button 14", invert: true),
                Source(KeysOnSource, "Button 15", invert: true)));
            Assert.Equal(2, GridItem(SourceSlot, "RawAxis2").PositionalSourceCount);

            Transfer(copyFrom);

            var row = SettingsManager.SlotMappingSets[TargetSlot].Rows.Single(r => r.Target == "LeftTrigger");
            Assert.Equal("Custom", row.CombineMode);
            Assert.Equal("a + b", row.CombineExpression);
            Assert.Equal(2, row.Sources.Count);
            Assert.All(row.Sources, s =>
            {
                Assert.True(string.IsNullOrEmpty(s.Descriptor));
                Assert.False(s.Invert);
            });
        }

        // Paste and Copy From, side by side.

        /// <summary>Binds every row of a slot's grid, a two-button pair on each
        /// two-sided axis, and gives the slot a shift layer with a row, a menu
        /// and the extras.</summary>
        private void MakeBusy(int slot, Guid device, string layer, string name)
        {
            var pad = _vm.Pads[slot];
            var set = SettingsManager.SlotMappingSets[slot];
            var rows = new List<MappingRow>();
            int button = 0;
            foreach (var item in pad.Mappings)
            {
                rows.Add(item.NegSettingName != null
                    ? Row(item.TargetSettingName, Legs(device, $"Button {button}", $"Button {button + 200}"))
                    : Row(item.TargetSettingName, Source(device, $"Button {button}")));
                button++;
            }
            rows.Add(OnLayer(layer, Row(pad.Mappings[0].TargetSettingName, Source(device, "Button 190"))));
            set.Rows = rows;
            set.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = device.ToString(), Descriptor = "Button 191", LayerMask = layer, LayerName = name,
            });
            set.Menus.Add(new MenuDefinitionEntry { MenuId = slot + 1, Name = name + " menu" });
            set.BaseLayerName = name + " base";
            set.SocdMode = slot == SourceSlot ? "Neutral" : "LastWins";
            set.SocdPairs = pad.Mappings[0].TargetSettingName + ":" + pad.Mappings[1].TargetSettingName;
            set.RumbleAudio = new RumbleAudioConfig { Enabled = true, EndpointId = name + "-endpoint", MasterGainPercent = 30 + slot };
            set.KeepAwakeEnabled = slot == SourceSlot;
            set.KeepAwakeAxis = slot == SourceSlot ? "RX" : "LY";
            set.KeepAwakeDeflection = 20 + slot;
            Reload(slot);
        }

        /// <summary>The same two slots end with the same mapping set whether
        /// the copy went through the clipboard or through Copy From.</summary>
        [Theory]
        [InlineData(Xbox, Extended)]
        [InlineData(Extended, Xbox)]
        [InlineData(Xbox, Midi)]
        [InlineData(Midi, Xbox)]
        [InlineData(Xbox, KeyboardMouse)]
        [InlineData(KeyboardMouse, PlayStation)]
        [InlineData(PlayStation, Nintendo)]
        [InlineData(Nintendo, Midi)]
        [InlineData(Extended, KeyboardMouse)]
        [InlineData(Xbox, Vr)]
        [InlineData(Vr, Extended)]
        public void PasteAndCopyFromLeaveTheSameMappingSet(VirtualControllerType from, VirtualControllerType to)
        {
            string Run(bool copyFrom)
            {
                Arrange(from, to);
                MakeBusy(SourceSlot, SourcePad, "Shift2", "Pit");
                MakeBusy(TargetSlot, TargetPad, "Shift1", "Old");
                Transfer(copyFrom);
                AutosavePush();
                return Dump(SettingsManager.SlotMappingSets[TargetSlot]);
            }

            string pasted = Run(copyFrom: false);
            string copied = Run(copyFrom: true);
            _output.WriteLine(pasted);

            // Both carried the source's rumble audio and kept the target's
            // own layer, and they agree on everything else.
            Assert.Contains("  EndpointId=Pit-endpoint\n", pasted);
            Assert.Contains("  LayerName=Old\n", pasted);
            Assert.Contains("  LayerMask=Shift1\n", pasted);
            Assert.DoesNotContain("LayerName=Pit", pasted);
            Assert.DoesNotContain("LayerMask=Shift2", pasted);
            Assert.Equal(pasted, copied);
        }

        /// <summary>A payload from before the whole-slot row list carries
        /// field bindings only. It changes no row on any layout, as before.</summary>
        [Theory]
        [InlineData(Xbox)]
        [InlineData(Extended)]
        public void APayloadWithNoRowListLeavesTheRowsAlone(VirtualControllerType to)
        {
            Arrange(Xbox, to);
            SetRows(SourceSlot, Row("ButtonA", Source(SourcePad, "Button 0")));
            string own = to == Xbox ? "ButtonY" : "RawBtn5";
            SetRows(TargetSlot, Row(own, Source(TargetPad, "Button 9")));
            var payload = PadSetting.FromJson(Copy(SourceSlot), out VirtualControllerType type, out bool raw);
            payload.SlotMultiSourceRows = null;

            Paste(TargetSlot, payload.ToJson(type, raw));
            AutosavePush();

            var row = Assert.Single(BaseRows(TargetSlot));
            Assert.Equal(own, row.Target);
            Assert.Equal("Button 9", Assert.Single(row.Sources).Descriptor);
        }

        /// <summary>The row step alone, as each handler calls it. A Paste's
        /// payload is built before the step is handed back.</summary>
        private Action RowStep(bool copyFrom)
        {
            var layout = InputService.SlotLayoutOf(_vm.Pads[TargetSlot]);
            if (copyFrom)
                return () => InputService.TranslateSlotMappingSet(TargetSlot, SourceSlot,
                    InputService.SlotLayoutOf(_vm.Pads[SourceSlot]), layout);
            var payload = PadSetting.FromJson(Copy(SourceSlot), out VirtualControllerType type, out bool raw);
            return () => InputService.ApplySlotRowsFromClipboard(TargetSlot, payload, type, raw, layout);
        }

        /// <summary>The copy replaces the slot's routing, so nothing a
        /// Bliss-Box port owed the slot is filled after, as on a shared
        /// layout.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheCopyEndsWhatAPortOwedTheSlot(bool copyFrom)
        {
            Arrange(Xbox, Extended);
            SetRows(SourceSlot, Row("ButtonA", Source(SourcePad, "Button 0")));
            AddDevice(WheelOnTarget, WheelModel, TargetSlot);
            var owing = SettingsManager.FindSettingByInstanceGuidAndSlot(WheelOnTarget, TargetSlot);
            owing.BlissBoxOwed = BlissBoxOwedMapping.Default;

            RowStep(copyFrom)();

            Assert.Equal(BlissBoxOwedMapping.None, owing.BlissBoxOwed);
            Assert.Equal("Button 0", Assert.Single(BaseRow(TargetSlot, "RawBtn0").Sources).Descriptor);
        }

        /// <summary>A target slot that has no mapping set yet gets one with
        /// the translated rows.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ATargetWithNoMappingSetGetsOne(bool copyFrom)
        {
            Arrange(Xbox, Extended);
            SetRows(SourceSlot, Row("ButtonA", Source(SourcePad, "Button 0")));
            var step = RowStep(copyFrom);
            SettingsManager.SlotMappingSets[TargetSlot] = null;

            step();

            Assert.Equal("Button 0", Assert.Single(BaseRow(TargetSlot, "RawBtn0").Sources).Descriptor);
        }

        /// <summary>The state the engine keeps for a slot's rows goes with the
        /// rows the copy replaced, as on a shared layout. The test reads a
        /// stick-trim level kept under a target no row has: only the slot's
        /// reset or a profile switch removes it, so a poll pass in another
        /// test cannot change what is read here. The request that starts the
        /// simulated pose over is consumed by any poll pass, so it is not
        /// read.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheCopyStartsTheSlotsRowStateOver(bool copyFrom)
        {
            Arrange(Xbox, Extended);
            SetRows(SourceSlot, Row("ButtonA", Source(SourcePad, "Button 0")));
            var field = typeof(InputManager).GetField("_stickTrimStates", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.True(field != null, "_stickTrimStates is gone");
            var levels = (IDictionary)field.GetValue(null);
            var stateType = typeof(InputManager).GetNestedType("StickTrimState", BindingFlags.NonPublic);
            Assert.True(stateType != null, "StickTrimState is gone");
            object key = Activator.CreateInstance(levels.GetType().GetGenericArguments()[0],
                TargetSlot, "CrossLayoutResetProbe", "Base");
            var step = RowStep(copyFrom);
            levels[key] = Activator.CreateInstance(stateType, nonPublic: true);
            Assert.True(levels.Contains(key));

            step();

            Assert.False(levels.Contains(key), "the copy left the slot's row state in place");
        }

        /// <summary>Source-text lock. The handlers read the clipboard and
        /// open a dialog, so these tests make their calls for them, and the
        /// calls are pinned here: the row step where Paste and Copy From run
        /// it, with the layouts they pass, and the two resets a replaced
        /// table needs.</summary>
        [Fact]
        public void TheHandlersRunTheRowStepWhereTheseTestsRunIt()
        {
            static string Between(string text, string start, string end)
            {
                int from = text.IndexOf(start, StringComparison.Ordinal);
                Assert.True(from >= 0, start + " not found");
                int to = text.IndexOf(end, from, StringComparison.Ordinal);
                Assert.True(to > from, end + " not found");
                return text.Substring(from, to - from);
            }
            static int At(string text, string call)
            {
                int index = text.IndexOf(call, StringComparison.Ordinal);
                Assert.True(index >= 0, call + " not found");
                return index;
            }

            string window = System.IO.File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(
                System.IO.Path.Combine("PadForge.App", "MainWindow.xaml.cs")));

            string copy = Between(window, "private void OnCopySettings(", "private void OnPasteSettings(");
            int profile = At(copy, "ps.SlotProfileId = padVm.ProfileId;");
            Assert.True(profile < At(copy, "Clipboard.SetText(ps.ToJson(copyOutputType, copyIsExtended));"));

            string paste = Between(window, "private void OnPasteSettings(", "private void OnCopyMacro(");
            int rows = At(paste, "InputService.ApplySlotRowsFromClipboard(padVm.PadIndex, ps,");
            int layouts = At(paste, "srcType, srcIsExtended, InputService.SlotLayoutOf(padVm));");
            int extras = At(paste, "InputService.ApplySlotSetExtrasJson(padVm.PadIndex, ps.SlotSetExtrasJson,");
            int tuning = At(paste, "_inputService.ApplyPadSettingToCurrentDeviceTranslated(");
            Assert.True(rows < layouts && layouts < extras && extras < tuning);

            string copyFrom = Between(window, "private void OnCopyFrom(", "internal static void ApplyCopyFromSettings(");
            int whole = At(copyFrom, "InputService.ReplaceSlotMappingSet(padVm.PadIndex, srcEntry.SourceSlot);");
            int translated = At(copyFrom, "InputService.TranslateSlotMappingSet(padVm.PadIndex, srcEntry.SourceSlot,");
            int sourceLayout = At(copyFrom, "InputService.SlotLayoutOf(_viewModel.Pads[srcEntry.SourceSlot]),");
            int targetLayout = At(copyFrom, "InputService.SlotLayoutOf(padVm));");
            int apply = At(copyFrom, "ApplyCopyFromSettings(_inputService, _settingsService, padVm, srcEntry,");
            Assert.True(whole < translated && translated < sourceLayout && sourceLayout < targetLayout && targetLayout < apply);

            string input = System.IO.File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(
                System.IO.Path.Combine("PadForge.App", "Services", "InputService.cs")));
            string step = Between(input, "private static void ReplaceBaseRowsTranslated(", "private static bool IsBaseRow(");
            int owed = At(step, "DeviceService.CancelOwedMappingForSlot(padIndex);");
            int swap = At(step, "else current.Rows = replaced;");
            int reset = At(step, "Common.Input.InputManager.ResetSourceKindRuntimeForSlot(padIndex);");
            Assert.True(owed < swap && swap < reset);
        }

        // A slot with no device.

        /// <summary>A copy the user made stays, whatever devices the slot has.
        /// With no device on the target slot nothing takes the tuning, and
        /// the rows the copy installed still have to reach the grid, or the
        /// autosave writes the grid's old cells back over them. On a shared
        /// layout and across layouts, by Paste and by Copy From. The sources
        /// read "(Any Device)", which a slot with no device keeps.</summary>
        [Theory]
        [InlineData(Xbox, false)]
        [InlineData(Xbox, true)]
        [InlineData(Extended, false)]
        [InlineData(Extended, true)]
        public void ACopyOntoASlotWithNoDeviceSurvivesTheAutosave(VirtualControllerType to, bool copyFrom)
        {
            Arrange(Xbox, to);
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.RemoveAll(u => u.MapTo == TargetSlot);
            _input.RefreshDeviceList();
            Assert.Null(_vm.Pads[TargetSlot].SelectedMappedDevice);
            string own = to == Xbox ? "ButtonY" : "RawBtn5";
            string copied = to == Xbox ? "ButtonA" : "RawBtn0";
            SetRows(SourceSlot, Row("ButtonA", new MappingSource { Descriptor = "Button 0" }));
            SetRows(TargetSlot, Row(own, new MappingSource { Descriptor = "Button 9" }));

            Transfer(copyFrom);
            AutosavePush();

            Assert.Equal("Button 0", Assert.Single(BaseRow(TargetSlot, copied).Sources).Descriptor);
            Assert.Null(BaseRow(TargetSlot, own));
            Assert.Equal("Button 0", GridItem(TargetSlot, copied).SourceDescriptor);
        }

        // A trigger and a two-sided axis rest at different ends.

        /// <summary>The engine's two-sided read of the target slot's row with
        /// its pad's axis 2 at the given value and the others centered.</summary>
        private short TwoSidedRead(string target, int axis2)
        {
            var state = new CustomInputState();
            Array.Fill(state.Axis, 32768);
            state.Axis[2] = axis2;
            state.Axis[5] = 0;
            Array.Fill(state.Povs, -1);
            SettingsManager.FindDeviceByInstanceGuid(TargetPad).InputState = state;
            InputManager.EndDeviceStateMemo();
            InputManager.ClearAllShiftRuntime();
            Assert.True(InputManager.TryEvaluateMappingSetBipolarAxis(state, SettingsManager.SlotMappingSets[TargetSlot],
                TargetPad.ToString(), TargetSlot, target, out short value));
            return value;
        }

        /// <summary>An Extended slot with two sticks and no trigger: raw axis
        /// 2 is the right stick's X axis.</summary>
        private void NoTriggers(int slot)
        {
            var pad = _vm.Pads[slot];
            pad.ExtendedConfig.TriggerCount = 0;
            pad.RebuildMappings();
            Assert.False(InputService.SlotLayoutOf(pad).IsTrigger("RawAxis2"));
        }

        /// <summary>A trigger rests released at the bottom of its travel, a
        /// two-sided axis at the middle. A gamepad trigger's row carried onto
        /// a two-sided axis reads its trigger as a half axis, so the released
        /// trigger leaves the new control at rest and a full pull drives it
        /// one way. Read straight, the released trigger would hold a stick to
        /// one side, turn a wheel without end or pin a CC at its bottom. A
        /// Bidirectional flag the full read ignored goes off, or the half read
        /// would take the released trigger's end too.</summary>
        [Theory]
        [InlineData(KeyboardMouse, "KbmScroll", false, false)]
        [InlineData(KeyboardMouse, "KbmScroll", false, true)]
        [InlineData(Midi, "MidiCC2", false, false)]
        [InlineData(Midi, "MidiCC2", false, true)]
        [InlineData(Extended, "RawAxis2", false, false)]
        [InlineData(Extended, "RawAxis2", false, true)]
        [InlineData(KeyboardMouse, "KbmScroll", true, false)]
        [InlineData(KeyboardMouse, "KbmScroll", true, true)]
        public void ATriggerOnATwoSidedAxisRestsAtTheMiddle(VirtualControllerType to, string landed,
            bool bidirectional, bool copyFrom)
        {
            Arrange(Xbox, to);
            if (to == Extended) NoTriggers(TargetSlot);
            var trigger = Source(SourcePad, "Axis 2");
            trigger.Bidirectional = bidirectional;
            SetRows(SourceSlot, Row("LeftTrigger", trigger));

            Transfer(copyFrom);
            AutosavePush();

            var source = Assert.Single(BaseRow(TargetSlot, landed).Sources);
            Assert.Equal("Axis 2", source.Descriptor);
            Assert.True(source.HalfAxis);
            Assert.False(source.Invert);
            Assert.False(source.Bidirectional);
            Assert.Equal(0, TwoSidedRead(landed, axis2: 0));
            Assert.True(TwoSidedRead(landed, axis2: 65535) > 30000);
        }

        /// <summary>The other way: a centered stick read straight sits half
        /// pulled on a trigger. Carried from a two-sided axis onto a trigger,
        /// it reads as a half axis, released at rest. Its Invert picks the
        /// half, so the push that drove the wheel up pulls the trigger.</summary>
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void AStickOnATriggerRestsReleased(bool invert, bool copyFrom)
        {
            Arrange(KeyboardMouse, Xbox);
            SetRows(SourceSlot, Row("KbmScroll", Source(SourcePad, "Axis 4", invert)));

            Transfer(copyFrom);
            AutosavePush();

            var source = Assert.Single(BaseRow(TargetSlot, "LeftTrigger").Sources);
            Assert.Equal("Axis 4", source.Descriptor);
            Assert.True(source.HalfAxis);
            Assert.Equal(invert, source.Invert);
            Assert.Equal(0.0, TriggerPull("LeftTrigger"));
            Assert.Equal(1.0, TriggerPullAt("LeftTrigger", axis: 4, value: invert ? 0 : 65535));
            Assert.Equal(0.0, TriggerPullAt("LeftTrigger", axis: 4, value: invert ? 65535 : 0));
        }

        /// <summary>The engine's trigger pull of the target slot's row with
        /// one axis of its pad at the given value, its other sticks centered
        /// and its triggers released.</summary>
        private double TriggerPullAt(string target, int axis, int value)
        {
            var state = new CustomInputState();
            Array.Fill(state.Axis, 32768);
            state.Axis[2] = state.Axis[5] = 0;
            state.Axis[axis] = value;
            Array.Fill(state.Povs, -1);
            SettingsManager.FindDeviceByInstanceGuid(TargetPad).InputState = state;
            InputManager.EndDeviceStateMemo();
            InputManager.ClearAllShiftRuntime();
            Assert.True(InputManager.TryEvaluateMappingSetRawTrigger(state, SettingsManager.SlotMappingSets[TargetSlot],
                TargetPad.ToString(), TargetSlot, target, out short pull));
            return (pull - short.MinValue) / 65535.0;
        }

        /// <summary>A trigger read through "(Any Device)" rests where the slot's
        /// devices rest it: a gamepad's axis 2 is its left trigger.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AnAnyDeviceTriggerRestsAtTheMiddleToo(bool copyFrom)
        {
            Arrange(Xbox, KeyboardMouse);
            SetRows(SourceSlot, Row("LeftTrigger", new MappingSource { Descriptor = "Axis 2" }));

            Transfer(copyFrom);
            AutosavePush();

            var source = Assert.Single(BaseRow(TargetSlot, "KbmScroll").Sources);
            Assert.True(source.HalfAxis);
            Assert.Equal(0, TwoSidedRead("KbmScroll", axis2: 0));
        }

        /// <summary>A button rests at the bottom of a source's travel too, and
        /// a full-axis read of a centered stick sits on its press line. A
        /// Switch pad's ZL is a button, so a mouse wheel's row carried onto it
        /// by role reads its stick as a half axis and is not pressed at rest.
        /// A gamepad trigger rests at the bottom already and keeps its full
        /// axis there.</summary>
        [Theory]
        [InlineData(KeyboardMouse, "KbmScroll", "Axis 4", true, false)]
        [InlineData(KeyboardMouse, "KbmScroll", "Axis 4", true, true)]
        [InlineData(Xbox, "LeftTrigger", "Axis 2", false, false)]
        [InlineData(Xbox, "LeftTrigger", "Axis 2", false, true)]
        public void AnAxisOnASwitchButtonIsNotPressedAtRest(VirtualControllerType from, string sourceTarget,
            string axis, bool half, bool copyFrom)
        {
            Arrange(from, Nintendo);
            string zl = ByTable("LeftTrigger", "switch-pro");
            SetRows(SourceSlot, Row(sourceTarget, Source(SourcePad, axis)));

            Transfer(copyFrom);
            AutosavePush();

            var source = Assert.Single(BaseRow(TargetSlot, zl).Sources);
            Assert.Equal(axis, source.Descriptor);
            Assert.Equal(half, source.HalfAxis);
            Assert.False(Pressed(zl));
        }

        /// <summary>A slider has no half read on a two-sided axis, so nothing
        /// can rest it at the middle. It is dropped, and a row left with no
        /// source goes with it, as a row with no counterpart does. A row with
        /// another source keeps that one.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ASliderThatCannotRestAtTheMiddleIsDropped(bool copyFrom)
        {
            Arrange(Xbox, Midi);
            SetRows(SourceSlot,
                Row("LeftTrigger", Source(SourcePad, "Slider 0")),
                Row("RightTrigger", Source(SourcePad, "Slider 1"), Source(SourcePad, "Button 7")));

            Transfer(copyFrom);
            Assert.DoesNotContain(SettingsManager.SlotMappingSets[TargetSlot].Rows, r => r.Target == "MidiCC2");
            AutosavePush();

            Assert.Null(BaseRow(TargetSlot, "MidiCC2"));
            Assert.Equal(new[] { "Button 7" }, Descriptors(BaseRow(TargetSlot, "MidiCC5")));
            Assert.DoesNotContain(SettingsManager.SlotMappingSets[TargetSlot].Rows,
                r => r.Sources.Any(s => s.Descriptor == "Slider 0" || s.Descriptor == "Slider 1"));
        }

        /// <summary>A source keeps how it reads where the new control rests
        /// at the end the copied one did, the user's own choice included: a
        /// trigger onto a trigger, and a trigger the user bound to a stick
        /// axis onto another two-sided axis. So does a source that did not
        /// rest on the copied control: a trigger on a MIDI CC sat at the CC's
        /// bottom, and on a gamepad trigger it rests released. An inverted
        /// trigger, which sat fully pulled on its own trigger, keeps its full
        /// read on a mouse wheel too.</summary>
        [Theory]
        [InlineData(Xbox, "LeftTrigger", Extended, "RawAxis2", false, false)]
        [InlineData(Xbox, "LeftTrigger", Extended, "RawAxis2", false, true)]
        [InlineData(Xbox, "LeftThumbAxisX", KeyboardMouse, "KbmMouseX", false, false)]
        [InlineData(Xbox, "LeftThumbAxisX", KeyboardMouse, "KbmMouseX", false, true)]
        [InlineData(Midi, "MidiCC2", Xbox, "LeftTrigger", false, false)]
        [InlineData(Midi, "MidiCC2", Xbox, "LeftTrigger", false, true)]
        [InlineData(Xbox, "LeftTrigger", KeyboardMouse, "KbmScroll", true, false)]
        [InlineData(Xbox, "LeftTrigger", KeyboardMouse, "KbmScroll", true, true)]
        public void ASourceThatFitsTheNewControlKeepsHowItReads(VirtualControllerType from, string sourceTarget,
            VirtualControllerType to, string landed, bool invert, bool copyFrom)
        {
            Arrange(from, to);
            SetRows(SourceSlot, Row(sourceTarget, Source(SourcePad, "Axis 2", invert)));

            Transfer(copyFrom);
            AutosavePush();

            var source = Assert.Single(BaseRow(TargetSlot, landed).Sources);
            Assert.Equal("Axis 2", source.Descriptor);
            Assert.False(source.HalfAxis);
            Assert.Equal(invert, source.Invert);
        }

        /// <summary>A row the copied slot's layout gives no position crosses
        /// under its own name only where the target layout has no position for
        /// that name either. A raw button's row left on an Xbox slot does not
        /// land on the Extended slot's raw button of that name.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ARowOfAnotherLayoutsNameCrossesNowhere(bool copyFrom)
        {
            Arrange(Xbox, Extended);
            SetRows(SourceSlot,
                Row("ButtonA", Source(SourcePad, "Button 0")),
                Row("RawBtn5", Source(SourcePad, "Button 9")));
            Assert.Null(MappingTranslation.GetPosition("RawBtn5", Xbox, false));
            Assert.Contains("RawBtn5", InputService.SlotLayoutOf(_vm.Pads[TargetSlot]).Grid);

            Transfer(copyFrom);
            AutosavePush();

            Assert.Equal("Button 0", Assert.Single(BaseRow(TargetSlot, "RawBtn0").Sources).Descriptor);
            Assert.DoesNotContain(BaseRows(TargetSlot), r => r.Sources.Any(s => s.Descriptor == "Button 9"));
        }

        /// <summary>The rest step leaves alone a source read as a half axis
        /// already, which rests at zero on every control whatever its other
        /// settings.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ASourceReadAsAHalfAxisKeepsItsSettings(bool copyFrom)
        {
            Arrange(Midi, Xbox);
            var half = Source(SourcePad, "Axis 4");
            half.HalfAxis = half.Bidirectional = true;
            SetRows(SourceSlot, Row("MidiCC2", half));

            Transfer(copyFrom);
            AutosavePush();

            var source = Assert.Single(BaseRow(TargetSlot, "LeftTrigger").Sources);
            Assert.True(source.HalfAxis);
            Assert.True(source.Bidirectional);
        }

        /// <summary>A source whose kind reads its keys keeps the input an
        /// earlier kind left in it, unread. The rest step leaves such a source
        /// alone: it is not dropped for a slider it does not read, and takes
        /// no half read for an axis it does not read. The row step is called
        /// as Copy From calls it, on rows as a slot's set holds them.</summary>
        [Fact]
        public void ASourceThatReadsItsKeysIsLeftAlone()
        {
            Arrange(Xbox, Midi);
            MappingSource Keys(string kept) => new()
            {
                DeviceGuid = SourcePad.ToString(), Kind = "Incremental", Descriptor = kept,
                ParamUp = "Button 1", ParamDown = "Button 2",
            };
            SettingsManager.SlotMappingSets[SourceSlot].Rows = new List<MappingRow>
            {
                Row("LeftTrigger", Keys("Slider 0")),
                Row("RightTrigger", Keys("Axis 5")),
            };

            InputService.TranslateSlotMappingSet(TargetSlot, SourceSlot,
                InputService.SlotLayoutOf(_vm.Pads[SourceSlot]), InputService.SlotLayoutOf(_vm.Pads[TargetSlot]));

            var rows = SettingsManager.SlotMappingSets[TargetSlot].Rows;
            var left = Assert.Single(rows.Single(r => r.Target == "MidiCC2").Sources);
            Assert.Equal("Incremental", left.Kind);
            Assert.Equal("Slider 0", left.Descriptor);
            var right = Assert.Single(rows.Single(r => r.Target == "MidiCC5").Sources);
            Assert.Equal("Incremental", right.Kind);
            Assert.Equal("Axis 5", right.Descriptor);
            Assert.False(right.HalfAxis);
        }

        /// <summary>In a formula a dropped slider leaves a blank, so every
        /// later letter keeps its place. The trigger gave each source its own
        /// letter, and a blank that looks like the next source's pair does not
        /// fold with it into one on a stick axis.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ASliderDroppedFromAFormulaLeavesItsLetter(bool copyFrom)
        {
            Arrange(Xbox, Extended);
            NoTriggers(TargetSlot);
            SetRows(SourceSlot, Formula("LeftTrigger", "a + b",
                Source(SourcePad, "Slider 0"), new MappingSource { Descriptor = "Button 5", Invert = true }));
            Assert.Equal(2, GridItem(SourceSlot, "LeftTrigger").PositionalSourceCount);

            Transfer(copyFrom);

            var row = SettingsManager.SlotMappingSets[TargetSlot].Rows.Single(r => r.Target == "RawAxis2");
            Assert.Equal("Custom", row.CombineMode);
            Assert.Equal("a + b", row.CombineExpression);
            Assert.Equal(new[] { "", "Button 5" }, Descriptors(row));
            Assert.True(row.SuppressBipolarPair);

            AutosavePush();
            Assert.True(BaseRow(TargetSlot, "RawAxis2").SuppressBipolarPair);
            Assert.Equal(2, GridItem(TargetSlot, "RawAxis2").PositionalSourceCount);
        }

        /// <summary>A grid keeps a row for each of its cells, bound or not.
        /// Where two copied rows land on one control, an empty one gives way
        /// to the one with a binding.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AnEmptyRowGivesWayWhereTwoLandOnOneControl(bool copyFrom)
        {
            Arrange(Midi, Nintendo);
            SetRows(SourceSlot, Row("MidiNote11"), Row("MidiNote12", Source(SourcePad, "Button 12")));

            Transfer(copyFrom);
            AutosavePush();

            Assert.Equal("Button 12",
                Assert.Single(BaseRow(TargetSlot, ByTable("ButtonMute", "switch-pro")).Sources).Descriptor);
        }

        // A shared layout: what Paste and Copy From did before, to the letter.

        private static readonly Guid SecondSourcePad = new("c1055011-1111-2222-3333-444444444444");
        private static readonly Guid SecondTargetPad = new("c1055012-1111-2222-3333-444444444444");
        private static readonly Guid SecondModel = new("c1055013-1111-2222-3333-444444444444");
        private static readonly Guid KeysOnSource = new("c1055021-1111-2222-3333-444444444444");
        private static readonly Guid KeysModel = new("c1055023-1111-2222-3333-444444444444");
        private static readonly Guid WheelOnTarget = new("c1055032-1111-2222-3333-444444444444");
        private static readonly Guid WheelModel = new("c1055033-1111-2222-3333-444444444444");
        private static readonly Guid OnBothSlots = new("c1055041-1111-2222-3333-444444444444");
        private static readonly Guid OnBothModel = new("c1055043-1111-2222-3333-444444444444");

        /// <summary>Two Xbox slots with several devices each: a pad of one
        /// model on each, a pad of a second model on each, a keyboard on the
        /// source only, a wheel on the target only and one device on both.
        /// The source holds shift layers, multi-source rows of every combine
        /// mode, source kinds, menus and the slot extras. The target holds
        /// its own of each.</summary>
        private void ArrangeBusySharedLayout()
        {
            Arrange(Xbox, Xbox);
            AddDevice(SecondSourcePad, SecondModel, SourceSlot);
            AddDevice(SecondTargetPad, SecondModel, TargetSlot);
            AddDevice(KeysOnSource, KeysModel, SourceSlot);
            AddDevice(WheelOnTarget, WheelModel, TargetSlot);
            AddDevice(OnBothSlots, OnBothModel, SourceSlot);
            AddDevice(OnBothSlots, OnBothModel, TargetSlot);
            _input.RefreshDeviceList();
            Select(SourceSlot, SourcePad);
            Select(TargetSlot, TargetPad);

            SettingsManager.SlotMappingSets[SourceSlot] = BusySource();
            SettingsManager.SlotMappingSets[TargetSlot] = BusyTarget();
            foreach (int slot in new[] { SourceSlot, TargetSlot })
            {
                _vm.Pads[slot].RebuildLayerTabs(SettingsManager.SlotMappingSets[slot].ShiftActivators);
                InputService.RefreshMappingsToViewModel(_vm.Pads[slot]);
            }
        }

        private static MappingRow OnLayer(string layer, MappingRow row)
        {
            row.LayerMask = layer;
            return row;
        }

        private static MappingSet BusySource()
        {
            string pad = SourcePad.ToString(), second = SecondSourcePad.ToString();
            string keys = KeysOnSource.ToString(), both = OnBothSlots.ToString();

            var tuned = Source(SourcePad, "Axis 0");
            tuned.Sensitivity = 1.5;
            tuned.DeadZone = 20;
            tuned.ParamCurveExponent = 2;
            tuned.GateDescriptor = "Button 8";

            var formula = Row("LeftTrigger", Source(SourcePad, "Axis 2"), Source(KeysOnSource, "Button 5"),
                Source(SecondSourcePad, "Axis 2"));
            formula.CombineMode = "Custom";
            formula.CombineExpression = "a * 0.5 + b + c";

            var trim = Row("RightTrigger", Source(SourcePad, "Axis 5"), Source(SourcePad, "Axis 4", invert: true));
            trim.CombineMode = "StickTrim";
            trim.TrimDeadzone = 30;
            trim.TrimRate = 80;
            trim.TrimResetOnRelease = false;

            var summed = Row("RightThumbAxisX", Source(SecondSourcePad, "Button 13"),
                Source(SecondSourcePad, "Button 14", invert: true), Source(OnBothSlots, "Axis 3"));
            summed.CombineMode = "Sum";

            var apart = OnLayer("Shift1", Row("RightThumbAxisY", Source(SourcePad, "Button 4"),
                Source(SourcePad, "Button 5", invert: true)));
            apart.CombineMode = "Custom";
            apart.CombineExpression = "a - b";
            apart.SuppressBipolarPair = true;

            var off = OnLayer("Shift1", Row("ButtonB"));
            off.NoInherit = true;

            var lean = Row(MappingSetMigrator.MotionPitchTarget, Source(SourcePad, "Axis 1"));
            lean.MotionResponse = MappingRow.MotionResponseAngle;
            lean.MotionSpeed = 900;
            lean.MotionMinSpeed = 30;
            lean.MotionAngle = 45;
            lean.MotionDeadzone = 15;

            return new MappingSet
            {
                Authoritative = true,
                WorkshopLeftStickDeadZoneShape = "2",
                WorkshopGyroEngageDescriptor = "Button 9",
                BaseLayerName = "Driving", BaseColor = "#FF112233", BaseIcon = "D",
                RumbleAudio = new RumbleAudioConfig
                {
                    Enabled = true, EndpointId = "endpoint-a", MasterGainPercent = 73, ChannelMode = "Stereo",
                },
                SocdMode = "Neutral", SocdPairs = "DPadLeft:DPadRight",
                KeepAwakeEnabled = true, KeepAwakeAxis = "RX", KeepAwakeDeflection = 30, KeepAwakeMotion = true,
                ShiftActivators = new List<ShiftActivator>
                {
                    new()
                    {
                        DeviceGuid = pad, Descriptor = "Button 6", LayerMask = "Shift1", LayerName = "Pit",
                        InheritUnmapped = true, Color = "#FF00FF00", Kind = "Chord",
                        ChordSecondDeviceGuid = second, ChordSecondDescriptor = "Button 7",
                    },
                    // Its controller's model is not on the target slot.
                    new() { DeviceGuid = keys, Descriptor = "Button 40", Mode = "Toggle", LayerMask = "Shift2", LayerName = "Keys" },
                    new()
                    {
                        DeviceGuid = both, Descriptor = "Button 1", Mode = "Cycle", LayerMask = "Shift3",
                        CycleLayers = "Shift3|Shift1", CyclePrevDeviceGuid = keys, CyclePrevDescriptor = "Button 41",
                        AxisHalf = true, AxisInvert = true,
                    },
                },
                Menus = new List<MenuDefinitionEntry>
                {
                    new() { MenuId = 4, Name = "Pinned", DeviceGuid = pad },
                    new() { MenuId = 5, Name = "Lost", DeviceGuid = keys },
                    new() { MenuId = 6, Name = "Any", LayerMask = "Shift1" },
                },
                Rows = new List<MappingRow>
                {
                    Row("ButtonA", Source(SourcePad, "Button 0")),
                    Row("ButtonB", Source(SourcePad, "Button 1"), Source(SecondSourcePad, "Button 1"),
                        Source(KeysOnSource, "Button 30")),
                    Row("LeftThumbAxisX", tuned),
                    Row("LeftThumbAxisY", Source(KeysOnSource, "Button 17"), Source(KeysOnSource, "Button 31", invert: true)),
                    summed,
                    formula,
                    trim,
                    Row("DPadUp", Source(SourcePad, "POV 0 Up"), new MappingSource
                    {
                        Kind = "InvertOnHold", DeviceGuid = second, ParamModifier = "Button 9", ParamModifierDeviceGuid = second,
                    }),
                    Row("ButtonX", new MappingSource
                    {
                        Kind = "Incremental", DeviceGuid = pad, ParamRate = 0.75,
                        ParamUp = "Button 2", ParamUpDeviceGuid = second,
                        ParamDown = "Button 3", ParamDownDeviceGuid = pad,
                    }),
                    Row("ButtonY", new MappingSource { Kind = "Toggle", Descriptor = "Button 3" }),
                    Row(MappingSetMigrator.MotionGyroTarget, Source(SourcePad, MappingSetMigrator.MotionGyroSourceDescriptor)),
                    lean,
                    OnLayer("Shift1", Row("ButtonA", Source(SecondSourcePad, "Button 7"))),
                    off,
                    apart,
                    OnLayer("Shift3", Row("LeftThumbAxisX", Source(OnBothSlots, "Axis 0"))),
                },
            };
        }

        private static MappingSet BusyTarget() => new()
        {
            BaseLayerName = "Old base", BaseColor = "#FF445566",
            RumbleAudio = new RumbleAudioConfig { Enabled = true, EndpointId = "endpoint-b", MasterGainPercent = 20 },
            SocdMode = "LastWins", SocdPairs = "DPadUp:DPadDown",
            KeepAwakeEnabled = true, KeepAwakeAxis = "LY", KeepAwakeDeflection = 12,
            ShiftActivators = new List<ShiftActivator>
            {
                new() { DeviceGuid = TargetPad.ToString(), Descriptor = "Button 5", LayerMask = "Old", LayerName = "Old layer" },
            },
            Menus = new List<MenuDefinitionEntry> { new() { MenuId = 9, Name = "Old menu" } },
            Rows = new List<MappingRow>
            {
                Row("ButtonA", Source(TargetPad, "Button 9")),
                Row("ButtonStart", Source(WheelOnTarget, "Button 2")),
                Row("RightThumbAxisY", Source(TargetPad, "Button 11"), Source(TargetPad, "Button 12", invert: true)),
                OnLayer("Old", Row("ButtonB", Source(SecondTargetPad, "Button 3"))),
            },
        };

        /// <summary>Every value of a mapping set that differs from a new
        /// object's, property by property and item by item. A property added
        /// later shows only once something sets it.</summary>
        private static string Dump(object value)
        {
            var text = new StringBuilder();
            Dump(text, value, "");
            return text.ToString();
        }

        private static void Dump(StringBuilder text, object value, string indent)
        {
            var type = value.GetType();
            object blank = Activator.CreateInstance(type);
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0
                    && p.GetCustomAttribute<XmlIgnoreAttribute>() == null)
                .OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                object current = property.GetValue(value);
                if (current == null) continue;
                if (current is string || current is Enum || current.GetType().IsPrimitive)
                {
                    if (!Equals(current, property.GetValue(blank)))
                        text.Append(indent).Append(property.Name).Append('=')
                            .Append(Convert.ToString(current, CultureInfo.InvariantCulture)).Append('\n');
                }
                else if (current is IEnumerable items)
                {
                    int index = 0;
                    foreach (object item in items)
                    {
                        text.Append(indent).Append(property.Name).Append('[').Append(index++).Append("]\n");
                        if (item == null) text.Append(indent).Append("  (none)\n");
                        else Dump(text, item, indent + "  ");
                    }
                }
                else
                {
                    text.Append(indent).Append(property.Name).Append('\n');
                    Dump(text, current, indent + "  ");
                }
            }
        }

        /// <summary>A shared-layout Paste and Copy From are what they were
        /// before the Base rows began to cross layouts. The expected text is
        /// the target slot's mapping set as the build before that change left
        /// it, captured by running this same arrangement on it. Copy From
        /// left the same set as Paste and, over it, the source's Workshop
        /// ownership and stamps, which a Paste does not carry.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void OnASharedLayoutTheResultIsWhatItWasBefore(bool copyFrom)
        {
            ArrangeBusySharedLayout();

            Transfer(copyFrom);
            AutosavePush();

            string result = Dump(SettingsManager.SlotMappingSets[TargetSlot]);
            _output.WriteLine(result);
            string pasted = SharedLayoutPasteResult.Replace("\r\n", "\n") + "\n";
            Assert.Equal(copyFrom
                ? "Authoritative=True\n" + pasted
                    + "WorkshopGyroEngageDescriptor=Button 9\nWorkshopLeftStickDeadZoneShape=2\n"
                : pasted, result);
        }

        private const string SharedLayoutPasteResult = """
            BaseColor=#FF112233
            BaseIcon=D
            BaseLayerName=Driving
            KeepAwakeAxis=RX
            KeepAwakeDeflection=30
            KeepAwakeEnabled=True
            KeepAwakeMotion=True
            Menus[0]
              DeviceGuid=c1055002-1111-2222-3333-444444444444
              MenuId=4
              Name=Pinned
            Menus[1]
              MenuId=5
              Name=Lost
            Menus[2]
              LayerMask=Shift1
              MenuId=6
              Name=Any
            Rows[0]
              Sources[0]
                Descriptor=Button 0
                DeviceGuid=c1055002-1111-2222-3333-444444444444
              Target=ButtonA
            Rows[1]
              CombineMode=OR
              Sources[0]
                Descriptor=Button 1
                DeviceGuid=c1055002-1111-2222-3333-444444444444
              Sources[1]
                Descriptor=Button 1
                DeviceGuid=c1055012-1111-2222-3333-444444444444
              Target=ButtonB
            Rows[2]
              Sources[0]
                DeadZone=20
                Descriptor=Axis 0
                DeviceGuid=c1055002-1111-2222-3333-444444444444
                GateDescriptor=Button 8
                ParamCurveExponent=2
                Sensitivity=1.5
              Target=LeftThumbAxisX
            Rows[3]
              CombineMode=MaxAbs
              Target=LeftThumbAxisY
            Rows[4]
              CombineMode=Sum
              Sources[0]
                Descriptor=Button 13
                DeviceGuid=c1055012-1111-2222-3333-444444444444
              Sources[1]
                Descriptor=Button 14
                DeviceGuid=c1055012-1111-2222-3333-444444444444
                Invert=True
              Sources[2]
                Descriptor=Axis 3
                DeviceGuid=c1055041-1111-2222-3333-444444444444
              Target=RightThumbAxisX
            Rows[5]
              CombineExpression=a * 0.5 + b + c
              CombineMode=Custom
              Sources[0]
                Descriptor=Axis 2
                DeviceGuid=c1055002-1111-2222-3333-444444444444
              Sources[1]
              Sources[2]
                Descriptor=Axis 2
                DeviceGuid=c1055012-1111-2222-3333-444444444444
              Target=LeftTrigger
            Rows[6]
              CombineMode=StickTrim
              Sources[0]
                Descriptor=Axis 5
                DeviceGuid=c1055002-1111-2222-3333-444444444444
              Sources[1]
                Descriptor=Axis 4
                DeviceGuid=c1055002-1111-2222-3333-444444444444
                Invert=True
              Target=RightTrigger
              TrimDeadzone=30
              TrimRate=80
              TrimResetOnRelease=False
            Rows[7]
              CombineMode=OR
              Sources[0]
                Descriptor=POV 0 Up
                DeviceGuid=c1055002-1111-2222-3333-444444444444
              Sources[1]
                DeviceGuid=c1055012-1111-2222-3333-444444444444
                Kind=InvertOnHold
                ParamModifier=Button 9
                ParamModifierDeviceGuid=c1055012-1111-2222-3333-444444444444
              Target=DPadUp
            Rows[8]
              CombineMode=OR
              Sources[0]
                DeviceGuid=c1055002-1111-2222-3333-444444444444
                Kind=Incremental
                ParamDown=Button 3
                ParamDownDeviceGuid=c1055002-1111-2222-3333-444444444444
                ParamRate=0.75
                ParamUp=Button 2
                ParamUpDeviceGuid=c1055012-1111-2222-3333-444444444444
              Target=ButtonX
            Rows[9]
              Sources[0]
                Descriptor=Button 3
                Kind=Toggle
              Target=ButtonY
            Rows[10]
              Sources[0]
                Descriptor=Motion Gyro
                DeviceGuid=c1055002-1111-2222-3333-444444444444
              Target=MotionGyro
            Rows[11]
              MotionAngle=45
              MotionDeadzone=15
              MotionMinSpeed=30
              MotionResponse=Angle
              MotionSpeed=900
              Sources[0]
                Descriptor=Axis 1
                DeviceGuid=c1055002-1111-2222-3333-444444444444
              Target=MotionPitch
            Rows[12]
              LayerMask=Shift1
              Sources[0]
                Descriptor=Button 7
                DeviceGuid=c1055012-1111-2222-3333-444444444444
              Target=ButtonA
            Rows[13]
              LayerMask=Shift1
              NoInherit=True
              Target=ButtonB
            Rows[14]
              CombineExpression=a - b
              CombineMode=Custom
              LayerMask=Shift1
              Sources[0]
                Descriptor=Button 4
                DeviceGuid=c1055002-1111-2222-3333-444444444444
              Sources[1]
                Descriptor=Button 5
                DeviceGuid=c1055002-1111-2222-3333-444444444444
                Invert=True
              SuppressBipolarPair=True
              Target=RightThumbAxisY
            Rows[15]
              LayerMask=Shift3
              Sources[0]
                Descriptor=Axis 0
                DeviceGuid=c1055041-1111-2222-3333-444444444444
              Target=LeftThumbAxisX
            Rows[16]
              Target=LeftShoulder
            Rows[17]
              Target=RightShoulder
            Rows[18]
              Target=ButtonBack
            Rows[19]
              Target=ButtonStart
            Rows[20]
              Target=ButtonGuide
            Rows[21]
              Target=ButtonShare
            Rows[22]
              Target=LeftThumbButton
            Rows[23]
              Target=RightThumbButton
            Rows[24]
              Target=DPadDown
            Rows[25]
              Target=DPadLeft
            Rows[26]
              Target=DPadRight
            Rows[27]
              Target=RightThumbAxisY
            RumbleAudio
              ChannelMode=Stereo
              Enabled=True
              EndpointId=endpoint-a
              MasterGainPercent=73
            ShiftActivators[0]
              ChordSecondDescriptor=Button 7
              ChordSecondDeviceGuid=c1055012-1111-2222-3333-444444444444
              Color=#FF00FF00
              Descriptor=Button 6
              DeviceGuid=c1055002-1111-2222-3333-444444444444
              InheritUnmapped=True
              Kind=Chord
              LayerMask=Shift1
              LayerName=Pit
            ShiftActivators[1]
              AxisHalf=True
              AxisInvert=True
              CycleLayers=Shift3|Shift1
              Descriptor=Button 1
              DeviceGuid=c1055041-1111-2222-3333-444444444444
              LayerMask=Shift3
              Mode=Cycle
            SocdMode=Neutral
            SocdPairs=DPadLeft:DPadRight
            """;
    }
}

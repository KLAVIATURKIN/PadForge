using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.Engine.Menus;
using PadForge.Services;
using Xunit.Abstractions;

namespace PadForge.Tests;

[Collection("SettingsManagerStatics")]
public sealed class EmptySlotClipboardTests : IDisposable
{
    readonly MappingSet[] saved = SettingsManager.SlotMappingSets;
    readonly ITestOutputHelper output;
    public EmptySlotClipboardTests(ITestOutputHelper output)
    {
        this.output = output;
        SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
    }
    public void Dispose() => SettingsManager.SlotMappingSets = saved;

    static MappingSet Busy() => new()
    {
        Rows = [new MappingRow { Target = "ButtonA", Sources = [new MappingSource { Descriptor = "Button 1" }] }],
        ShiftActivators = [new ShiftActivator { LayerMask = "Shift1", Mode = "Passive", LayerName = "Held" }],
        BaseLayerName = "Base name", BaseColor = "#FF0000", BaseIcon = "base",
        Menus = [new MenuDefinitionEntry { MenuId = 4, Name = "Menu" }],
        RumbleAudio = new RumbleAudioConfig { Enabled = true, EndpointId = "explicit-endpoint" },
        SocdMode = "Neutral", SocdPairs = "DPadLeft|DPadRight",
        KeepAwakeEnabled = true, KeepAwakeAxis = "LeftThumbX", KeepAwakeDeflection = 42, KeepAwakeMotion = true
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyWholeSlotRowsReplaceBusyRows(bool throughJson)
    {
        var source = new PadSetting { SlotMultiSourceRows = [], DeviceScopedMultiSourceRows = [new MappingRow { Target = "ButtonB" }] };
        if (throughJson) source = PadSetting.FromJson(source.ToJson());
        SettingsManager.SlotMappingSets[1] = Busy();
        Assert.Single(SettingsManager.SlotMappingSets[1].Rows);
        InputService.ApplySlotRowsFromClipboard(1,
            new PadSetting { SlotMultiSourceRows = [new MappingRow { Target = "ButtonB" }] }, sameLayout: true);
        Assert.Equal("ButtonB", Assert.Single(SettingsManager.SlotMappingSets[1].Rows).Target);
        InputService.ApplySlotRowsFromClipboard(1, source, sameLayout: true);
        output.WriteLine($"throughJson={throughJson} rows={SettingsManager.SlotMappingSets[1].Rows.Count}");
        Assert.Empty(SettingsManager.SlotMappingSets[1].Rows);
        Assert.Null(source.DeviceScopedMultiSourceRows);
    }

    [Theory]
    [InlineData("shift", false)]
    [InlineData("menus", false)]
    [InlineData("extras", false)]
    [InlineData("shift", true)]
    [InlineData("menus", true)]
    [InlineData("extras", true)]
    public void DefaultSectionClearsAnAuthoredDestination(string section, bool absentSet)
    {
        SettingsManager.SlotMappingSets[0] = Busy();
        var positive = Capture(section);
        SettingsManager.SlotMappingSets[1] = new MappingSet();
        Apply(section, positive, sameLayout: true);
        AssertAuthored(section);

        SettingsManager.SlotMappingSets[0] = absentSet ? null : new MappingSet();
        var blank = Capture(section);
        Apply(section, blank, sameLayout: true);
        var got = SettingsManager.SlotMappingSets[1];
        output.WriteLine($"{section}/absentSet={absentSet}: shift={got.ShiftActivators.Count} menus={got.Menus.Count} keepAwake={got.KeepAwakeEnabled}");
        if (section == "shift")
        {
            Assert.Empty(got.ShiftActivators);
            Assert.Equal("", got.BaseLayerName);
            Assert.Equal("", got.BaseColor);
            Assert.Equal("", got.BaseIcon);
        }
        else if (section == "menus") Assert.Empty(got.Menus);
        else
        {
            Assert.Null(got.RumbleAudio);
            Assert.Equal("", got.SocdMode);
            Assert.Equal("", got.SocdPairs);
            Assert.False(got.KeepAwakeEnabled);
            Assert.False(got.KeepAwakeMotion);
            Assert.Equal("", got.KeepAwakeAxis);
            Assert.Equal(0, got.KeepAwakeDeflection);
        }
    }

    PadSetting Capture(string section)
    {
        var source = new PadSetting();
        if (section == "shift") source.SlotShiftActivatorsJson = InputService.BuildShiftLayerSnapshotJson(0);
        else if (section == "menus") source.SlotMenusJson = InputService.BuildMenusSnapshotJson(0);
        else source.SlotSetExtrasJson = InputService.BuildSlotSetExtrasJson(0);
        return PadSetting.FromJson(source.ToJson());
    }
    static void Apply(string section, PadSetting source, bool sameLayout)
    {
        if (section == "shift") InputService.ApplyShiftLayerSnapshotJson(1, source.SlotShiftActivatorsJson);
        else if (section == "menus") InputService.ApplyMenusSnapshotJson(1, source.SlotMenusJson);
        else InputService.ApplySlotSetExtrasJson(1, source.SlotSetExtrasJson, sameLayout);
    }
    static void AssertAuthored(string section)
    {
        var got = SettingsManager.SlotMappingSets[1];
        if (section == "shift") Assert.Single(got.ShiftActivators);
        else if (section == "menus") Assert.Single(got.Menus);
        else { Assert.True(got.KeepAwakeEnabled); Assert.NotNull(got.RumbleAudio); }
    }

    [Fact]
    public void AbsentLegacySectionsLeaveTheirDestinationsAlone()
    {
        SettingsManager.SlotMappingSets[1] = Busy();
        var legacy = PadSetting.FromJson(new PadSetting { ButtonA = "Button 7" }.ToJson());
        InputService.ApplySlotRowsFromClipboard(1, legacy, sameLayout: true);
        Apply("shift", legacy, true);
        Apply("menus", legacy, true);
        Apply("extras", legacy, true);
        Assert.Single(SettingsManager.SlotMappingSets[1].Rows);
        AssertAuthored("shift");
        AssertAuthored("menus");
        AssertAuthored("extras");
    }

    [Fact]
    public void CrossLayoutKeepsRowsAndSocdWhileReplacingOtherExtras()
    {
        SettingsManager.SlotMappingSets[0] = new MappingSet();
        SettingsManager.SlotMappingSets[1] = Busy();
        var source = Capture("extras");
        source.SlotMultiSourceRows = [];
        InputService.ApplySlotRowsFromClipboard(1, source, sameLayout: false);
        Apply("extras", source, sameLayout: false);
        var got = SettingsManager.SlotMappingSets[1];
        Assert.Single(got.Rows);
        Assert.Equal("Neutral", got.SocdMode);
        Assert.Equal("DPadLeft|DPadRight", got.SocdPairs);
        Assert.False(got.KeepAwakeEnabled);
        Assert.Null(got.RumbleAudio);
    }
}

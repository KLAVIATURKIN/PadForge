using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit.Abstractions;

namespace PadForge.Tests;

[Collection("SettingsManagerStatics")]
public sealed class BidirectionalMigrationTests(ITestOutputHelper output)
{
    const string Device = "11111111-1111-1111-1111-111111111111";
    public static IEnumerable<object[]> PrimaryTargets()
    {
        foreach (string target in new[] { "ButtonA", "LeftTrigger", "TouchpadContact1", "LeftThumbAxisX",
            "RawAxis0", "RawBtn0", "MidiCC0", "MidiNote0", "KbmMouseX", "KbmKey41", "VrLGrip", "VrLSystem" })
            foreach (bool bidirectional in new[] { false, true }) yield return [target, bidirectional];
    }
    public static IEnumerable<object[]> NegativeTargets()
    {
        foreach (string target in new[] { "LeftThumbAxisX", "RawAxis0", "MidiCC0", "KbmMouseX", "VrLStickX" })
            foreach (bool bidirectional in new[] { false, true }) yield return [target, bidirectional];
    }

    static void Set(PadSetting settings, string target, string descriptor)
    {
        if (target.StartsWith("Raw", StringComparison.Ordinal)) settings.SetRawMapping(target, descriptor);
        else if (target.StartsWith("Midi", StringComparison.Ordinal)) settings.SetMidiMapping(target, descriptor);
        else if (target.StartsWith("Kbm", StringComparison.Ordinal)) settings.SetKbmMapping(target, descriptor);
        else if (target.StartsWith("Vr", StringComparison.Ordinal)) settings.SetVrMapping(target, descriptor);
        else typeof(PadSetting).GetProperty(target)!.SetValue(settings, descriptor);
        settings.FlushRawMappings();
        settings.FlushMidiMappings();
        settings.FlushKbmMappings();
        settings.FlushVrMappings();
    }

    static MappingSet Build(PadSetting settings) => MappingSetMigrator.BuildFromLegacy(0,
        [(DeviceGuid: Device, PadSetting: settings, IsGamepadEligible: true)]);
    static CustomInputState State(int value)
    {
        var state = new CustomInputState();
        state.Axis[6] = value;
        return state;
    }
    static bool Read(string target, int value, MappingSource source)
    {
        var state = State(value);
        if (target is "LeftTrigger" or "VrLGrip") return SourceCoercion.EvaluateForTriggerTarget(state, source) > .5f;
        if (target is "LeftThumbAxisX" or "RawAxis0" or "MidiCC0" or "KbmMouseX")
            return SourceCoercion.EvaluateForBipolarAxisTarget(state, source) > .5f;
        return SourceCoercion.EvaluateForButtonTarget(state, source, 50);
    }

    [Theory]
    [MemberData(nameof(PrimaryTargets))]
    public void EveryPrimarySurfaceRetainsEitherSideInput(string target, bool bidirectional)
    {
        var settings = new PadSetting();
        Set(settings, target, "HAxis 6");
        settings.SetMappingBidirectional(target, bidirectional ? "1" : "0");
        settings.SetMappingDeadZone(target, "23");
        var source = Assert.Single(Assert.Single(Build(settings).Rows, row => row.Target == target).Sources);
        Assert.True(source.HalfAxis);
        Assert.Equal(23, source.DeadZone);
        Assert.True(Read(target, 65535, source));
        bool lower = Read(target, 0, source);
        output.WriteLine($"{target}: expectedEither={bidirectional} storedEither={source.Bidirectional} lowerHalf={lower}");
        Assert.Equal(bidirectional, lower);
        Assert.Equal(bidirectional, source.Bidirectional);
        Assert.False(Read(target, 32768, source));
    }

    [Theory]
    [MemberData(nameof(NegativeTargets))]
    public void NegativeLegRetainsEitherSideInputAndItsOutputSign(string target, bool bidirectional)
    {
        var settings = new PadSetting();
        Set(settings, target + "Neg", "HAxis 6");
        settings.SetMappingBidirectional(target, bidirectional ? "1" : "0");
        var source = Assert.Single(Assert.Single(Build(settings).Rows, row => row.Target == target).Sources);
        Assert.True(source.HalfAxis);
        Assert.True(source.InvertOutput);
        Assert.False(source.Invert);
        Assert.Equal(-1f, SourceCoercion.EvaluateForBipolarAxisTarget(State(65535), source), 3);
        Assert.Equal(bidirectional ? -1f : 0f, SourceCoercion.EvaluateForBipolarAxisTarget(State(0), source), 3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CombinedPovPreservesItsStoredCompanion(bool bidirectional)
    {
        var settings = new PadSetting { DPad = "POV 0" };
        settings.SetMappingBidirectional("DPad", bidirectional ? "1" : "0");
        var source = Assert.Single(Assert.Single(Build(settings).Rows, row => row.Target == "DPad").Sources);
        Assert.Equal("POV 0", source.Descriptor);
        Assert.Equal(bidirectional, source.Bidirectional);
    }

    [Fact]
    public void ContributionsKeepTheirOwnDeviceFlag()
    {
        var either = new PadSetting { ButtonA = "HAxis 6" };
        var one = new PadSetting { ButtonA = "HAxis 6" };
        either.SetMappingBidirectional("ButtonA", "1");
        var set = MappingSetMigrator.BuildFromLegacy(0,
            [(DeviceGuid: Device, PadSetting: either, IsGamepadEligible: true),
             (DeviceGuid: "22222222-2222-2222-2222-222222222222", PadSetting: one, IsGamepadEligible: true)]);
        var row = Assert.Single(set.Rows, row => row.Target == "ButtonA");
        Assert.Equal(2, row.Sources.Count);
        Assert.True(Read("ButtonA", 0, row.Sources[0]));
        Assert.False(Read("ButtonA", 0, row.Sources[1]));
    }
}

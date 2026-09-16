using System.Reflection;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit.Abstractions;

namespace PadForge.Tests;

public sealed class MappingDictionaryLifetimeTests(ITestOutputHelper output)
{
    static readonly string[][] Families =
    [
        ["RawMapping", "_rawDictLock", "RawAxis0", "FlushRawMappings"],
        ["MidiMapping", "_midiDictLock", "MidiCC0", "FlushMidiMappings"],
        ["KbmMapping", "_kbmDictLock", "KbmMouseX", "FlushKbmMappings"],
        ["VrMapping", "_vrDictLock", "VrLStickX", "FlushVrMappings"],
        ["MappingDeadZone", "_mappingDeadZoneDictLock", "ButtonA", "FlushMappingDeadZones"],
        ["MappingBidirectional", "_mappingBidirectionalDictLock", "ButtonA", "FlushMappingBidirectional"]
    ];
    public static IEnumerable<object[]> WaitingOperations()
    {
        foreach (var family in Families)
            foreach (string operation in new[] { "get", "set", "flush" })
                foreach (bool copy in new[] { false, true }) yield return [family[0], operation, copy];
    }
    public static IEnumerable<object[]> Replacements()
    {
        foreach (var family in Families)
            foreach (bool copy in new[] { false, true }) yield return [family[0], copy];
    }
    public static IEnumerable<object[]> Seeds() => Families.Select(f => new object[] { f[0] });
    static string[] Family(string name) => Families.Single(f => f[0] == name);
    static string Value(string family, int step) => family switch
    {
        "MappingBidirectional" => step == 2 ? "" : "1",
        "MappingDeadZone" => (20 + step).ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => "Button " + step
    };
    static Action<string, string> Setter(PadSetting settings, string name)
        => typeof(PadSetting).GetMethod("Set" + name)!.CreateDelegate<Action<string, string>>(settings);
    static Func<string, string> Getter(PadSetting settings, string name)
        => typeof(PadSetting).GetMethod("Get" + name)!.CreateDelegate<Func<string, string>>(settings);
    static object Gate(PadSetting settings, string name)
        => typeof(PadSetting).GetField(Family(name)[1], BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(settings)!;

    [Theory]
    [MemberData(nameof(WaitingOperations))]
    public void WaitingOperationUsesTheReplacementDictionary(string family, string operation, bool copy)
    {
        var settings = new PadSetting();
        var incoming = new PadSetting();
        var entry = Family(family);
        var get = Getter(settings, family);
        var set = Setter(settings, family);
        set(entry[2], Value(family, 1));
        Setter(incoming, family)(entry[2], Value(family, 2));
        Assert.Equal(Value(family, 1), get(entry[2]));
        var flush = typeof(PadSetting).GetMethod(entry[3])!.CreateDelegate<Action>(settings);
        using var started = new ManualResetEventSlim();
        Exception fault = null;
        string observed = null;
        var worker = new Thread(() =>
        {
            started.Set();
            try
            {
                if (operation == "get") observed = get(entry[2]);
                else if (operation == "set") set(entry[2], Value(family, 3));
                else flush();
            }
            catch (Exception error) { fault = error; }
        }) { IsBackground = true };
        try
        {
            lock (Gate(settings, family))
            {
                worker.Start();
                Assert.True(started.Wait(2000));
                Assert.True(SpinWait.SpinUntil(() => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0, 2000));
                if (copy) settings.CopyFrom(incoming);
                else settings.ClearMappingDescriptors();
            }
            Assert.True(worker.Join(2000));
            output.WriteLine($"{family}/{operation}/copy={copy}: fault={fault?.GetType().Name ?? "none"} observed={observed ?? "none"}");
            Assert.Null(fault);
            string expected = operation == "set" ? Value(family, 3) : copy ? Value(family, 2) : "";
            Assert.Equal(expected, get(entry[2]));
            if (operation == "get") Assert.Equal(expected, observed);
        }
        finally { if (worker.IsAlive) Assert.True(worker.Join(2000)); }
    }

    [Theory]
    [MemberData(nameof(Replacements))]
    public void ReplacementWaitsForTheDictionaryOwner(string family, bool copy)
        => CheckReplacement(family, settings =>
        {
            if (copy) settings.CopyFrom(new PadSetting());
            else settings.ClearMappingDescriptors();
        });

    [Theory]
    [InlineData("RawMapping", VirtualControllerType.Extended, true)]
    [InlineData("MidiMapping", VirtualControllerType.Midi, false)]
    [InlineData("KbmMapping", VirtualControllerType.KeyboardMouse, false)]
    [InlineData("VrMapping", VirtualControllerType.Vr, false)]
    public void TranslatedReplacementWaitsForTheDictionaryOwner(string family, VirtualControllerType target, bool extended)
        => CheckReplacement(family, settings => settings.CopyFromTranslated(new PadSetting(),
            VirtualControllerType.Xbox, false, target, extended));

    void CheckReplacement(string family, Action<PadSetting> replace)
    {
        var settings = new PadSetting();
        string key = Family(family)[2];
        Setter(settings, family)(key, Value(family, 1));
        Assert.Equal(Value(family, 1), Getter(settings, family)(key));
        using var started = new ManualResetEventSlim();
        using var done = new ManualResetEventSlim();
        Exception fault = null;
        var worker = new Thread(() =>
        {
            started.Set();
            try { replace(settings); }
            catch (Exception error) { fault = error; }
            finally { done.Set(); }
        }) { IsBackground = true };
        bool early = false;
        try
        {
            lock (Gate(settings, family))
            {
                worker.Start();
                Assert.True(started.Wait(2000));
                early = done.Wait(150);
            }
            Assert.True(worker.Join(2000));
            output.WriteLine($"{family}: replacementCompletedDuringRead={early}");
            Assert.Null(fault);
            Assert.False(early);
            Assert.Equal("", Getter(settings, family)(key));
        }
        finally { if (worker.IsAlive) Assert.True(worker.Join(2000)); }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void SeededReplacementPublishesTheFinalValue(string family)
    {
        var settings = new PadSetting { ButtonA = "Button 1", ButtonB = "Button 2" };
        string key = Family(family)[2];
        var set = Setter(settings, family);
        var get = Getter(settings, family);
        set(key, Value(family, 1));
        set("removed", Value(family, 1));
        Assert.Equal(Value(family, 1), get(key));
        settings.SetRawMapping("Stick0SteerMode", "Wheel");
        var seed = new Dictionary<string, string> { [key] = Value(family, 3) };
        settings.ClearMappingDescriptors(
            family.StartsWith("Mapping", StringComparison.Ordinal) ? null : seed,
            family == "MappingDeadZone" ? seed : null,
            family == "MappingBidirectional" ? seed : null);
        Assert.Equal(Value(family, 3), get(key));
        Assert.Equal("", get("removed"));
        Assert.Equal("Wheel", settings.GetRawMapping("Stick0SteerMode"));
        Assert.Equal("", settings.ButtonB);
    }

    [Fact]
    public void CompanionSeedsKeepTheSettersDefaultOmissionRules()
    {
        var settings = new PadSetting();
        settings.ClearMappingDescriptors(null,
            new Dictionary<string, string> { ["ButtonA"] = "50", ["ButtonB"] = "0", ["ButtonX"] = "" },
            new Dictionary<string, string> { ["ButtonA"] = "0", ["ButtonB"] = "" });
        Assert.Equal("", settings.GetMappingDeadZone("ButtonA"));
        Assert.Equal("", settings.GetMappingDeadZone("ButtonB"));
        Assert.Equal("", settings.GetMappingBidirectional("ButtonA"));
        Assert.Equal(new PadSetting().ComputeChecksum(), settings.ComputeChecksum());
        settings.ClearMappingDescriptors(null,
            new Dictionary<string, string> { ["ButtonA"] = "65" },
            new Dictionary<string, string> { ["ButtonA"] = "1" });
        Assert.Equal("65", settings.GetMappingDeadZone("ButtonA"));
        Assert.Equal("1", settings.GetMappingBidirectional("ButtonA"));
        Assert.NotEqual(new PadSetting().ComputeChecksum(), settings.ComputeChecksum());
    }
}

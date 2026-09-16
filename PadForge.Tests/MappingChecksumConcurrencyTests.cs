using System.Reflection;
using PadForge.Engine.Data;
using Xunit.Abstractions;

namespace PadForge.Tests;

public sealed class MappingChecksumConcurrencyTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("RawMapping", "_rawDictLock")]
    [InlineData("MidiMapping", "_midiDictLock")]
    [InlineData("KbmMapping", "_kbmDictLock")]
    [InlineData("VrMapping", "_vrDictLock")]
    [InlineData("MappingDeadZone", "_mappingDeadZoneDictLock")]
    [InlineData("MappingBidirectional", "_mappingBidirectionalDictLock")]
    public void ChecksumWaitsForAnInProgressDictionaryEdit(string family, string gateName)
    {
        var settings = new PadSetting();
        var set = typeof(PadSetting).GetMethod("Set" + family)!.CreateDelegate<Action<string, string>>(settings);
        set("entry", "1");
        string before = settings.ComputeChecksum();
        object gate = typeof(PadSetting).GetField(gateName, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(settings)!;
        using var started = new ManualResetEventSlim();
        using var done = new ManualResetEventSlim();
        string concurrent = null;
        Exception fault = null;
        var reader = new Thread(() =>
        {
            started.Set();
            try { concurrent = settings.ComputeChecksum(); }
            catch (Exception error) { fault = error; }
            finally { done.Set(); }
        }) { IsBackground = true };
        bool completedDuringEdit = false;
        try
        {
            lock (gate)
            {
                reader.Start();
                Assert.True(started.Wait(2000));
                completedDuringEdit = done.Wait(150);
                set("entry", "2");
            }
            Assert.True(reader.Join(2000));
            Assert.Null(fault);
            string after = settings.ComputeChecksum();
            output.WriteLine($"{family}: completedDuringEdit={completedDuringEdit} before={before} concurrent={concurrent} after={after}");
            Assert.NotEqual(before, after);
            Assert.False(completedDuringEdit);
            Assert.Equal(after, concurrent);
        }
        finally
        {
            if (reader.IsAlive) Assert.True(reader.Join(2000));
        }
    }

    [Theory]
    [InlineData("RawMapping")]
    [InlineData("MidiMapping")]
    [InlineData("KbmMapping")]
    [InlineData("VrMapping")]
    public void DictionaryInsertionOrderDoesNotChangeTheChecksum(string family)
    {
        var first = new PadSetting();
        var second = new PadSetting();
        var method = typeof(PadSetting).GetMethod("Set" + family)!;
        var a = method.CreateDelegate<Action<string, string>>(first);
        var b = method.CreateDelegate<Action<string, string>>(second);
        a("a", "Button 1"); a("z", "Button 2");
        b("z", "Button 2"); b("a", "Button 1");
        Assert.Equal(first.ComputeChecksum(), second.ComputeChecksum());
    }
}

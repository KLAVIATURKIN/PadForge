using System.Collections;
using System.Reflection;
using PadForge.Common.Input;
using Xunit.Abstractions;

namespace PadForge.Tests;

[Collection("AudioProfileLifecycle")]
public sealed class PairRetryEnumerationTests(ITestOutputHelper output)
{
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

    [Theory]
    [InlineData("empty", 0)]
    [InlineData("complete", 0)]
    [InlineData("remote", 0)]
    [InlineData("direct", 0)]
    [InlineData("other-family", 0)]
    [InlineData("missing-primary", 0)]
    [InlineData("suppressed", 0)]
    [InlineData("one-missing-child", 2)]
    [InlineData("three-missing-children", 2)]
    public void EnumerateChildrenOnlyWhenTheRetryHasEligiblePairs(string scenario, int expected)
    {
        var service = typeof(HapticToneService);
        var gate = service.GetField("_lock", Static)!.GetValue(null)!;
        var sinks = (IList)service.GetField("_sinks", Static)!.GetValue(null)!;
        var suppressed = service.GetField("_suppressed", Static)!;
        bool oldSuppressed = (bool)suppressed.GetValue(null)!;
        var finder = HapticToneService.PairRetryPathProvider;
        var type = service.GetNestedType("Sink", BindingFlags.NonPublic)!;
        var family = service.GetNestedType("Family", BindingFlags.NonPublic)!;
        var oldSinks = sinks.Cast<object>().ToArray();
        Assert.Empty(oldSinks);
        var created = new List<object>();
        var calls = new List<ushort>();
        Assert.Null(service.GetField("_reconcileTimer", Static)!.GetValue(null));
        Assert.Equal(0, (int)service.GetField("_reconcileBusy", Static)!.GetValue(null)!);
        try
        {
            HapticToneService.PairRetryPathProvider = (vid, pid) =>
            {
                Assert.Equal((ushort)0x057e, vid);
                calls.Add(pid);
                return new List<string>();
            };
            lock (gate)
            {
                sinks.Clear();
                suppressed.SetValue(null, scenario == "suppressed");
                int count = scenario == "empty" ? 0 : scenario == "three-missing-children" ? 3 : 1;
                for (int i = 0; i < count; i++)
                {
                    var sink = Activator.CreateInstance(type, true)!;
                    type.GetField("Family")!.SetValue(sink, Enum.Parse(family, scenario == "other-family" ? "Pro" : "JoyConPair"));
                    type.GetField("Handle")!.SetValue(sink, scenario == "missing-primary" ? IntPtr.Zero : new IntPtr(1));
                    type.GetField("PairSecondHandle")!.SetValue(sink, scenario == "complete" ? new IntPtr(2) : IntPtr.Zero);
                    type.GetField("Remote")!.SetValue(sink, scenario == "remote");
                    type.GetField("HidPath")!.SetValue(sink, scenario == "direct" ? @"\\?\hid#controlled" : "nintendo_joycons_combined");
                    sinks.Add(sink);
                    created.Add(sink);
                }
            }
            service.GetMethod("RetryPairSecondHandles", Static)!.Invoke(null, null);
            output.WriteLine($"scenario={scenario} enumerationCalls={calls.Count}");
            Assert.Equal(expected, calls.Count);
            if (expected > 0) Assert.Equal(new ushort[] { 0x2006, 0x2007 }, calls);
        }
        finally
        {
            lock (gate)
            {
                foreach (var sink in created)
                {
                    type.GetField("Handle")!.SetValue(sink, IntPtr.Zero);
                    type.GetField("PairSecondHandle")!.SetValue(sink, IntPtr.Zero);
                }
                sinks.Clear();
                foreach (var sink in oldSinks) sinks.Add(sink);
                suppressed.SetValue(null, oldSuppressed);
            }
            HapticToneService.PairRetryPathProvider = finder;
        }
    }
}

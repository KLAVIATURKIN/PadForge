using PadForge.Engine;
using static SDL3.SDL;
using Xunit.Abstractions;

namespace PadForge.Tests;

public sealed class HapticEffectStartTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Starts()
    {
        foreach (string mode in new[] { "scalar", "directional", "condition", "center" })
            foreach (string transition in new[] { "initial", "replace", "restore" })
                yield return [mode, transition];
    }

    [Theory]
    [MemberData(nameof(Starts))]
    public void FailedStartReleasesItsAllocationAndRetriesPlayback(string mode, string transition)
    {
        using var h = new HapticEffectFixture();
        if (mode == "center")
        {
            h.Set("JoystickType", SDL_JoystickType.SDL_JOYSTICK_TYPE_WHEEL);
            h.Settings.AutoCenterStrength = "25";
        }
        var value = mode switch
        {
            "condition" => h.Condition(),
            "directional" => h.Directional(),
            "scalar" => new Vibration(1000, 0),
            _ => new Vibration()
        };
        void Change(bool changed)
        {
            switch (mode)
            {
                case "condition": value.ConditionAxes[0].Offset = changed ? (short)5000 : (short)0; break;
                case "directional": value.SignedMagnitude = changed ? (short)6000 : (short)4000; break;
                case "scalar": value.LeftMotorSpeed = changed ? (ushort)2000 : (ushort)1000; break;
                case "center": h.Settings.AutoCenterStrength = changed ? "50" : "25"; break;
            }
        }

        // A successful start in the same fixture proves the playback path is active.
        h.Apply(value);
        Assert.True(h.Running);
        Assert.Single(h.Allocated);
        if (transition == "initial") h.State.StopDeviceForces(h.Device);
        else { Change(true); h.FailUpdate = true; }

        h.FailRun = true;
        h.Apply(value);
        output.WriteLine($"{mode}/{transition}: running={h.Running} allocated={h.Allocated.Count} calls={string.Join(',', h.Calls)}");
        Assert.False(h.Running);
        int failedAllocations = h.Allocated.Count;

        h.FailRun = h.FailUpdate = false;
        if (transition == "restore") Change(false);
        int runs = h.Calls.Count(x => x == "run");
        h.Apply(value);
        output.WriteLine($"after retry: running={h.Running} allocated={h.Allocated.Count} calls={string.Join(',', h.Calls)}");
        Assert.True(h.Running);
        Assert.Equal(0, failedAllocations);
        Assert.Single(h.Allocated);
        Assert.Equal(runs + 1, h.Calls.Count(x => x == "run"));
        int calls = h.Calls.Count;
        h.Apply(value);
        Assert.Equal(calls, h.Calls.Count);
    }
}

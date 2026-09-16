using System.IO;
using System.Reflection;
using PadForge.Engine;
using Xunit.Abstractions;

namespace PadForge.Tests;

[Collection("SettingsManagerStatics")]
public sealed class ButtonSurfaceTraceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(7)]
    [InlineData(120)]
    [InlineData(300)]
    public void RawOnlyChangesHaveIndependentEdgesBesideAMappedControl(int rawIndex)
    {
        string path = Path.Combine(Path.GetTempPath(), $"button-surface-{Guid.NewGuid():N}.log");
        using var wrapper = new SdlDeviceWrapper();
        void Set(string name, object value) => typeof(SdlDeviceWrapper).GetProperty(name)!.SetValue(wrapper, value);
        var trace = typeof(SdlDeviceWrapper).GetMethod("TraceButtonSurface", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var raw = new bool[rawIndex + 1];
        int reads = 0;
        bool failRead = false;
        wrapper.DiagnosticRawButtonReader = (_, index) =>
        {
            reads++;
            if (failRead) throw new IOException("Read unavailable.");
            return raw[index];
        };
        var state = new CustomInputState();
        try
        {
            // Every native read is replaced, and the handle is cleared before disposal.
            Set("Joystick", new IntPtr(1));
            Set("RawButtonCount", raw.Length);
            SdlDiagLog.SetMirror(path);
            trace.Invoke(wrapper, [state]);
            state.Buttons[0] = true;
            trace.Invoke(wrapper, [state]);
            string control = File.ReadAllText(path);
            Assert.Contains("BTNEDGE pos=0 gp=1", control);

            raw[rawIndex] = true;
            trace.Invoke(wrapper, [state]);
            trace.Invoke(wrapper, [state]);
            failRead = true;
            trace.Invoke(wrapper, [state]);
            Assert.DoesNotContain($"RAWBTNEDGE index={rawIndex} down=0", File.ReadAllText(path));
            failRead = false;
            raw[rawIndex] = false;
            state.Buttons[0] = false;
            trace.Invoke(wrapper, [state]);
            string log = File.ReadAllText(path);
            output.WriteLine(log);
            Assert.Contains("BTNEDGE pos=0 gp=0", log);
            Assert.Single(log.Split('\n'), x => x.Contains($"RAWBTNEDGE index={rawIndex} down=1"));
            Assert.Single(log.Split('\n'), x => x.Contains($"RAWBTNEDGE index={rawIndex} down=0"));
            Assert.DoesNotContain(" raw=", log);

            int enabledReads = reads;
            SdlDiagLog.SetMirror(null);
            raw[rawIndex] = true;
            trace.Invoke(wrapper, [state]);
            Assert.Equal(enabledReads, reads);
        }
        finally
        {
            Set("Joystick", IntPtr.Zero);
            SdlDiagLog.SetMirror(null);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

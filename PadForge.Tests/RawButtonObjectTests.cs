using Xunit.Abstractions;

namespace PadForge.Tests;

public sealed class RawButtonObjectTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("buttons-mapped")]
    [InlineData("buttons-unmapped")]
    [InlineData("buttons-all-mapped")]
    [InlineData("buttons-joystick")]
    public async Task DeviceObjectsAgreeWithTheReadableButtonSurface(string scenario)
    {
        var (code, log) = await NativeCheckRunner.Run(scenario);
        output.WriteLine(log);
        Assert.Equal(0, code);
    }
}

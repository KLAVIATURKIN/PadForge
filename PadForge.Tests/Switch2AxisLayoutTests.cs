using System.Reflection;
using PadForge.Engine;
using PadForge.Engine.Common;
using Xunit.Abstractions;

namespace PadForge.Tests;

public sealed class Switch2AxisLayoutTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0x2066, 6, false, false, 6)]
    [InlineData(0x2066, 8, true, false, 8)]
    [InlineData(0x2066, 9, false, true, 6)]
    [InlineData(0x2066, 11, true, true, 8)]
    [InlineData(0x2067, 6, false, false, 6)]
    [InlineData(0x2067, 8, true, false, 8)]
    [InlineData(0x2067, 9, false, true, 6)]
    [InlineData(0x2067, 11, true, true, 8)]
    public void SensorCapabilitiesMatchThePublishedAxisLayout(int product, int axes, bool mouse, bool magnetometer, int magBase)
    {
        using var wrapper = new SdlDeviceWrapper();
        void Set(string name, object value) => typeof(SdlDeviceWrapper).GetProperty(name)!.SetValue(wrapper, value);
        try
        {
            Set("VendorId", (ushort)0x057e);
            Set("ProductId", (ushort)product);
            Set("Joystick", new IntPtr(1));
            Set("GameController", new IntPtr(1));
            Set("RawAxisCount", axes);
            // Only managed capability resolvers receive these synthetic handles.
            wrapper.UpdateSwitch2AxisCapabilities();
            wrapper.UpdateExtraAxisCapabilities();
            output.WriteLine($"pid={product:X4} axes={axes} mouse={wrapper.HasJoyCon2Mouse} mag={wrapper.HasSwitch2Magnetometer} magBase={wrapper.Switch2MagnetometerAxisBase} generic={wrapper.HasExtraGenericAxes}");
            Assert.Equal(mouse, wrapper.HasJoyCon2Mouse);
            Assert.Equal(magnetometer, wrapper.HasSwitch2Magnetometer);
            Assert.Equal(magBase, wrapper.Switch2MagnetometerAxisBase);
            Assert.False(wrapper.HasExtraGenericAxes);
        }
        finally { Set("Joystick", IntPtr.Zero); Set("GameController", IntPtr.Zero); }
    }

    [Theory]
    [InlineData(0x045e, 0x2066, true)]
    [InlineData(0x057e, 0x2069, true)]
    [InlineData(0x057e, 0x2007, true)]
    [InlineData(0x057e, 0x2066, false)]
    public void SensorCapabilitiesRequireTheSupportedLiveJoystick(int vendor, int product, bool joystick)
    {
        using var wrapper = new SdlDeviceWrapper();
        void Set(string name, object value) => typeof(SdlDeviceWrapper).GetProperty(name)!.SetValue(wrapper, value);
        try
        {
            Set("VendorId", (ushort)vendor);
            Set("ProductId", (ushort)product);
            Set("Joystick", joystick ? new IntPtr(1) : IntPtr.Zero);
            Set("RawAxisCount", 11);
            wrapper.UpdateSwitch2AxisCapabilities();
            Assert.False(wrapper.HasJoyCon2Mouse);
            Assert.False(wrapper.HasSwitch2Magnetometer);
        }
        finally { Set("Joystick", IntPtr.Zero); }
    }

    [Fact]
    public void OrdinaryExtraAxesRemainAvailable()
    {
        using var wrapper = new SdlDeviceWrapper();
        void Set(string name, object value) => typeof(SdlDeviceWrapper).GetProperty(name)!.SetValue(wrapper, value);
        try
        {
            Set("VendorId", (ushort)0x054c);
            Set("ProductId", (ushort)0x0268);
            Set("Joystick", new IntPtr(1));
            Set("GameController", new IntPtr(1));
            Set("RawAxisCount", 16);
            wrapper.UpdateSwitch2AxisCapabilities();
            wrapper.UpdateExtraAxisCapabilities();
            Assert.True(wrapper.HasExtraGenericAxes);
        }
        finally { Set("Joystick", IntPtr.Zero); Set("GameController", IntPtr.Zero); }
    }
}

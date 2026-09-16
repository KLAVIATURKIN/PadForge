using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit.Abstractions;

namespace PadForge.Tests;

[Collection("AudioProfileLifecycle")]
public sealed class LightingCompositionTests(ITestOutputHelper output)
{
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    const BindingFlags Static=BindingFlags.Static|BindingFlags.NonPublic;
    [Theory]
    [InlineData(false,false,false,1)] [InlineData(false,false,true,1)]
    [InlineData(false,true,false,1)] [InlineData(false,true,true,1)]
    [InlineData(true,false,false,1)] [InlineData(true,false,true,1)]
    [InlineData(true,true,false,1)] [InlineData(true,true,true,1)]
    [InlineData(true,true,false,5)] [InlineData(false,true,false,5)]
    public void EveryLightingSinkComposesOverlayAndMacro(bool move,bool playerBase,bool macro,int playerNumber)
    {
        var cultureCallbacks=typeof(PadForge.Resources.Strings.Strings).GetFields(Static|BindingFlags.Public)
            .Where(f=>!f.IsInitOnly&&typeof(Delegate).IsAssignableFrom(f.FieldType)).Select(f=>(Field:f,Value:f.GetValue(null))).ToArray();
        var oldDevices=SettingsManager.UserDevices;var oldSettings=SettingsManager.UserSettings;
        var oldCreated=SettingsManager.SlotCreated;var oldEnabled=SettingsManager.SlotEnabled;var oldOrder=SettingsManager.XboxSlotOrder;
        var oldConfigs=UserEffectsDispatcher.SlotPerDeviceConfigsProvider;
        var current=typeof(PsMoveDirectService).GetField("_current",Static)!;var oldMove=current.GetValue(null);
        PsMoveDirectService service=null;SdlDeviceWrapper wrapper=null;UserEffectsDispatcher dispatcher=null;
        (byte r,byte g,byte b) actual=default;
        try
        {
            SettingsManager.UserDevices=new();SettingsManager.UserSettings=new();
            SettingsManager.SlotCreated=new bool[16];SettingsManager.SlotEnabled=new bool[16];
            SettingsManager.XboxSlotOrder=Enumerable.Range(0,playerNumber-1).Append(15).ToList();
            foreach(int slot in SettingsManager.XboxSlotOrder)SettingsManager.SlotCreated[slot]=SettingsManager.SlotEnabled[slot]=true;
            var id=Guid.NewGuid();var device=new UserDevice{InstanceGuid=id,IsOnline=true,IsEnabled=true};
            if(move)
            {
                service=new PsMoveDirectService();typeof(PsMoveDirectService).GetField("_sdlJoystick",Private)!.SetValue(service,new IntPtr(1));
                typeof(PsMoveDirectService).GetField("_instanceId",Private)!.SetValue(service,(uint)123);current.SetValue(null,service);
                wrapper=new SdlDeviceWrapper{SdlInstanceId=123};device.Device=wrapper;device.VendorId=0x054c;device.ProdId=0x03d5;
            }
            else
            {
                var web=new WebControllerDevice(id.ToString(),"Lighting fixture",layoutKey:"ds4");
                web.LedChanged+=(r,g,b)=>actual=(r,g,b);device.Device=web;
            }
            SettingsManager.UserDevices.Items.Add(device);SettingsManager.UserSettings.Items.Add(new UserSetting{InstanceGuid=id,MapTo=15});
            var cfg=new DeviceSlotConfig{LightbarMode=LightbarMode.Static,LightbarRed=16,LightbarGreen=20,LightbarBlue=24,LightbarInputHoldMs=10000};
            UserEffectsDispatcher.SlotPerDeviceConfigsProvider=_=>new Dictionary<Guid,DeviceSlotConfig>{[id]=cfg};
            dispatcher=new UserEffectsDispatcher(15,cfg,startTimer:false);
            (byte,byte,byte) Read()=>move?((byte)typeof(PsMoveDirectService).GetField("_r",Private)!.GetValue(service),(byte)typeof(PsMoveDirectService).GetField("_g",Private)!.GetValue(service),(byte)typeof(PsMoveDirectService).GetField("_b",Private)!.GetValue(service)):actual;
            dispatcher.ApplyOnce();Assert.Equal(((byte)16,(byte)20,(byte)24),Read());
            cfg.LightbarMode=playerBase?LightbarMode.PlayerNumber:LightbarMode.Static;
            if(macro){cfg.MacroOverrideR=100;cfg.MacroOverrideG=110;cfg.MacroOverrideB=120;cfg.MacroOverrideHoldMode=MacroLightbarHoldMode.Sticky;cfg.MacroOverrideExpiresAtUtc=DateTime.UtcNow.AddMinutes(1);}
            else{cfg.InputReactiveR=100;cfg.InputReactiveG=110;cfg.InputReactiveB=120;cfg.InputReactiveMode=InputReactiveMode.Fixed;typeof(UserEffectsDispatcher).GetField("_pulseStartMs",Private)!.SetValue(dispatcher,Environment.TickCount64);}
            typeof(UserEffectsDispatcher).GetMethod("StopAnimTimer",Private)!.Invoke(dispatcher,null);
            dispatcher.ApplyOnce();var composed=Read();
            PadForge.Services.InputService guide=null;
            if(!move)
            {
                var vm=new MainViewModel();var pad=vm.Pads[15];
                pad.MappedDevices.Add(new PadViewModel.MappedDeviceInfo{InstanceGuid=id,Name="Lighting fixture",IsOnline=true});
                var fallback=pad.GetOrCreateDeviceConfig(id);
                fallback.LightbarMode=LightbarMode.Static;fallback.LightbarRed=16;fallback.LightbarGreen=20;fallback.LightbarBlue=24;
                guide=(PadForge.Services.InputService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PadForge.Services.InputService));
                typeof(PadForge.Services.InputService).GetField("_mainVm",Private)!.SetValue(guide,vm);
                guide.ApplyGuideLeds();
                Assert.Equal(composed,Read());
            }
            if(playerBase&&!macro)
            {
                cfg.LightbarInputHoldMs=0;cfg.LightbarInputDecayMs=5000;
                typeof(UserEffectsDispatcher).GetField("_pulseStartMs",Private)!.SetValue(dispatcher,Environment.TickCount64-2500);
                typeof(UserEffectsDispatcher).GetMethod("StopAnimTimer",Private)!.Invoke(dispatcher,null);
                dispatcher.ApplyOnce();var half=Read();
                var floor=move?PsMoveDirectService.DefaultSphereColor(playerNumber):PlayerIdentityDefaults.WebColorFor(playerNumber);
                Assert.InRange((int)half.Item1,(floor.R+100)/2-3,(floor.R+100)/2+3);
                Assert.InRange((int)half.Item2,(floor.G+110)/2-3,(floor.G+110)/2+3);
                Assert.InRange((int)half.Item3,(floor.B+120)/2-3,(floor.B+120)/2+3);
            }
            cfg.MacroOverrideExpiresAtUtc=DateTime.MinValue;cfg.InputReactiveMode=InputReactiveMode.Off;
            typeof(UserEffectsDispatcher).GetMethod("StopAnimTimer",Private)!.Invoke(dispatcher,null);
            dispatcher.ApplyOnce();var released=Read();
            bool claim=move&&(bool)typeof(PsMoveDirectService).GetField("_ledExplicit",Private)!.GetValue(service);
            output.WriteLine($"move={move} player={playerBase} macro={macro} composed={composed} released={released} claim={claim}");
            Assert.Equal(((byte)100,(byte)110,(byte)120),composed);
            if(playerBase)
            {
                if(move){Assert.False(claim);Assert.Equal(PsMoveDirectService.DefaultSphereColor(playerNumber),released);}
                else Assert.Equal(PlayerIdentityDefaults.WebColorFor(playerNumber),released);
            }
            else Assert.Equal(((byte)16,(byte)20,(byte)24),released);
            if(guide!=null)
            {
                dispatcher.Dispose();guide.ApplyGuideLeds();
                Assert.Equal(((byte)16,(byte)20,(byte)24),Read());
            }
        }
        finally
        {
            dispatcher?.Dispose();
            if(service!=null){typeof(PsMoveDirectService).GetField("_sdlJoystick",Private)!.SetValue(service,IntPtr.Zero);((AutoResetEvent)typeof(PsMoveDirectService).GetField("_writeSignal",Private)!.GetValue(service)).Dispose();}
            wrapper?.Dispose();current.SetValue(null,oldMove);UserEffectsDispatcher.SlotPerDeviceConfigsProvider=oldConfigs;
            foreach(var callback in cultureCallbacks)callback.Field.SetValue(null,callback.Value);
            SettingsManager.UserDevices=oldDevices;SettingsManager.UserSettings=oldSettings;SettingsManager.SlotCreated=oldCreated;SettingsManager.SlotEnabled=oldEnabled;SettingsManager.XboxSlotOrder=oldOrder;
        }
    }
}

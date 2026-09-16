using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.RemoteLink;
using PadForge.ViewModels;
using Xunit.Abstractions;

namespace PadForge.Tests;

[Collection("AudioProfileLifecycle")]
public sealed class EffectDeliveryTests(ITestOutputHelper output)
{
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    const BindingFlags Static=BindingFlags.Static|BindingFlags.NonPublic;
    static void StopAudioWorker()
    {
        AudioPassthroughService.Shutdown();
        foreach(string name in new[]{"_workerThread","_btThread"})
            if(typeof(AudioPassthroughService).GetField(name,Static)!.GetValue(null) is Thread t&&t.IsAlive)Assert.True(t.Join(5000));
    }
    [Fact] public void RejectedRelayWriteReportsNoDelivery()
    {
        string path="peer://delivery-"+Guid.NewGuid();var sender=RemoteLinkOutputRouter.SendOutput;
        try
        {
            RemoteLinkOutputRouter.SendOutput=null;
            var profile=HMaestroProfileCatalog.GetProfileById("dualsense");Assert.NotNull(profile);
            var fields=Ds5EffectSynthesizer.BuildFields(new DeviceSlotConfig());
            bool failed=PlayStationEffectWriter.Write(path,profile,fields);
            int writes=0;RemoteLinkOutputRouter.Register(path,"delivery",0);RemoteLinkOutputRouter.SendOutput=(_,_,_)=>writes++;
            bool sent=PlayStationEffectWriter.Write(path,profile,fields);
            output.WriteLine($"rejected result={failed} accepted result={sent} sent frames={writes}");
            Assert.True(sent);Assert.Equal(1,writes);Assert.False(failed);
        }
        finally{RemoteLinkOutputRouter.Unregister(path);RemoteLinkOutputRouter.SendOutput=sender;}
    }

    [Theory] [InlineData("local")] [InlineData("relay")] [InlineData("claimed")]
    public void RejectedEffectRetainsItsTransitionUntilSuccessfulRetry(string mode)
    {
        StopAudioWorker();
        string dir=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"PadForge-EffectDelivery-"+Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);string file=System.IO.Path.Combine(dir,"BTHENUM-report.bin");
        string path=mode=="relay"?"peer://delivery-"+Guid.NewGuid():file;
        var oldDevices=SettingsManager.UserDevices;var oldSettings=SettingsManager.UserSettings;
        var sender=RemoteLinkOutputRouter.SendOutput;var cfgs=UserEffectsDispatcher.SlotPerDeviceConfigsProvider;
        var id=Guid.NewGuid();UserEffectsDispatcher dispatcher=null;
        var clears=(HashSet<Guid>)typeof(AudioPassthroughService).GetField("_speakerPathCleared",Static)!.GetValue(null);
        var gate=typeof(AudioPassthroughService).GetField("_lock",Static)!.GetValue(null);
        try
        {
            SettingsManager.UserDevices=new();SettingsManager.UserSettings=new();UserEffectsDispatcher.SlotPerDeviceConfigsProvider=null;
            var device=new UserDevice{InstanceGuid=id,VendorId=0x054c,ProdId=0x0ce6,IsOnline=true,IsEnabled=true,DevicePath=path};
            SettingsManager.UserDevices.Items.Add(device);SettingsManager.UserSettings.Items.Add(new UserSetting{InstanceGuid=id,MapTo=15});
            var cfg=new DeviceSlotConfig{LightbarMode=LightbarMode.Off,AudioOutputPath=AudioOutputPath.Automatic};
            dispatcher=new UserEffectsDispatcher(15,cfg,startTimer:false);
            var bt=(System.Collections.Concurrent.ConcurrentDictionary<Guid,byte>)typeof(UserEffectsDispatcher).GetField("_btBarReleasePending",Private)!.GetValue(dispatcher);
            lock(gate)clears.Add(id);
            if(mode=="claimed"){System.IO.File.WriteAllBytes(file,[]);Assert.True(RemoteLinkOutputRouter.ClaimOutput(path));}
            if(mode=="relay")RemoteLinkOutputRouter.SendOutput=null;
            dispatcher.ApplyOnce();
            bool firstSpeaker=AudioPassthroughService.PeekSpeakerPathCleared(id),firstBt=bt.ContainsKey(id);
            bool retryArmed=(bool)typeof(UserEffectsDispatcher).GetField("_animTickActive",Private)!.GetValue(dispatcher);
            (typeof(UserEffectsDispatcher).GetField("_animTimer",Private)!.GetValue(dispatcher) as Timer)?.Change(Timeout.Infinite,Timeout.Infinite);
            int delivered=0;var reports=new List<byte[]>();
            if(mode=="relay"){RemoteLinkOutputRouter.Register(path,"delivery",0);RemoteLinkOutputRouter.SendOutput=(_,_,bytes)=>{delivered++;Assert.True(OutputEffectCodec.TryDecode(bytes,out var e));reports.Add(e.SonyBody);};}
            else{RemoteLinkOutputRouter.ReleaseDevice(path);System.IO.File.WriteAllBytes(file,[]);}
            typeof(UserEffectsDispatcher).GetMethod("OnAnimTickCore",Private)!.Invoke(dispatcher,[null]);
            int retryDelivered=mode=="relay"?delivered:System.IO.File.ReadAllBytes(file).Length;
            dispatcher.ApplyOnce();
            byte[] local=mode!="relay"?System.IO.File.ReadAllBytes(file):[];
            if(mode!="relay")delivered=local.Length;
            output.WriteLine($"mode={mode} pending speaker={firstSpeaker} pending BT={firstBt} retry armed={retryArmed} retry bytes/frames={retryDelivered} control bytes/frames={delivered}");
            Assert.True(delivered>0);Assert.False(AudioPassthroughService.PeekSpeakerPathCleared(id));Assert.DoesNotContain(id,bt.Keys);
            Assert.True(firstSpeaker);if(mode!="relay")Assert.True(firstBt);
            Assert.True(retryArmed);Assert.True(retryDelivered>0);
            if(mode=="relay")Assert.NotEqual(0,reports[0][0]&3);
        }
        finally
        {
            dispatcher?.Dispose();lock(gate)clears.Remove(id);RemoteLinkOutputRouter.Unregister(path);RemoteLinkOutputRouter.ReleaseDevice(path);
            RemoteLinkOutputRouter.SendOutput=sender;UserEffectsDispatcher.SlotPerDeviceConfigsProvider=cfgs;
            SettingsManager.UserDevices=oldDevices;SettingsManager.UserSettings=oldSettings;
            if(System.IO.File.Exists(file))System.IO.File.Delete(file);System.IO.Directory.Delete(dir);
        }
    }

    [Fact] public void FrameBuiltBeforeFailureCannotAcknowledgeRecovery()
    {
        StopAudioWorker();
        var devices=SettingsManager.UserDevices;var settings=SettingsManager.UserSettings;
        var configs=UserEffectsDispatcher.SlotPerDeviceConfigsProvider;var sender=RemoteLinkOutputRouter.SendScopedOutput;
        string path="peer://prebuilt-"+Guid.NewGuid();var id=Guid.NewGuid();UserEffectsDispatcher dispatcher=null;
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        var successful=new List<byte[]>();int attempts=0;Exception errorA=null,errorB=null;Thread a=null,b=null;
        try
        {
            SettingsManager.UserDevices=new();SettingsManager.UserSettings=new();UserEffectsDispatcher.SlotPerDeviceConfigsProvider=null;
            SettingsManager.UserDevices.Items.Add(new UserDevice{InstanceGuid=id,VendorId=0x054c,ProdId=0x0ce6,IsOnline=true,DevicePath=path});
            SettingsManager.UserSettings.Items.Add(new UserSetting{InstanceGuid=id,MapTo=15});
            dispatcher=new UserEffectsDispatcher(15,new DeviceSlotConfig{LightbarMode=LightbarMode.Off},startTimer:false);
            var connection=new LinkConnectionLifetime("delivery",new LinkExposureSnapshot([]),()=>true,(_,_,_,_)=>true);
            RemoteLinkOutputRouter.Register(path,"delivery",0,connection,null);
            RemoteLinkOutputRouter.SendScopedOutput=(_,_,_,bytes)=>
            {
                if(Interlocked.Increment(ref attempts)==1){entered.Set();if(!release.Wait(5000))throw new TimeoutException();return false;}
                Assert.True(OutputEffectCodec.TryDecode(bytes,out var effect));successful.Add(effect.SonyBody);return true;
            };
            a=new Thread(()=>{try{dispatcher.ApplyOnce();}catch(Exception e){errorA=e;}});
            b=new Thread(()=>{try{dispatcher.ApplyOnce();}catch(Exception e){errorB=e;}});
            a.Start();Assert.True(entered.Wait(5000));
            var sequence=typeof(UserEffectsDispatcher).GetField("s_dispatchSeq",Static)!;long first=(long)sequence.GetValue(null);
            b.Start();Assert.True(SpinWait.SpinUntil(()=>(long)sequence.GetValue(null)>first,5000));
            release.Set();Assert.True(a.Join(5000));Assert.True(b.Join(5000));
            (typeof(UserEffectsDispatcher).GetField("_animTimer",Private)!.GetValue(dispatcher) as Timer)?.Change(Timeout.Infinite,Timeout.Infinite);
            Assert.Null(errorA);Assert.Null(errorB);
            typeof(UserEffectsDispatcher).GetMethod("OnAnimTickCore",Private)!.Invoke(dispatcher,[null]);
            Assert.NotEmpty(successful);Assert.NotEqual(0,successful[0][0]&3);
        }
        finally
        {
            release.Set();if(a?.IsAlive==true)Assert.True(a.Join(5000));if(b?.IsAlive==true)Assert.True(b.Join(5000));
            dispatcher?.Dispose();RemoteLinkOutputRouter.Unregister(path);RemoteLinkOutputRouter.SendScopedOutput=sender;
            UserEffectsDispatcher.SlotPerDeviceConfigsProvider=configs;SettingsManager.UserDevices=devices;SettingsManager.UserSettings=settings;
        }
    }

    [Theory] [InlineData("unassign")] [InlineData("offline")] [InlineData("owner")] [InlineData("dispose")]
    public void RetiringTheOutputTargetStopsItsRetry(string transition)
    {
        StopAudioWorker();
        var devices=SettingsManager.UserDevices;var settings=SettingsManager.UserSettings;
        var configs=UserEffectsDispatcher.SlotPerDeviceConfigsProvider;
        UserEffectsDispatcher dispatcher=null,owner=null;var id=Guid.NewGuid();
        try
        {
            SettingsManager.UserDevices=new();SettingsManager.UserSettings=new();UserEffectsDispatcher.SlotPerDeviceConfigsProvider=null;
            var device=new UserDevice{InstanceGuid=id,VendorId=0x054c,ProdId=0x0ce6,IsOnline=true,DevicePath="peer://unavailable-"+id};
            SettingsManager.UserDevices.Items.Add(device);SettingsManager.UserSettings.Items.Add(new UserSetting{InstanceGuid=id,MapTo=15});
            dispatcher=new UserEffectsDispatcher(15,new DeviceSlotConfig{LightbarMode=LightbarMode.Off},startTimer:false);
            dispatcher.ApplyOnce();
            var active=typeof(UserEffectsDispatcher).GetField("_animTickActive",Private)!;
            Assert.True((bool)active.GetValue(dispatcher));
            (typeof(UserEffectsDispatcher).GetField("_animTimer",Private)!.GetValue(dispatcher) as Timer)?.Change(Timeout.Infinite,Timeout.Infinite);
            if(transition=="unassign")SettingsManager.UserSettings.Items.Clear();
            else if(transition=="offline")device.IsOnline=false;
            else if(transition=="owner")
            {
                owner=new UserEffectsDispatcher(14,new DeviceSlotConfig{LightbarMode=LightbarMode.Off},startTimer:false);
                SettingsManager.UserSettings.Items.Add(new UserSetting{InstanceGuid=id,MapTo=14});
            }
            else dispatcher.Dispose();
            var tick=typeof(UserEffectsDispatcher).GetMethod("OnAnimTickCore",Private)!;
            tick.Invoke(dispatcher,[null]);tick.Invoke(dispatcher,[null]);
            Assert.False((bool)active.GetValue(dispatcher));
            var failures=(System.Collections.Concurrent.ConcurrentDictionary<Guid,long>)typeof(UserEffectsDispatcher).GetField("_failedDeliveries",Private)!.GetValue(dispatcher);
            Assert.Empty(failures);
        }
        finally
        {
            owner?.Dispose();dispatcher?.Dispose();UserEffectsDispatcher.SlotPerDeviceConfigsProvider=configs;
            SettingsManager.UserDevices=devices;SettingsManager.UserSettings=settings;
        }
    }
}

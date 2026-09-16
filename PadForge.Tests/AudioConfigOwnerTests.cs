using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using NAudio.Wave;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Tests;

[Collection("AudioProfileLifecycle")]
public sealed class AudioConfigOwnerTests
{
    const BindingFlags Static=BindingFlags.Static|BindingFlags.NonPublic;
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    static readonly Type Audio=typeof(AudioPassthroughService);
    static object Call(string name,params object[] args)=>Audio.GetMethod(name,Static)!.Invoke(null,args);
    static object Sink(Guid id,int slot)
    {
        var type=Audio.GetNestedType("Sink",BindingFlags.NonPublic)!;var sink=Activator.CreateInstance(type,true)!;
        type.GetField("DeviceGuid")!.SetValue(sink,id);type.GetField("Slot")!.SetValue(sink,slot);return sink;
    }
    sealed class Stereo:ISampleProvider
    {
        public WaveFormat WaveFormat=>WaveFormat.CreateIeeeFloatWaveFormat(48000,2);
        public int Read(float[] buffer,int offset,int count){for(int i=0;i<count;i++)buffer[offset+i]=i%2==0?.25f:.75f;return count;}
    }
    [Fact] public void ProvidersReadOnlyTheRequestedSlotsConfig()
    {
        Exception failure=null;var t=new Thread(()=>
        {
            try
            {
                var vm=new MainViewModel();var input=(InputService)RuntimeHelpers.GetUninitializedObject(typeof(InputService));
                typeof(InputService).GetField("_mainVm",Private)!.SetValue(input,vm);
                var id=Guid.NewGuid();var old=vm.Pads[0].GetOrCreateDeviceConfig(id);old.AudioOutputPath=AudioOutputPath.SpeakerOnly;old.Ds5AudioBufferLength=32;
                var current=vm.Pads[1].GetOrCreateDeviceConfig(id);current.AudioOutputPath=AudioOutputPath.StereoHeadset;current.Ds5AudioBufferLength=128;
                Assert.Equal(4,input.ResolveDeviceAudioOutputPath(0,id));Assert.Equal(32,input.ResolveDeviceAudioBufferLength(0,id));
                Assert.Equal(1,input.ResolveDeviceAudioOutputPath(1,id));Assert.Equal(128,input.ResolveDeviceAudioBufferLength(1,id));
                Assert.Equal(0,input.ResolveDeviceAudioOutputPath(2,id));Assert.Equal(48,input.ResolveDeviceAudioBufferLength(2,id));
                // Slot 2 above pins the real contract: an in-range slot reads
                // only its own config. A number that is not a slot at all (the
                // sentinel a remote-audio sink carries, or the negative a
                // voice-only microphone lane resolves) falls back to the
                // device's own config, which is what keeps a remotely fed pad
                // on its configured output path and buffer length.
                Assert.Equal(4,input.ResolveDeviceAudioOutputPath(-1,id));Assert.Equal(32,input.ResolveDeviceAudioBufferLength(16,id));
            }
            catch(Exception e){failure=e;}
        }){IsBackground=true};
        t.SetApartmentState(ApartmentState.STA);t.Start();Assert.True(t.Join(10000));
        if(failure!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    [Fact] public void OpenUsbProviderFollowsItsSinksNewSlot()
    {
        var saved=AudioPassthroughService.DeviceAudioOutputPathProvider;var id=Guid.NewGuid();var sink=Sink(id,1);
        try
        {
            var seen=new List<(int,Guid)>();
            AudioPassthroughService.DeviceAudioOutputPathProvider=(slot,device)=>{seen.Add((slot,device));return slot==1?1:4;};
            var type=Audio.GetNestedType("UsbFrameProvider",BindingFlags.NonPublic)!;
            var provider=(IWaveProvider)Activator.CreateInstance(type,new Stereo(),WaveFormat.CreateIeeeFloatWaveFormat(48000,2),sink)!;
            var data=new byte[8];Assert.Equal(8,provider.Read(data,0,data.Length));
            Assert.Equal(.25f,BitConverter.ToSingle(data,0));Assert.Equal(.75f,BitConverter.ToSingle(data,4));
            sink.GetType().GetField("Slot")!.SetValue(sink,2);provider.Read(data,0,data.Length);
            Assert.Equal(0f,BitConverter.ToSingle(data,0));Assert.Equal(.5f,BitConverter.ToSingle(data,4));
            Assert.Equal(new[]{(1,id),(2,id)},seen.ToArray());
        }
        finally{AudioPassthroughService.DeviceAudioOutputPathProvider=saved;}
    }
    [Theory] [InlineData("speaker")] [InlineData("combined")] [InlineData("haptic")] [InlineData("mic")]
    public void EveryBluetoothPacketUsesItsOwner(string family)
    {
        var path=AudioPassthroughService.DeviceAudioOutputPathProvider;var length=AudioPassthroughService.DeviceAudioBufferLengthProvider;
        var id=Guid.NewGuid();var sink=Sink(id,1);var owners=new List<int>();
        try
        {
            AudioPassthroughService.DeviceAudioOutputPathProvider=(slot,device)=>{Assert.Equal(id,device);owners.Add(slot);return 1;};
            AudioPassthroughService.DeviceAudioBufferLengthProvider=(slot,device)=>{Assert.Equal(id,device);owners.Add(slot);return slot==1?128:32;};
            foreach(int slot in new[]{1,2})
            {
                sink.GetType().GetField("Slot")!.SetValue(sink,slot);var report=new byte[1024];
                if(family=="mic")Call("FillDs5MicToggle",report,true,slot,id,(byte)0);
                else if(family=="haptic")Call("SendDs5BtHapticOnly",sink,new short[512*2],report);
                else Call("SendDs5BtFrame",sink,new float[960],new byte[200],report,family=="combined"?new short[512*2]:null);
                Assert.Equal(slot==1?128:32,report[9]);
                Assert.Equal(slot,owners[^1]);
            }
        }
        finally{AudioPassthroughService.DeviceAudioOutputPathProvider=path;AudioPassthroughService.DeviceAudioBufferLengthProvider=length;}
    }
    [Fact] public void VoiceMicChoosesCurrentOwnershipBeforeCachedSettings()
    {
        AudioPassthroughService.Shutdown();
        foreach(string name in new[]{"_workerThread","_btThread"})
            if(Audio.GetField(name,Static)!.GetValue(null) is Thread t&&t.IsAlive)Assert.True(t.Join(5000));
        var settings=SettingsManager.UserSettings;var id=Guid.NewGuid();
        var sinks=(IDictionary)Audio.GetField("_sinks",Static)!.GetValue(null);var gate=Audio.GetField("_lock",Static)!.GetValue(null);
        try
        {
            SettingsManager.UserSettings=new();
            SettingsManager.UserSettings.Items.Add(new UserSetting{InstanceGuid=id,MapTo=4});
            SettingsManager.UserSettings.Items.Add(new UserSetting{InstanceGuid=id,MapTo=2});
            Assert.Equal(2,Call("ResolveVoiceMicSlot",id));
            lock(gate)sinks[id]=Sink(id,4);
            Assert.Equal(4,Call("ResolveVoiceMicSlot",id));
            lock(gate)sinks.Remove(id);SettingsManager.UserSettings.Items.Clear();
            Assert.Equal(-1,Call("ResolveVoiceMicSlot",id));
        }
        finally{lock(gate)sinks.Remove(id);SettingsManager.UserSettings=settings;}
    }
}

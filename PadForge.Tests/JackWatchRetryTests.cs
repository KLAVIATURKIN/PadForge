using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using Xunit.Abstractions;

namespace PadForge.Tests;

[Collection("AudioProfileLifecycle")]
public sealed class JackWatchRetryTests(ITestOutputHelper output)
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public void TransientOpenFailureRetriesWithoutRepeatingItsFailureLog(bool bluetooth)
    {
        const BindingFlags flags=BindingFlags.Static|BindingFlags.NonPublic;
        var type=typeof(AudioPassthroughService);
        var ensure=type.GetMethod("EnsureJackWatch",flags)!;
        var watches=(IDictionary)type.GetField("_jackWatch",flags)!.GetValue(null);
        var gate=type.GetField("_jackLock",flags)!.GetValue(null);
        var states=(ConcurrentDictionary<Guid,bool>)type.GetField("s_padJackState",flags)!.GetValue(null);
        Guid failed=Guid.NewGuid(),fresh=Guid.NewGuid();
        string path=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"padforge-jack-"+Guid.NewGuid()+".bin");
        var seen=new List<object>();
        object Watch(Guid id){lock(gate)return watches[id];}
        object Start(Guid id)
        {
            ensure.Invoke(null,[id,path,bluetooth]);var watch=Watch(id);
            if(watch!=null&&!seen.Contains(watch))seen.Add(watch);return watch;
        }
        bool Failed(object watch)=>(bool)watch.GetType().GetField("OpenFailed")!.GetValue(watch);
        void Expire(object watch){lock(gate)watch.GetType().GetField("RetryAfterTick")!.SetValue(watch,Environment.TickCount64-1);}
        int FailureLines()=>SdlDiagLog.Snapshot().Split('\n').Count(s=>s.Contains("JACKWATCH open FAILED"));
        int before=FailureLines();
        try
        {
            var first=Start(failed);Assert.True(SpinWait.SpinUntil(()=>Failed(first),2000));
            Assert.Same(first,Start(failed));
            Expire(first);var second=Start(failed);Assert.NotSame(first,second);
            Assert.True(SpinWait.SpinUntil(()=>Failed(second),2000));Assert.Equal(before+1,FailureLines());
            var report=new byte[78];report[0]=bluetooth?(byte)0x31:(byte)1;report[bluetooth?55:54]=1;
            System.IO.File.WriteAllBytes(path,report);Start(fresh);
            Assert.True(SpinWait.SpinUntil(()=>AudioPassthroughService.TryGetHeadphoneJack(fresh)==true,2000));
            Assert.Same(second,Start(failed));Assert.Null(AudioPassthroughService.TryGetHeadphoneJack(failed));
            Expire(second);var recovered=Start(failed);Assert.NotSame(second,recovered);
            Assert.True(SpinWait.SpinUntil(()=>AudioPassthroughService.TryGetHeadphoneJack(failed)==true,2000));
            Assert.Same(recovered,Start(failed));
            output.WriteLine($"BT={bluetooth}: initial failure logged once, immediate retry held, same path recovered, fresh reader control observed plugged.");
        }
        finally
        {
            foreach(var watch in seen)watch.GetType().GetField("Stop")!.SetValue(watch,true);
            foreach(var watch in seen)Assert.True(((Thread)watch.GetType().GetField("Thread")!.GetValue(watch)).Join(2000));
            lock(gate){watches.Remove(failed);watches.Remove(fresh);}
            states.TryRemove(failed,out _);states.TryRemove(fresh,out _);
            if(System.IO.File.Exists(path))System.IO.File.Delete(path);
        }
    }
}

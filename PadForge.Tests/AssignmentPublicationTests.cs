using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.ViewModels;
using Xunit.Abstractions;

namespace PadForge.Tests;

[Collection("AudioProfileLifecycle")]
public sealed class AssignmentPublicationTests(ITestOutputHelper output)
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    sealed class PausingComparer(Guid blocked) : IEqualityComparer<Guid>, IDisposable
    {
        public Func<bool> Empty;
        public readonly ManualResetEventSlim Paused = new();
        public readonly ManualResetEventSlim Release = new();
        int paused;
        public bool Equals(Guid a, Guid b) => a == b;
        public int GetHashCode(Guid value)
        {
            if (value == blocked && Empty() && Interlocked.CompareExchange(ref paused, 1, 0) == 0)
            {
                Paused.Set();
                if (!Release.Wait(5000)) throw new TimeoutException("Publication was not released.");
            }
            return value.GetHashCode();
        }
        public void Dispose() { Paused.Dispose(); Release.Dispose(); }
    }

    [Fact] public void AssignmentPublicationDoesNotMergeConcurrentSnapshots()
    {
        var a=Guid.NewGuid();var b=Guid.NewGuid();var c=Guid.NewGuid();
        using var comparer=new PausingComparer(b);var previous=new HashSet<Guid>(comparer){a};comparer.Empty=()=>previous.Count==0;
        using var dispatcher=new UserEffectsDispatcher(15,new DeviceSlotConfig(),startTimer:false);
        typeof(UserEffectsDispatcher).GetField("_lastAssignedGuids",Private)!.SetValue(dispatcher,previous);
        var observe=typeof(UserEffectsDispatcher).GetMethod("OnAssignmentSetObserved",Private)!;
        var devices=new DeviceCollection();
        Exception firstError=null,secondError=null;
        using var secondStarted=new ManualResetEventSlim();
        using var secondFinished=new ManualResetEventSlim();
        var first=new Thread(()=>{try{observe.Invoke(dispatcher,[new List<Guid>{b},devices]);}catch(Exception e){firstError=e;}});
        var second=new Thread(()=>{secondStarted.Set();try{observe.Invoke(dispatcher,[new List<Guid>{c},devices]);}catch(Exception e){secondError=e;}finally{secondFinished.Set();}});
        bool finishedWhilePaused=false;
        try
        {
            first.Start();Assert.True(comparer.Paused.Wait(5000));second.Start();Assert.True(secondStarted.Wait(5000));
            finishedWhilePaused=secondFinished.Wait(1000);
        }
        finally
        {
            comparer.Release.Set();Assert.True(first.Join(5000));
            if(second.ThreadState!=ThreadState.Unstarted)Assert.True(second.Join(5000));
        }
        output.WriteLine($"second completed during first publication={finishedWhilePaused}; final count={previous.Count}");
        Assert.Null(firstError);Assert.Null(secondError);Assert.False(finishedWhilePaused);
        Assert.Single(previous);Assert.Contains(c,previous);
    }
}

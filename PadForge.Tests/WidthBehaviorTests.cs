using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PadForge.Common;
using Xunit.Abstractions;

namespace PadForge.Tests;

[Collection("AudioProfileLifecycle")]
public sealed class WidthBehaviorTests(ITestOutputHelper output)
{
    [Theory] [InlineData("disable")] [InlineData("unload")] [InlineData("regroup")]
    public void RetiredWidthContributorLeavesItsNeighbors(string transition)=>Sta(()=>
    {
        string group=Guid.NewGuid().ToString();
        var a=new ComboBox{ItemsSource=new[]{new string('W',90)}};
        var b=new ComboBox{ItemsSource=new[]{"A"}};
        foreach(var c in new[]{a,b}){ComboBoxWidthBehavior.SetWidthGroup(c,group);ComboBoxWidthBehavior.SetSizeToItems(c,true);}
        var panel=new StackPanel();panel.Children.Add(a);panel.Children.Add(b);
        var window=new Window{Content=panel,Width=500,Height=200,ShowActivated=false,Left=-10000,Top=-10000};
        void Drain()=>window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
        try
        {
            window.Show();Drain();Assert.True(a.IsLoaded&&b.IsLoaded);double before=b.Width;
            if(transition=="disable")ComboBoxWidthBehavior.SetSizeToItems(a,false);
            else if(transition=="unload"){panel.Children.Remove(a);Drain();}
            else ComboBoxWidthBehavior.SetWidthGroup(a,group+"-other");
            double afterRetire=b.Width;a.Width=123;
            b.ItemsSource=new[]{"A medium option"};double sibling=b.Width,retired=a.Width;
            if(transition=="disable")ComboBoxWidthBehavior.SetSizeToItems(a,true);
            else if(transition=="unload"){panel.Children.Add(a);Drain();}
            else ComboBoxWidthBehavior.SetWidthGroup(a,group);
            double restored=b.Width;
            output.WriteLine($"transition={transition} before={before} retired={afterRetire} sibling={sibling} disabled width={retired} restored={restored}");
            Assert.Equal(before,restored);Assert.True(afterRetire<before);Assert.Equal(123,retired);Assert.True(sibling<before);
        }
        finally{window.Close();}
    });

    [Fact] public void DisabledControlIgnoresItemsUntilReenabled()=>Sta(()=>
    {
        var combo=new ComboBox{ItemsSource=new[]{"A"}};
        ComboBoxWidthBehavior.SetSizeToItems(combo,true);
        var window=new Window{Content=combo,Width=500,Height=150,ShowActivated=false,Left=-10000,Top=-10000};
        try
        {
            window.Show();window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            double before=combo.Width;ComboBoxWidthBehavior.SetSizeToItems(combo,false);
            combo.ItemsSource=new[]{new string('W',90)};double disabled=combo.Width;
            ComboBoxWidthBehavior.SetSizeToItems(combo,true);double enabled=combo.Width;
            Assert.True(enabled>before);Assert.Equal(before,disabled);
        }
        finally{window.Close();}
    });

    [Fact] public void DisablingSizingReleasesThePropertyDescriptorRoot()=>Sta(()=>
    {
        var enabled=SubscribedControl(false);var disabled=SubscribedControl(true);
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
        bool enabledAlive=enabled.TryGetTarget(out var control);
        bool disabledAlive=disabled.TryGetTarget(out _);
        if(control!=null)ComboBoxWidthBehavior.SetSizeToItems(control,false);
        Assert.True(enabledAlive);Assert.False(disabledAlive);
    });

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static WeakReference<ComboBox> SubscribedControl(bool disable)
    {
        var combo=new ComboBox();ComboBoxWidthBehavior.SetSizeToItems(combo,true);
        combo.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        if(disable)ComboBoxWidthBehavior.SetSizeToItems(combo,false);
        return new WeakReference<ComboBox>(combo);
    }

    static void Sta(Action action)
    {
        Exception failure=null;var thread=new Thread(()=>{try{action();}catch(Exception e){failure=e;}}){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(10000));
        if(failure!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

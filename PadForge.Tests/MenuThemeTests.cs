using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using PadForge.Common.Input;
using PadForge.Engine.Menus;
using PadForge.Views;
using Wpf.Ui.Appearance;

namespace PadForge.Tests;

[Collection("AudioProfileLifecycle")]
public sealed class MenuThemeTests
{
    [Theory]
    [InlineData(MenuKind.Grid)]
    [InlineData(MenuKind.Radial)]
    public void OpenMenuRefreshesThemeAndKeepsItsHover(MenuKind kind)
    {
        Exception failure=null;
        var thread=new Thread(()=>
        {
            var field=typeof(ApplicationThemeManager).GetField("_cachedApplicationTheme",BindingFlags.Static|BindingFlags.NonPublic)!;
            var saved=field.GetValue(null);
            var window=new MenuOverlayWindow{Topmost=false,Left=-10000,Top=-10000,Opacity=0};
            window.LocationChanged+=(_,_)=>{if(window.Left>-9000)window.Left=-10000;if(window.Top>-9000)window.Top=-10000;};
            try
            {
                int hover=kind==MenuKind.Radial?1:0,other=hover+1;
                MenuDefinitionEntry Menu()
                {
                    var m=new MenuDefinitionEntry{Kind=kind,CellCount=2,ShowLabels=true};
                    m.Items.Add(new MenuItemDefinition{Index=hover,Label="Selected"});
                    m.Items.Add(new MenuItemDefinition{Index=other,Label="Theme"});
                    return m;
                }
                Color Fill(int i)=>((SolidColorBrush)((Dictionary<int,Shape>)typeof(MenuOverlayWindow).GetField("_cellShapes",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window))[i].Fill).Color;
                void Update(MenuDefinitionEntry m){window.UpdateFromSnapshot(new InputManager.MenuOverlayState{Menu=m,HoveredIndex=hover});window.Opacity=0;}
                var menu=Menu();field.SetValue(null,ApplicationTheme.Dark);Update(menu);
                Color dark=Fill(other),selected=Fill(hover);
                Assert.NotEqual(dark,selected);
                field.SetValue(null,ApplicationTheme.Light);Update(menu);
                Color same=Fill(other);Assert.Equal(selected,Fill(hover));
                var control=Menu();Update(control);Color rebuilt=Fill(other);
                Assert.NotEqual(dark,rebuilt);Assert.Equal(rebuilt,same);
                field.SetValue(null,ApplicationTheme.Dark);Update(control);
                Assert.Equal(dark,Fill(other));Assert.Equal(selected,Fill(hover));
            }
            catch(Exception e){failure=e;}
            finally{window.Close();field.SetValue(null,saved);}
        }){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(10000));
        if(failure!=null)ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

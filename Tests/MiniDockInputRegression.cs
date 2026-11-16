using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nava.Core;
using Nava.Views;

namespace Nava.Tests;

/// <summary>Own-view pointer eligibility checks, without a native window or generated physical input.</summary>
public static class MiniDockInputRegression
{
    public static void Verify(OverlayWindow overlay,ContentControl host,string reportPath)
    {
        var dock=overlay.FindName("MiniDock") as Button ?? throw new InvalidOperationException("Mini dock is missing");
        var avatar=overlay.FindName("MiniAvatar") as CharacterAvatar ?? throw new InvalidOperationException("Mini avatar is missing");
        string previous=avatar.CharacterId;
        var evidence=new List<object>();
        var points=new[] { new Point(.5,.5),new Point(39.5,.5),new Point(.5,43.5),new Point(39.5,43.5),new Point(20,22) };
        try
        {
            foreach(var character in CharacterCatalog.All)
            {
                overlay.Collapse(); avatar.SetCurrentValue(CharacterAvatar.CharacterIdProperty,character.Id);
                dock.ApplyTemplate();
                host.Width=overlay.Width; host.Height=overlay.Height;
                host.Measure(new Size(host.Width,host.Height)); host.Arrange(new Rect(0,0,host.Width,host.Height)); host.UpdateLayout();
                DependencyObject? centerHit=null; int matched=0;
                foreach(var localPoint in points)
                {
                    var hostPoint=dock.TranslatePoint(localPoint,host);
                    var hit=host.InputHitTest(hostPoint) as DependencyObject;
                    // Detached visual roots have no source visibility. Apply the same
                    // local input policy to their rendered geometry when needed.
                    if(hit==null && PresentationSource.FromVisual(host)==null) hit=DetachedInputHitTest(host,hostPoint);
                    if(!DescendsFrom(hit,dock)) throw new InvalidOperationException("Mini dock pointer target missing for "+character.Id+" at "+localPoint);
                    matched++; if(localPoint.X==20 && localPoint.Y==22) centerHit=hit;
                }

                // Native layered windows route fully transparent pixels through to
                // the window underneath. The entire 40x44 target needs nonzero alpha.
                var bitmap=new RenderTargetBitmap(40,44,96,96,PixelFormats.Pbgra32); bitmap.Render(dock);
                byte[] pixels=new byte[40*44*4]; bitmap.CopyPixels(pixels,40*4,0);
                int transparent=0; byte minimumAlpha=255;
                for(int offset=3;offset<pixels.Length;offset+=4) { if(pixels[offset]==0) transparent++; minimumAlpha=Math.Min(minimumAlpha,pixels[offset]); }
                if(transparent!=0) throw new InvalidOperationException("Mini dock has "+transparent+" click-through pixels for "+character.Id);

                double left=overlay.MiniLeft,top=overlay.MiniTop;
                if(centerHit is not UIElement eventSource) throw new InvalidOperationException("Mini target cannot route input events");
                eventSource.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,eventSource));
                if(!overlay.IsExpanded) throw new InvalidOperationException("Mini dock click route did not expand "+character.Id);
                overlay.Collapse();
                if(Math.Abs(overlay.MiniLeft-left)>.01 || Math.Abs(overlay.MiniTop-top)>.01) throw new InvalidOperationException("Click expansion moved the mini anchor");
                evidence.Add(new { character=character.Id,hitPoints=matched,targetPixels=40*44,transparentPixels=transparent,minimumAlpha,clickRouted=true });
            }
            File.WriteAllText(reportPath,JsonSerializer.Serialize(new { scope="Detached own-view input hit path and rendered alpha; no physical mouse or desktop interaction",characters=evidence },new JsonSerializerOptions { WriteIndented=true }));
        }
        finally { avatar.SetCurrentValue(CharacterAvatar.CharacterIdProperty,previous); overlay.Collapse(); }
    }

    private static DependencyObject? DetachedInputHitTest(Visual root,Point point)
    {
        DependencyObject? result=null;
        VisualTreeHelper.HitTest(root,node=> {
            if(node is UIElement element && (!element.IsHitTestVisible || !element.IsEnabled || element.Visibility!=Visibility.Visible)) return HitTestFilterBehavior.ContinueSkipSelfAndChildren;
            return HitTestFilterBehavior.Continue;
        },hit=> { result=hit.VisualHit; return HitTestResultBehavior.Stop; },new PointHitTestParameters(point));
        return result;
    }
    private static bool DescendsFrom(DependencyObject? current,DependencyObject expected)
    {
        for(;current!=null;current=current is Visual?VisualTreeHelper.GetParent(current):LogicalTreeHelper.GetParent(current)) if(ReferenceEquals(current,expected)) return true;
        return false;
    }
}

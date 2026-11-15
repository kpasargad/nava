using System;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using Nava.Core;

namespace Nava.Views;

/// <summary>Original, transparent vector characters drawn in a shared 48-unit coordinate space.</summary>
public sealed class CharacterAvatar : FrameworkElement
{
    public static readonly DependencyProperty CharacterIdProperty=DependencyProperty.Register(nameof(CharacterId),typeof(string),typeof(CharacterAvatar),new FrameworkPropertyMetadata(CharacterCatalog.DefaultId,FrameworkPropertyMetadataOptions.AffectsRender,null,(_,value)=>CharacterCatalog.Normalize(value as string)));
    public static readonly DependencyProperty IsRecordingProperty=DependencyProperty.Register(nameof(IsRecording),typeof(bool),typeof(CharacterAvatar),new FrameworkPropertyMetadata(false,FrameworkPropertyMetadataOptions.AffectsRender));
    public string CharacterId { get=>(string)GetValue(CharacterIdProperty); set=>SetValue(CharacterIdProperty,value); }
    public bool IsRecording { get=>(bool)GetValue(IsRecordingProperty); set=>SetValue(IsRecordingProperty,value); }
    private static readonly ConcurrentDictionary<string,SolidColorBrush> Brushes=new();
    private static readonly ConcurrentDictionary<string,Geometry> Shapes=new();
    private const string Ink="#263C3A", Blush="#C9F0A699", Cream="#FFF5E5";
    public CharacterAvatar() { IsHitTestVisible=false; Focusable=false; UseLayoutRounding=true; }
    protected override Size MeasureOverride(Size available)=>new(double.IsInfinity(available.Width)?48:Math.Min(48,available.Width),double.IsInfinity(available.Height)?48:Math.Min(48,available.Height));
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        double scale=Math.Min(RenderSize.Width/48,RenderSize.Height/48); if(scale<=0) return;
        context.PushTransform(new TranslateTransform((RenderSize.Width-48*scale)/2,(RenderSize.Height-48*scale)/2));
        context.PushTransform(new ScaleTransform(scale,scale));
        switch(CharacterCatalog.Normalize(CharacterId))
        {
            case "cat": Cat(context); break;
            case "rabbit": Rabbit(context); break;
            case "fox": Fox(context); break;
            case "bear": Bear(context); break;
            case "panda": Panda(context); break;
            case "frog": Frog(context); break;
            case "penguin": Penguin(context); break;
            case "owl": Owl(context); break;
            case "octopus": Octopus(context); break;
            default: Ghost(context); break;
        }
        if(IsRecording) { Dot(context,43,5,3.2,3.2,"#23342F"); Dot(context,43,5,2.2,2.2,"#F5B8A7"); }
        context.Pop(); context.Pop();
    }
    private static SolidColorBrush Brush(string color)=>Brushes.GetOrAdd(color,key=> { var result=new SolidColorBrush((Color)ColorConverter.ConvertFromString(key)); result.Freeze(); return result; });
    private static Geometry Shape(string path)=>Shapes.GetOrAdd(path,key=> { var result=Geometry.Parse(key); result.Freeze(); return result; });
    private static Pen Pen(string color,double width) { var pen=new Pen(Brush(color),width) { StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round }; pen.Freeze(); return pen; }
    private static void Fill(DrawingContext dc,string path,string color)=>dc.DrawGeometry(Brush(color),null,Shape(path));
    private static void Line(DrawingContext dc,string path,string color=Ink,double width=1.6)=>dc.DrawGeometry(null,Pen(color,width),Shape(path));
    private static void Dot(DrawingContext dc,double x,double y,double rx,double ry,string color)=>dc.DrawEllipse(Brush(color),null,new Point(x,y),rx,ry);
    private static void Eyes(DrawingContext dc,double left,double right,double y,double rx=2,double ry=3.4) { Dot(dc,left,y,rx,ry,Ink); Dot(dc,right,y,rx,ry,Ink); }
    private static void Cheeks(DrawingContext dc,double left,double right,double y) { Dot(dc,left,y,3,1.5,Blush); Dot(dc,right,y,3,1.5,Blush); }
    private static void Ghost(DrawingContext dc)
    {
        Fill(dc,"M24,2 C39,2 45,11 45,25 L45,39 Q44,48 36,43 Q30,49 24,44 Q17,49 11,44 Q3,48 3,39 L3,25 C3,10 10,2 24,2 Z","#BAEDCD");
        Eyes(dc,15,32,23); Cheeks(dc,10,38,30); Line(dc,"M20,31 Q24,35 28,31","#44664F",1.7);
    }
    private static void Cat(DrawingContext dc)
    {
        Fill(dc,"M7,21 L7,7 Q8,4 11,7 L20,13 Q24,11 28,13 L37,6 Q40,4 41,8 L42,23 Q46,39 35,44 Q24,48 12,43 Q2,38 7,21 Z","#F4BD99");
        Fill(dc,"M10,10 L10,19 L17,16 Z","#D69289"); Fill(dc,"M38,10 L31,16 L38,19 Z","#D69289");
        Eyes(dc,17,31,26,2,3); Cheeks(dc,11,37,33);
        Fill(dc,"M21.5,31 L26.5,31 Q24,35 21.5,31 Z","#9E665B");
        Line(dc,"M24,33 Q21,37 19,34 M24,33 Q27,37 29,34","#77534B",1.4);
        Line(dc,"M4,27 L12,29 M3,33 L12,32 M36,29 L44,27 M36,32 L45,33","#8C6556",1.35);
    }
    private static void Rabbit(DrawingContext dc)
    {
        Fill(dc,"M12,26 C5,17 7,2 13,2 C21,2 19,19 21,25 Z","#D6C7ED");
        Fill(dc,"M27,25 C28,17 28,2 35,2 C42,3 42,18 35,27 Z","#D6C7ED");
        Fill(dc,"M12,7 C9,12 11,19 14,23 C15,17 17,8 12,7 Z","#E6ABBD");
        Fill(dc,"M35,7 C31,10 32,18 32,23 C36,19 39,10 35,7 Z","#E6ABBD");
        Dot(dc,24,32,18,14.5,"#D6C7ED"); Eyes(dc,17,31,30,2,3.1); Cheeks(dc,11,37,36);
        Fill(dc,"M21,35 Q24,33 27,35 L24,38 Z","#AE7A93");
        Line(dc,"M24,38 Q21,41 19,38 M24,38 Q27,41 29,38","#7C647C",1.3);
        Fill(dc,"M22,39 L26,39 L26,43 Q24,44 22,43 Z","#FFF9F1");
    }
    private static void Fox(DrawingContext dc)
    {
        Fill(dc,"M5,21 L5,3 L19,13 Q24,11 29,13 L43,3 L43,21 Q46,34 24,46 Q2,34 5,21 Z","#EBA46C");
        Fill(dc,"M9,9 L10,20 L17,16 Z","#AA6958"); Fill(dc,"M39,9 L31,16 L38,20 Z","#AA6958");
        Fill(dc,"M7,25 C11,20 17,26 24,33 C31,26 37,20 41,25 C40,35 32,42 24,44 C16,42 8,35 7,25 Z",Cream);
        Eyes(dc,16,32,26,2,3); Dot(dc,24,34,3,2,Ink); Line(dc,"M21,38 Q24,41 27,38","#805F53",1.4);
    }
    private static void Bear(DrawingContext dc)
    {
        Dot(dc,10,11,8,8,"#E8BC74"); Dot(dc,38,11,8,8,"#E8BC74");
        Dot(dc,10,11,4.2,4.2,"#BD8C56"); Dot(dc,38,11,4.2,4.2,"#BD8C56");
        Dot(dc,24,28,21,18,"#E8BC74"); Eyes(dc,16,32,25,2,3); Cheeks(dc,9,39,32);
        Dot(dc,24,35,9.5,7.7,"#F7DCAB"); Dot(dc,24,32,3.3,2.1,"#755744");
        Line(dc,"M24,34 L24,37 M20,37 Q24,41 28,37","#755744",1.5);
    }
    private static void Panda(DrawingContext dc)
    {
        Dot(dc,10,10,7.5,7.5,"#34474A"); Dot(dc,38,10,7.5,7.5,"#34474A");
        Dot(dc,24,28,21,18.5,"#EEEAE0");
        dc.PushTransform(new RotateTransform(23,15,25)); Dot(dc,15,25,6,8,"#34474A"); dc.Pop();
        dc.PushTransform(new RotateTransform(-23,33,25)); Dot(dc,33,25,6,8,"#34474A"); dc.Pop();
        Dot(dc,15,24.5,1.8,2.8,"#FFF9EE"); Dot(dc,33,24.5,1.8,2.8,"#FFF9EE");
        Dot(dc,15.2,25,0.9,1.7,Ink); Dot(dc,32.8,25,0.9,1.7,Ink);
        Cheeks(dc,9,39,34); Dot(dc,24,34,2.8,1.8,Ink); Line(dc,"M21,38 Q24,41 27,38","#63736B",1.4);
    }
    private static void Frog(DrawingContext dc)
    {
        Dot(dc,13,12,9,9,"#ABD39B"); Dot(dc,35,12,9,9,"#ABD39B");
        Dot(dc,24,29,22,16,"#ABD39B");
        Dot(dc,13,12,5.8,5.8,"#F5F4D9"); Dot(dc,35,12,5.8,5.8,"#F5F4D9");
        Eyes(dc,13,35,12,2.2,3.5); Cheeks(dc,8,40,29);
        Line(dc,"M14,31 Q24,40 34,31","#476A4A",1.8);
        Dot(dc,8,41,4,3,"#8CBD87"); Dot(dc,40,41,4,3,"#8CBD87");
    }
    private static void Penguin(DrawingContext dc)
    {
        Fill(dc,"M10,20 Q1,24 3,37 Q7,40 13,29 Z","#354D63");
        Fill(dc,"M38,20 Q47,24 45,37 Q41,40 35,29 Z","#354D63");
        Dot(dc,17,44,6,2.8,"#E8B16F"); Dot(dc,31,44,6,2.8,"#E8B16F");
        Dot(dc,24,25,18,21,"#354D63");
        Fill(dc,"M11,22 C11,11 20,10 24,17 C28,10 37,11 37,22 L37,30 Q38,43 24,44 Q10,43 11,30 Z","#F4F0E7");
        Eyes(dc,17,31,23,2,3.1); Cheeks(dc,12,36,30);
        Fill(dc,"M20,29 Q24,26 28,29 L24,33 Z","#DEAA68");
    }
    private static void Owl(DrawingContext dc)
    {
        Fill(dc,"M8,16 L7,5 Q14,6 18,10 Q24,8 30,10 Q36,6 41,5 L40,16 Q46,25 41,39 Q37,47 24,47 Q11,47 7,39 Q2,26 8,16 Z","#BAAED8");
        Dot(dc,24,34,13,11,"#D9D0E8");
        Fill(dc,"M7,24 Q3,36 12,41 Q14,32 7,24 Z","#9385BA");
        Fill(dc,"M41,24 Q45,36 36,41 Q34,32 41,24 Z","#9385BA");
        Dot(dc,15,23,9,9,"#F1EDF2"); Dot(dc,33,23,9,9,"#F1EDF2");
        Eyes(dc,15,33,23,2.3,3.6); Fill(dc,"M21,30 L27,30 L24,35 Z","#C79B60");
        Line(dc,"M16,44 L20,44 M28,44 L32,44","#7C6F91",2);
    }
    private static void Octopus(DrawingContext dc)
    {
        const string blue="#9DCAD9";
        Line(dc,"M11,23 C5,23 1,27 3,31 C4,34 8,32 9,29",blue,5.5);
        Line(dc,"M13,28 C5,30 3,38 7,40 C11,42 12,36 15,34",blue,5.5);
        Line(dc,"M18,30 C11,35 12,44 17,44 C21,44 19,39 22,35",blue,5.5);
        Line(dc,"M22,31 C18,37 19,45 23,45 C27,45 24,38 26,34",blue,5.5);
        Line(dc,"M27,31 C24,37 26,45 30,44 C34,43 29,38 30,34",blue,5.5);
        Line(dc,"M32,29 C30,35 33,44 38,42 C42,39 35,36 36,32",blue,5.5);
        Line(dc,"M36,27 C39,28 46,35 43,39 C40,42 38,35 34,32",blue,5.5);
        Line(dc,"M38,22 C44,22 47,27 44,31 C41,34 39,28 36,27",blue,5.5);
        Fill(dc,"M9,25 C7,14 12,3 24,3 C36,3 41,14 39,26 Q39,35 24,35 Q9,35 9,25 Z",blue);
        Eyes(dc,17,31,21,2,3.1); Cheeks(dc,11,37,27); Line(dc,"M20,28 Q24,32 28,28","#4B747B",1.7);
    }
}

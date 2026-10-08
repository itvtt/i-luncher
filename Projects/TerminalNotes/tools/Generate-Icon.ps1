$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
public static class TerminalNotesIconGenerator {
    static GraphicsPath Rounded(float x,float y,float width,float height,float radius) {
        var p=new GraphicsPath(); float d=radius*2;
        p.AddArc(x,y,d,d,180,90); p.AddArc(x+width-d,y,d,d,270,90);
        p.AddArc(x+width-d,y+height-d,d,d,0,90); p.AddArc(x,y+height-d,d,d,90,90);p.CloseFigure();return p;
    }
    static Color ColorOf(string hex) {return ColorTranslator.FromHtml(hex);}
    public static void Generate(string folder) {
        int[] sizes={16,24,32,48,64,128,256}; var pngs=new byte[sizes.Length][];
        for(int i=0;i<sizes.Length;i++) {
            using(var bmp=new Bitmap(sizes[i],sizes[i],PixelFormat.Format32bppArgb))
            using(var g=Graphics.FromImage(bmp)) {
                g.Clear(Color.Transparent); g.SmoothingMode=SmoothingMode.AntiAlias; g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                g.ScaleTransform(sizes[i]/256f,sizes[i]/256f);
                using(var p=Rounded(8,8,240,240,48)) using(var b=new SolidBrush(ColorOf("#101923"))) g.FillPath(b,p);
                using(var p=Rounded(15,15,226,226,41)) using(var pen=new Pen(ColorOf("#29443f"),3)) g.DrawPath(pen,p);
                using(var p=new GraphicsPath()) using(var b=new SolidBrush(ColorOf("#203e39"))) {
                    p.AddLines(new[]{new PointF(154,57),new PointF(197,57),new PointF(221,81),new PointF(221,201),new PointF(154,201)});p.CloseFigure();g.FillPath(b,p);
                }
                using(var b=new SolidBrush(ColorOf("#408470"))) g.FillPolygon(b,new[]{new PointF(197,57),new PointF(197,81),new PointF(221,81)});
                using(var pen=new Pen(ColorOf("#9ee6cd"),6)) {pen.StartCap=pen.EndCap=LineCap.Round;g.DrawLine(pen,171,108,203,108);g.DrawLine(pen,171,131,203,131);g.DrawLine(pen,171,154,193,154);}
                using(var pen=new Pen(ColorOf("#77e6bd"),17)) {pen.StartCap=pen.EndCap=LineCap.Round;pen.LineJoin=LineJoin.Round;g.DrawLines(pen,new[]{new PointF(50,88),new PointF(86,124),new PointF(50,160)});g.DrawLine(pen,101,162,135,162);}
                using(var buffer=new MemoryStream()) {bmp.Save(buffer,ImageFormat.Png);pngs[i]=buffer.ToArray();}
                if(sizes[i]==256) bmp.Save(Path.Combine(folder,"AppIcon.png"),ImageFormat.Png);
            }
        }
        using(var stream=File.Create(Path.Combine(folder,"AppIcon.ico"))) using(var writer=new BinaryWriter(stream)) {
            writer.Write((ushort)0);writer.Write((ushort)1);writer.Write((ushort)sizes.Length);
            int offset=6+16*sizes.Length;
            for(int i=0;i<sizes.Length;i++) {writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)0);writer.Write((byte)0);writer.Write((ushort)1);writer.Write((ushort)32);writer.Write(pngs[i].Length);writer.Write(offset);offset+=pngs[i].Length;}
            foreach(var png in pngs) writer.Write(png);
        }
    }
}
'@
[TerminalNotesIconGenerator]::Generate((Join-Path $PSScriptRoot '../Assets'))

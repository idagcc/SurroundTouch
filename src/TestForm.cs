using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SurroundTouch
{
    internal sealed class TestForm : Form
    {
        readonly PixelRect[] regions;
        readonly Dictionary<uint,Point> contacts=new Dictionary<uint,Point>();
        readonly Queue<Point> trail=new Queue<Point>();
        readonly bool corrected;
        int total,max;
        public TestForm(Rectangle bounds,PixelRect[] regions,bool corrected)
        {
            this.regions=regions;this.corrected=corrected;
            Text="SurroundTouch — touch test";FormBorderStyle=FormBorderStyle.None;StartPosition=FormStartPosition.Manual;Bounds=bounds;TopMost=true;KeyPreview=true;
            BackColor=Color.FromArgb(13,24,40);ForeColor=Color.White;DoubleBuffered=true;
            KeyDown+=delegate(object sender,KeyEventArgs e) { if(e.KeyCode==Keys.Escape)Close(); };
            MouseDown+=delegate(object sender,MouseEventArgs e) { if(e.Y<70&&e.X>ClientSize.Width-160)Close(); };
        }
        protected override void WndProc(ref Message m)
        {
            if(m.Msg==0x0245||m.Msg==0x0246||m.Msg==0x0247)
            {
                uint id=(uint)(m.WParam.ToInt64()&0xffff);Native.TOUCH touch;
                if(Native.GetPointerTouchInfo(id,out touch))
                {
                    var p=touch.pointerInfo;Point at=PointToClient(new Point(p.ptPixelLocation.X,p.ptPixelLocation.Y));
                    if((p.pointerFlags&Native.DOWN)!=0) { total++;if(at.Y<70&&at.X>ClientSize.Width-160) { Close();m.Result=IntPtr.Zero;return; } }
                    if((p.pointerFlags&(Native.UP|Native.CANCELED))!=0)contacts.Remove(id);else contacts[id]=at;
                    max=Math.Max(max,contacts.Count);trail.Enqueue(at);while(trail.Count>1800)trail.Dequeue();Invalidate();
                }
                m.Result=IntPtr.Zero;return;
            }
            if(m.Msg==0x024C) { contacts.Clear();Invalidate(); }
            base.WndProc(ref m);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);Graphics g=e.Graphics;g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using(var font=new Font("Segoe UI",20,FontStyle.Bold))g.DrawString("Touch accuracy test",font,Brushes.White,28,18);
            using(var font=new Font("Segoe UI",12))
            {
                g.DrawString("Close / Esc",font,Brushes.White,ClientSize.Width-140,24);
                g.DrawString((corrected?"Correction active":"Original Windows input")+"   |   Active: "+contacts.Count+"   Maximum simultaneous: "+max+"   Touches: "+total,font,Brushes.LightSteelBlue,28,72);
                g.DrawString("Touch the targets and draw across each screen. Hold a finger on BOTH screens together. Dots should stay under your fingers.",font,Brushes.LightSteelBlue,28,102);
            }
            using(var pen=new Pen(Color.FromArgb(42,66,91),1))
            {
                for(int x=0;x<Width;x+=80)g.DrawLine(pen,x,140,x,Height);for(int y=160;y<Height;y+=80)g.DrawLine(pen,0,y,Width,y);
            }
            for(int n=0;n<regions.Length;n++)
            {
                PixelRect r=regions[n];int left=r.X-Left,top=r.Y-Top;
                using(var pen=new Pen(n==0?Color.CornflowerBlue:Color.Turquoise,2))
                {
                    g.DrawRectangle(pen,left+2,Math.Max(top+2,140),r.Width-5,r.Height-Math.Max(142-top,5));
                    double[] points={0.12,0.5,0.88};foreach(double u in points)foreach(double v in points)
                    { int x=left+(int)(u*(r.Width-1)),y=top+(int)(v*(r.Height-1));if(y<155)continue;g.DrawEllipse(pen,x-18,y-18,36,36);g.DrawLine(pen,x-26,y,x+26,y);g.DrawLine(pen,x,y-26,x,y+26); }
                }
            }
            using(var brush=new SolidBrush(Color.FromArgb(100,121,155)))foreach(Point p in trail)g.FillEllipse(brush,p.X-2,p.Y-2,4,4);
            foreach(var pair in contacts)
            {
                Point p=pair.Value;g.FillEllipse(Brushes.Gold,p.X-13,p.Y-13,26,26);g.DrawString(pair.Key.ToString(),Font,Brushes.White,p.X+18,p.Y-10);
            }
        }
    }
}

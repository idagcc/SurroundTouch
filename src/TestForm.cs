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
        readonly bool driverCheck;
        readonly TouchObservation observation=new TouchObservation();
        readonly List<TouchSample> observedFrame=new List<TouchSample>(16);
        readonly Dictionary<IntPtr,string> sourceNames=new Dictionary<IntPtr,string>();
        readonly Native.TOUCH[] frame=new Native.TOUCH[256];
        readonly Timer paintTimer=new Timer { Interval=33 };
        bool dirty;
        int total,max;
        public TestForm(Rectangle bounds,PixelRect[] regions,bool corrected,bool driverCheck=false)
        {
            this.regions=regions;this.corrected=corrected;this.driverCheck=driverCheck;
            Text="SurroundTouch — touch test";FormBorderStyle=FormBorderStyle.None;StartPosition=FormStartPosition.Manual;Bounds=bounds;TopMost=true;KeyPreview=true;
            BackColor=Color.FromArgb(13,24,40);ForeColor=Color.White;DoubleBuffered=true;
            KeyDown+=delegate(object sender,KeyEventArgs e) { if(e.KeyCode==Keys.Escape)Close(); };
            MouseDown+=delegate(object sender,MouseEventArgs e) { if(e.Y<70&&e.X>ClientSize.Width-160)Close(); };
            foreach(TouchDevice d in Native.Devices())sourceNames[d.Handle]=d.Name+" | "+d.Path;
            paintTimer.Tick+=delegate { if(dirty) { dirty=false;Invalidate(); } };paintTimer.Start();
            FormClosed+=delegate { paintTimer.Dispose();DriverDiagnostics.LastTest=observation.Report(corrected); };
        }
        protected override void WndProc(ref Message m)
        {
            if(m.Msg==0x0245||m.Msg==0x0246||m.Msg==0x0247)
            {
                uint id=(uint)(m.WParam.ToInt64()&0xffff),count=(uint)frame.Length;
                if(Native.GetPointerFrameTouchInfo(id,ref count,frame))
                {
                    observedFrame.Clear();
                    for(int i=0;i<count&&i<frame.Length;i++)
                    {
                        var p=frame[i].pointerInfo;id=p.pointerId;Point at=PointToClient(new Point(p.ptPixelLocation.X,p.ptPixelLocation.Y));
                        if((p.pointerFlags&Native.DOWN)!=0) { total++;if(at.Y<70&&at.X>ClientSize.Width-160) { Close();m.Result=IntPtr.Zero;return; } }
                        string source;
                        if(!sourceNames.TryGetValue(p.sourceDevice,out source))source="Source handle "+p.sourceDevice.ToInt64();
                        TouchPhase phase=(p.pointerFlags&Native.CANCELED)!=0?TouchPhase.Cancel:(p.pointerFlags&Native.UP)!=0?TouchPhase.Up:(p.pointerFlags&Native.DOWN)!=0?TouchPhase.Down:TouchPhase.Move;
                        if(phase==TouchPhase.Up||phase==TouchPhase.Cancel)contacts.Remove(id);
                        else if(phase==TouchPhase.Down||(p.pointerFlags&Native.INCONTACT)!=0)contacts[id]=at;
                        observedFrame.Add(new TouchSample { Id=id,Device=source,Phase=phase });
                        trail.Enqueue(at);while(trail.Count>600)trail.Dequeue();
                    }
                    observation.RecordFrame(observedFrame);max=Math.Max(max,contacts.Count);
                    dirty=true;
                    Native.SkipPointerFrameMessages((uint)(m.WParam.ToInt64()&0xffff));
                }
                m.Result=IntPtr.Zero;return;
            }
            if(m.Msg==0x024C) { contacts.Clear();observation.ClearActive();dirty=true; }
            base.WndProc(ref m);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);Graphics g=e.Graphics;g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using(var font=new Font("Segoe UI",20,FontStyle.Bold))g.DrawString(driverCheck?"Touch driver check":"Touch accuracy test",font,Brushes.White,28,18);
            using(var font=new Font("Segoe UI",12))
            {
                g.DrawString("Close / Esc",font,Brushes.White,ClientSize.Width-140,24);
                g.DrawString((corrected?"Correction active":"Original Windows input")+"   |   Active: "+contacts.Count+"   Maximum simultaneous: "+max+"   Touches: "+total,font,Brushes.LightSteelBlue,28,72);
                g.DrawString(driverCheck?"Hold TWO fingers on each panel, then ONE on each together. Two-touch: "+(observation.PeakContacts>=2?"observed":"waiting")+". Esc saves results for the report.":"Touch the targets and draw across each screen. Hold a finger on BOTH screens together. Dots should stay under your fingers.",font,Brushes.LightSteelBlue,28,102);
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

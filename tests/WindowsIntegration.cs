using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
namespace SurroundTouch
{
    internal sealed class WindowsIntegration : Form
    {
        readonly TouchPipeline pipeline=new TouchPipeline(Native.Inject);
        readonly Timer timer=new Timer();
        readonly string report;
        readonly bool showResult;
        readonly HashSet<uint> ids=new HashSet<uint>();
        readonly HashSet<uint> active=new HashSet<uint>();
        int step,downs,ups,moves,maximum;bool failed;string problem="";
        [DllImport("user32.dll")]static extern IntPtr WindowFromPoint(Native.POINT p);
        [STAThread]static int Main(string[] args)
        {
            Native.DpiAware();Application.EnableVisualStyles();
            var form=new WindowsIntegration(args.Length>0?args[0]:Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"WindowsIntegration-report.txt"),args.Length==0);Application.Run(form);return form.failed?1:0;
        }
        WindowsIntegration(string report,bool showResult)
        {
            this.report=report;this.showResult=showResult;Text="SurroundTouch input integration test";ClientSize=new Size(560,300);StartPosition=FormStartPosition.CenterScreen;TopMost=true;BackColor=Color.White;
            Shown+=delegate { if(!Native.InitializeTouchInjection(256,3)) { failed=true;problem="InitializeTouchInjection: "+Marshal.GetLastWin32Error();Finish();return; } timer.Interval=100;timer.Tick+=Tick;timer.Start(); };
        }
        void Tick(object sender,EventArgs e)
        {
            try
            {
                Point left=PointToScreen(new Point(150,150)),right=PointToScreen(new Point(350,150));
                if(WindowFromPoint(new Native.POINT {X=left.X,Y=left.Y})!=Handle||WindowFromPoint(new Native.POINT {X=right.X,Y=right.Y})!=Handle)throw new InvalidOperationException("Test window was covered; injection aborted.");
                if(step==0)pipeline.Process(new[]{Sample("left",TouchPhase.Down,left),Sample("right",TouchPhase.Down,right)});
                else if(step==1) { left.X+=30;right.X-=30;pipeline.Process(new[]{Sample("left",TouchPhase.Move,left),Sample("right",TouchPhase.Move,right)}); }
                else if(step==2)pipeline.Tick();
                else if(step==3)pipeline.Process(new[]{Sample("left",TouchPhase.Up,left)}); // moved UP must create an UPDATE first
                else if(step==4)pipeline.CancelAll();
                else if(step>=6) { if(downs!=2||ups!=2||moves<2||maximum!=2||active.Count!=0||ids.Count!=2)throw new Exception("Unexpected receive counts.");Finish();return; }
                step++;
            }
            catch(Exception ex) { failed=true;problem=ex.ToString();try { pipeline.CancelAll(); }catch{}Finish(); }
        }
        static TouchSample Sample(string device,TouchPhase phase,Point p) { return new TouchSample {Device=device,Id=7,Phase=phase,Position=new PixelPoint(p.X,p.Y)}; }
        protected override void WndProc(ref Message m)
        {
            if(m.Msg==0x245||m.Msg==0x246||m.Msg==0x247)
            {
                uint id=(uint)(m.WParam.ToInt64()&0xffff);Native.TOUCH t;
                if(Native.GetPointerTouchInfo(id,out t))
                {
                    uint flags=t.pointerInfo.pointerFlags;ids.Add(id);
                    if((flags&Native.DOWN)!=0) { downs++;active.Add(id); }
                    if((flags&Native.UPDATE)!=0)moves++;
                    if((flags&Native.UP)!=0) { ups++;active.Remove(id); }
                    maximum=Math.Max(maximum,active.Count);
                }
                m.Result=IntPtr.Zero;return;
            }
            base.WndProc(ref m);
        }
        void Finish()
        {
            timer.Stop();
            File.WriteAllText(report,(failed?"FAIL":"PASS")+" Windows touch injection integration\r\n"+"Down="+downs+", Up="+ups+", Move="+moves+", Peak simultaneous="+maximum+", remaining="+active.Count+", distinct IDs="+ids.Count+"\r\n"+problem);
            if(showResult)MessageBox.Show(this,(failed?"Test failed. "+problem:"PASS: Windows received two simultaneous touch contacts, movement and clean release.")+"\n\nReport: "+report,"SurroundTouch input test");
            Close();
        }
    }
}

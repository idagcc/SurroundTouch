using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SurroundTouch
{
    internal static class Program
    {
        static readonly object logLock=new object();
        internal static void LogStatus(string text)
        {
            // Record app status only, never touch coordinates or other apps' data.
            try
            {
                lock(logLock)
                {
                    Directory.CreateDirectory(Profile.DirectoryPath);
                    string path=Path.Combine(Profile.DirectoryPath,"status.log");
                    if(File.Exists(path)&&new FileInfo(path).Length>131072)File.WriteAllText(path,"",Encoding.UTF8);
                    File.AppendAllText(path,DateTime.UtcNow.ToString("O")+" "+text+Environment.NewLine,Encoding.UTF8);
                }
            }
            catch { } // A diagnostics failure must never interrupt touch cleanup.
        }
        [STAThread] static int Main(string[] args)
        {
            Native.DpiAware();
            if(args.Length==2 && args[0]=="--diagnostics")
            {
                try { File.WriteAllText(args[1],Diagnostics(),Encoding.UTF8); return 0; }
                catch(Exception ex) { File.WriteAllText(args[1],ex.ToString()); return 1; }
            }
            bool created;
            using(Mutex mutex=new Mutex(true,"Local\\SurroundTouch.Application",out created))
            {
                if(!created) { MessageBox.Show("SurroundTouch is already open. Use its existing window.","SurroundTouch"); return 0; }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                MainForm main=null;
                Application.ThreadException+=delegate(object sender,ThreadExceptionEventArgs e)
                {
                    if(main!=null)main.EmergencyStop();
                    MessageBox.Show(e.Exception.Message+"\nTouch correction has stopped.","SurroundTouch",MessageBoxButtons.OK,MessageBoxIcon.Error);
                };
                main=new MainForm(); Application.Run(main);
            }
            return 0;
        }
        internal static string Diagnostics()
        {
            StringBuilder b=new StringBuilder();
            b.AppendLine("SurroundTouch diagnostics 1.0"); b.AppendLine("UTC: "+DateTime.UtcNow.ToString("O"));
            b.AppendLine("OS: "+Environment.OSVersion+" | process: "+(IntPtr.Size*8)+" bit");
            b.AppendLine("UI Access token: "+Native.HasUIAccess());
            b.AppendLine("Native sizes: POINTER_INFO="+Marshal.SizeOf(typeof(Native.POINTER_INFO))+" TOUCH="+Marshal.SizeOf(typeof(Native.TOUCH))+" DEVICE="+Marshal.SizeOf(typeof(Native.DEVICE)));
            foreach(Screen s in Screen.AllScreens)b.AppendLine("Display: "+s.DeviceName+" "+s.Bounds+" primary="+s.Primary);
            List<TouchDevice> devices=Native.Devices(); b.AppendLine("Touchscreen controllers: "+devices.Count);
            foreach(TouchDevice d in devices)
            {
                b.AppendLine(d.ToString()); b.AppendLine("  Path: "+d.Path);
                b.AppendLine("  Source pixels: "+d.Source.X+","+d.Source.Y+" "+d.Source.Width+"x"+d.Source.Height+" available="+d.HasRect);
            }
            b.AppendLine("Profile directory: "+Profile.DirectoryPath);
            string log=Path.Combine(Profile.DirectoryPath,"status.log");
            try
            {
                if(File.Exists(log)&&new FileInfo(log).Length<=132096)
                {
                    string[] lines=File.ReadAllLines(log);b.AppendLine("Recent app status:");
                    for(int i=Math.Max(0,lines.Length-40);i<lines.Length;i++)b.AppendLine(lines[i]);
                }
            }
            catch(Exception ex) { b.AppendLine("Status log unavailable: "+ex.Message); }
            b.AppendLine("Diagnostic mode does not redirect or inject touch."); return b.ToString();
        }
    }

    internal sealed class MainForm : Form
    {
        static readonly Color Ink=Color.FromArgb(24,38,56),Blue=Color.FromArgb(30,96,220),Muted=Color.FromArgb(87,104,125);
        readonly ComboBox display=new ComboBox(),layout=new ComboBox();
        readonly NumericUpDown split=new NumericUpDown(),gap=new NumericUpDown();
        readonly Label status=new Label(),details=new Label(),activity=new Label();
        readonly Button start=new Button(),stop=new Button(),refresh=new Button(),test=new Button();
        readonly Panel preview=new Panel();
        readonly RoleCard first,second;
        readonly TouchEngine engine;
        readonly List<Control> setup=new List<Control>();
        List<TouchDevice> devices=new List<TouchDevice>();
        Screen[] screens=new Screen[0];
        Profile profile;
        bool busy;
        TestForm testForm;

        public MainForm()
        {
            Text="SurroundTouch"; Font=new Font("Segoe UI",10F); ForeColor=Ink; BackColor=Color.FromArgb(245,248,252);
            ClientSize=new Size(980,860); MinimumSize=new Size(980,899); StartPosition=FormStartPosition.CenterScreen; AutoScaleMode=AutoScaleMode.Dpi;
            var root=new TableLayoutPanel { Dock=DockStyle.Fill,Padding=new Padding(24),ColumnCount=1,RowCount=8 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,78)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,94));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,110)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,204));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,52)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,64));
            root.RowStyles.Add(new RowStyle(SizeType.Percent,100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,32)); Controls.Add(root);
            var heading=new Panel { Dock=DockStyle.Fill };
            heading.Controls.Add(new Label { Text="SurroundTouch",Font=new Font("Segoe UI",23F,FontStyle.Bold),AutoSize=true,Location=new Point(0,0) });
            heading.Controls.Add(new Label { Text="Two touchscreens. One accurate desktop.",AutoSize=true,ForeColor=Muted,Location=new Point(3,47) }); root.Controls.Add(heading);
            var settings=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=4,RowCount=2 };
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,40)); settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,30));settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,15));settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,15));
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute,28));settings.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            string[] labels={"Combined display","Physical arrangement","First screen (%)","Bezel gap (px)"};
            for(int i=0;i<4;i++)settings.Controls.Add(new Label { Text=labels[i],Dock=DockStyle.Fill,ForeColor=Muted },i,0);
            display.DropDownStyle=layout.DropDownStyle=ComboBoxStyle.DropDownList; display.Dock=layout.Dock=DockStyle.Top;
            layout.Items.AddRange(new object[]{"Side by side","Stacked vertically"});layout.SelectedIndex=0;
            split.Minimum=5;split.Maximum=95;split.Value=50;gap.Minimum=0;gap.Maximum=1000;split.Dock=gap.Dock=DockStyle.Top;
            settings.Controls.Add(display,0,1);settings.Controls.Add(layout,1,1);settings.Controls.Add(split,2,1);settings.Controls.Add(gap,3,1);root.Controls.Add(settings);
            preview.Dock=DockStyle.Fill;preview.Paint+=PaintPreview;root.Controls.Add(preview);
            var cards=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2,RowCount=1 };cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
            first=new RoleCard("LEFT SCREEN",Blue);second=new RoleCard("RIGHT SCREEN",Color.FromArgb(0,132,116));cards.Controls.Add(first,0,0);cards.Controls.Add(second,1,0);root.Controls.Add(cards);
            first.Identify.Click+=delegate { Identify(first,"LEFT / FIRST"); };second.Identify.Click+=delegate { Identify(second,"RIGHT / SECOND"); };
            var actions=new FlowLayoutPanel { Dock=DockStyle.Fill,WrapContents=false };
            MakeButton(start,"Start correction",156,Blue,Color.White);MakeButton(stop,"Stop",82,Color.White,Ink);MakeButton(test,"Test touch",110,Color.White,Ink);MakeButton(refresh,"Refresh devices",142,Color.White,Ink);
            Button swap=new Button(),save=new Button();MakeButton(swap,"Swap screens",128,Color.White,Ink);MakeButton(save,"Save profile",116,Color.White,Ink);
            actions.Controls.AddRange(new Control[]{start,stop,test,refresh,swap,save});root.Controls.Add(actions);
            status.Dock=DockStyle.Fill;status.Font=new Font("Segoe UI",11F,FontStyle.Bold);status.Padding=new Padding(10);status.BackColor=Color.White;root.Controls.Add(status);
            details.Dock=DockStyle.Fill;details.ForeColor=Muted;details.Padding=new Padding(4,8,4,0);root.Controls.Add(details);
            var bottom=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2 };bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,65));bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,35));
            activity.Text="Emergency stop: Ctrl + Alt + F10";activity.Dock=DockStyle.Fill;activity.ForeColor=Muted;bottom.Controls.Add(activity);
            var diagnostic=new LinkLabel { Text="Save diagnostic report",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleRight };bottom.Controls.Add(diagnostic);root.Controls.Add(bottom);
            setup.AddRange(new Control[]{display,layout,split,gap,first,second,refresh,swap,save});
            engine=new TouchEngine(this);
            engine.Stopped=delegate(string reason) { busy=false; SetRunning(false); Status(reason,false); };
            engine.Activity=delegate(int contacts,long frames) { if(busy) { status.Text="Correction active  ·  "+contacts+" contacts  ·  "+frames+" frames";status.ForeColor=Color.FromArgb(0,118,86); } };
            start.Click+=delegate { StartCorrection(); }; stop.Click+=delegate { EmergencyStop(); };
            refresh.Click+=delegate { RefreshDevices(); };save.Click+=delegate { TrySave(); };swap.Click+=delegate { var a=first.ReadRole();first.LoadRole(second.ReadRole());second.LoadRole(a); };
            test.Click+=delegate { OpenTest(); };
            diagnostic.LinkClicked+=delegate { SaveDiagnostics(); };
            layout.SelectedIndexChanged+=delegate { LayoutChanged(); };display.SelectedIndexChanged+=delegate { LayoutChanged(); };split.ValueChanged+=delegate { preview.Invalidate(); };gap.ValueChanged+=delegate { preview.Invalidate(); };
            Shown+=delegate
            {
                try { profile=Profile.Load(); }catch(Exception ex) { profile=new Profile();Status("Saved profile could not be loaded: "+ex.Message,true); }
                RefreshDevices();
            };
            FormClosing+=delegate { engine.Dispose(); if(testForm!=null)testForm.Close(); };
            stop.Enabled=false;
        }
        static void MakeButton(Button b,string text,int width,Color back,Color fore) { b.Text=text;b.Size=new Size(width,38);b.FlatStyle=FlatStyle.Flat;b.FlatAppearance.BorderColor=Color.FromArgb(214,222,233);b.BackColor=back;b.ForeColor=fore;b.Cursor=Cursors.Hand; }
        void Status(string text,bool error) { status.Text=text;status.ForeColor=error?Color.FromArgb(180,45,45):Ink; Program.LogStatus(text); }
        void SetRunning(bool value) { foreach(Control c in setup)c.Enabled=!value;start.Enabled=!value;stop.Enabled=value; }
        public void EmergencyStop() { engine.Stop("Stopped. Windows is receiving the original touch input.");busy=false;SetRunning(false); }
        void RefreshDevices()
        {
            try
            {
                devices=Native.Devices(); screens=Screen.AllScreens; display.Items.Clear();
                foreach(Screen s in screens)display.Items.Add(s.DeviceName+"  ·  "+s.Bounds.Width+" × "+s.Bounds.Height);
                int chosen=0;for(int i=0;i<screens.Length;i++)if(screens[i].DeviceName==profile.Display)chosen=i;
                if(screens.Length>0)display.SelectedIndex=chosen;
                layout.SelectedIndex=profile.Vertical?1:0;split.Value=profile.SplitPercent;gap.Value=profile.Gap;
                first.SetDevices(devices);second.SetDevices(devices);first.LoadRole(profile.First);second.LoadRole(profile.Second);LayoutChanged();
                bool access=Native.HasUIAccess();int physical=devices.FindAll(d=>d.IsPhysical).Count;
                Status(physical<2?"Detected "+physical+" physical touchscreen controllers. Two independent controllers are required.":access?"Ready. Identify the left and right screen, then start correction.":"Setup required. Run Install.cmd, then open SurroundTouch from the installed shortcut.",physical<2);
                details.Text=physical+" physical touch controllers detected. "+(access?"Windows UI Access is enabled.":"This portable preview can inspect devices and test local touch; global correction needs installation.")+"\nUse Identify and tap the requested physical screen. Keep all fingers lifted when starting. Closing this app restores normal Windows touch.";
                if(devices.Exists(d=>d.IsUpdd))details.Text+="\nUPDD virtual devices detected and excluded. Stop UPDD before identification using Stop-UPDD.cmd in the package.";
                if(profile.Width>0 && screens.Length>0 && (screens[chosen].Bounds.Width!=profile.Width||screens[chosen].Bounds.Height!=profile.Height))details.Text+="\nThe display resolution has changed. Review the layout before starting.";
            }
            catch(Exception ex) { Status("Device detection failed: "+ex.Message,true); }
        }
        void LayoutChanged() { first.Title.Text=layout.SelectedIndex==1?"TOP SCREEN":"LEFT SCREEN";second.Title.Text=layout.SelectedIndex==1?"BOTTOM SCREEN":"RIGHT SCREEN";preview.Invalidate(); }
        PixelRect[] Regions()
        {
            if(display.SelectedIndex<0||display.SelectedIndex>=screens.Length)throw new InvalidOperationException("Select the combined display.");
            Rectangle r=screens[display.SelectedIndex].Bounds; bool vertical=layout.SelectedIndex==1;
            int total=vertical?r.Height:r.Width,g=(int)gap.Value,usable=total-g;
            if(usable<4)throw new InvalidOperationException("Bezel gap must be smaller than the combined display.");
            int n=(int)Math.Round(usable*(double)split.Value/100.0);
            return vertical?new[]{new PixelRect(r.X,r.Y,r.Width,n),new PixelRect(r.X,r.Y+n+g,r.Width,usable-n)}:
                new[]{new PixelRect(r.X,r.Y,n,r.Height),new PixelRect(r.X+n+g,r.Y,usable-n,r.Height)};
        }
        void PaintPreview(object sender,PaintEventArgs e)
        {
            int w=preview.Width,h=preview.Height; if(w<100)return;
            bool vertical=layout.SelectedIndex==1;
            Rectangle a=vertical?new Rectangle(2,4,w-4,43):new Rectangle(2,4,(w-12)/2,h-18);
            Rectangle b=vertical?new Rectangle(2,53,w-4,43):new Rectangle(w/2+4,4,(w-12)/2,h-18);
            using(var ba=new SolidBrush(Color.FromArgb(229,237,255)))e.Graphics.FillRectangle(ba,a);
            using(var bb=new SolidBrush(Color.FromArgb(222,244,238)))e.Graphics.FillRectangle(bb,b);
            string one=vertical?"TOP":"LEFT",two=vertical?"BOTTOM":"RIGHT";
            try { var r=Regions();one+="  ·  "+r[0].Width+" × "+r[0].Height;two+="  ·  "+r[1].Width+" × "+r[1].Height; }catch { }
            TextRenderer.DrawText(e.Graphics,one,Font,a,Blue,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(e.Graphics,two,Font,b,Color.FromArgb(0,115,98),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
        }
        void Identify(RoleCard card,string name)
        {
            try
            {
                if(devices.FindAll(d=>d.IsPhysical).Count<2)throw new InvalidOperationException("Windows must report both touch controllers. Check each screen's USB touch cable and press Refresh.");
                busy=false;SetRunning(true);Status("Tap the physical "+card.Title.Text+" once. Identification expires in 20 seconds.",false);
                engine.Start(devices,new Dictionary<string,Mapping>(),delegate(TouchDevice d)
                {
                    card.SelectDevice(d.Path);Status(card.Title.Text+" assigned to "+d+". Lift your finger.",false);SetRunning(false);
                });
            }
            catch(Exception ex) { SetRunning(false);Status(ex.Message,true); }
        }
        Dictionary<string,Mapping> Mappings()
        {
            var result=new Dictionary<string,Mapping>(StringComparer.OrdinalIgnoreCase);var regions=Regions();
            RoleCard[] roles={first,second};
            for(int i=0;i<2;i++)
            {
                TouchDevice d=roles[i].SelectedDevice;
                if(d==null||!d.IsPhysical)throw new InvalidOperationException("Identify both physical screens or select each controller.");
                if(String.IsNullOrEmpty(d.Path))throw new InvalidOperationException("Windows did not expose a stable identity for "+d.Name+". Save a diagnostic report.");
                if(result.ContainsKey(d.Path))throw new InvalidOperationException("Each screen must use a different touch controller.");
                Native.RECT p,r;if(!Native.GetPointerDeviceRects(d.Handle,out p,out r))throw new InvalidOperationException("A touch controller disconnected. Refresh devices.");
                var role=roles[i].ReadRole();
                var map=new Mapping { DevicePath=d.Path,Source=r.Pixels,Target=regions[i],Rotation=role.Rotation,FlipX=role.FlipX,FlipY=role.FlipY,MinX=role.MinX,MinY=role.MinY,MaxX=role.MaxX,MaxY=role.MaxY };
                map.Validate();result.Add(d.Path,map);
            }
            return result;
        }
        void StartCorrection()
        {
            try
            {
                var mappings=Mappings();SaveProfile();busy=true;SetRunning(true);Status("Starting touch correction…",false);
                engine.Start(devices,mappings,null);
            }
            catch(Exception ex) { busy=false;SetRunning(false);Status(ex.Message,true); }
        }
        void SaveProfile()
        {
            if(display.SelectedIndex<0)throw new InvalidOperationException("Select a display.");var s=screens[display.SelectedIndex];
            profile=new Profile { Display=s.DeviceName,Width=s.Bounds.Width,Height=s.Bounds.Height,X=s.Bounds.X,Y=s.Bounds.Y,Vertical=layout.SelectedIndex==1,SplitPercent=(int)split.Value,Gap=(int)gap.Value,First=first.ReadRole(),Second=second.ReadRole() };profile.Save();
        }
        void TrySave() { try { SaveProfile();Status("Profile saved. Correction starts only when you press Start.",false); }catch(Exception ex) { Status(ex.Message,true); } }
        void OpenTest()
        {
            if(testForm!=null&&!testForm.IsDisposed) { testForm.Activate();return; }
            try { var regions=Regions();testForm=new TestForm(screens[display.SelectedIndex].Bounds,regions,busy);testForm.Show(this); }catch(Exception ex) { Status(ex.Message,true); }
        }
        void SaveDiagnostics()
        {
            using(var dialog=new SaveFileDialog { Filter="Text report (*.txt)|*.txt",FileName="SurroundTouch-diagnostics.txt" })
                if(dialog.ShowDialog(this)==DialogResult.OK)try { File.WriteAllText(dialog.FileName,Program.Diagnostics(),Encoding.UTF8);Status("Diagnostic report saved.",false); }catch(Exception ex) { Status(ex.Message,true); }
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e);Native.WTSRegisterSessionNotification(Handle,0); }
        protected override void OnHandleDestroyed(EventArgs e) { Native.WTSUnRegisterSessionNotification(Handle);base.OnHandleDestroyed(e); }
        protected override void WndProc(ref Message m)
        {
            // Check actual controller handles when PnP changes. Our injection device
            // also produces these broadcasts and must not stop a healthy receiver.
            if(engine!=null && m.Msg==0x0219 && (m.WParam.ToInt64()==0x8004||m.WParam.ToInt64()==7))engine.CheckDevices();
            if(engine!=null && (m.Msg==0x007E || m.Msg==0x02B1 || (m.Msg==0x0218&&m.WParam.ToInt64()==4)))
            {
                if(engine.Running||busy) { engine.Stop("Display or session changed. Refresh and verify the layout before restarting.");busy=false;SetRunning(false); }
            }
            base.WndProc(ref m);
        }
    }
    internal sealed class RoleCard : Panel
    {
        public readonly Label Title=new Label();public readonly Button Identify=new Button();
        readonly ComboBox device=new ComboBox(),rotation=new ComboBox();readonly CheckBox flipX=new CheckBox(),flipY=new CheckBox();
        readonly NumericUpDown minX=new NumericUpDown(),maxX=new NumericUpDown(),minY=new NumericUpDown(),maxY=new NumericUpDown();
        public TouchDevice SelectedDevice { get { return device.SelectedItem as TouchDevice; } }
        public RoleCard(string title,Color color)
        {
            Size=new Size(430,192);Dock=DockStyle.Fill;BackColor=Color.White;Margin=new Padding(0,0,10,12);Padding=new Padding(12);
            Title.Text=title;Title.ForeColor=color;Title.Font=new Font("Segoe UI",10F,FontStyle.Bold);Title.SetBounds(12,10,200,22);Controls.Add(Title);
            Identify.Text="Identify by touch";Identify.SetBounds(230,5,166,32);Identify.Anchor=AnchorStyles.Top|AnchorStyles.Right;Controls.Add(Identify);
            device.DropDownStyle=ComboBoxStyle.DropDownList;device.SetBounds(12,42,382,30);device.Anchor=AnchorStyles.Left|AnchorStyles.Right|AnchorStyles.Top;Controls.Add(device);
            var rotlabel=new Label { Text="Rotate",Location=new Point(12,82),AutoSize=true };Controls.Add(rotlabel);
            rotation.Items.AddRange(new object[]{"0°","90°","180°","270°"});rotation.SelectedIndex=0;rotation.DropDownStyle=ComboBoxStyle.DropDownList;rotation.SetBounds(69,77,76,30);Controls.Add(rotation);
            flipX.Text="Flip X";flipY.Text="Flip Y";flipX.SetBounds(158,78,83,28);flipY.SetBounds(250,78,83,28);Controls.Add(flipX);Controls.Add(flipY);
            Controls.Add(new Label { Text="Input range %  (normally 0 – 100)",Location=new Point(12,114),AutoSize=true,ForeColor=Color.DimGray });
            NumericUpDown[] fields={minX,maxX,minY,maxY};string[] names={"X from","to","Y from","to"};int[] xpos={12,119,208,315};
            for(int i=0;i<4;i++) { fields[i].Minimum=0;fields[i].Maximum=100;fields[i].DecimalPlaces=1;fields[i].Increment=0.1m;fields[i].SetBounds(xpos[i]+(i%2==0?48:22),145,58,26);Controls.Add(fields[i]);Controls.Add(new Label { Text=names[i],Location=new Point(xpos[i],149),AutoSize=true }); }
            maxX.Value=maxY.Value=100;
        }
        public void SetDevices(List<TouchDevice> items) { device.Items.Clear();foreach(var d in items)if(d.IsPhysical)device.Items.Add(d); }
        public void SelectDevice(string path) { device.SelectedIndex=-1;for(int i=0;i<device.Items.Count;i++)if(String.Equals(((TouchDevice)device.Items[i]).Path,path,StringComparison.OrdinalIgnoreCase)&&!String.IsNullOrEmpty(path))device.SelectedIndex=i; }
        public RoleProfile ReadRole() { return new RoleProfile { Path=SelectedDevice==null?"":SelectedDevice.Path,Rotation=rotation.SelectedIndex*90,FlipX=flipX.Checked,FlipY=flipY.Checked,MinX=(double)minX.Value/100,MaxX=(double)maxX.Value/100,MinY=(double)minY.Value/100,MaxY=(double)maxY.Value/100 }; }
        public void LoadRole(RoleProfile p)
        {
            SelectDevice(p.Path);rotation.SelectedIndex=p.Rotation>=0&&p.Rotation<=270&&p.Rotation%90==0?p.Rotation/90:0;flipX.Checked=p.FlipX;flipY.Checked=p.FlipY;
            minX.Value=Limit(p.MinX);maxX.Value=Limit(p.MaxX);minY.Value=Limit(p.MinY);maxY.Value=Limit(p.MaxY);
        }
        static decimal Limit(double d) { return Double.IsNaN(d)||Double.IsInfinity(d)?0:(decimal)(Math.Max(0,Math.Min(1,d))*100); }
    }
}

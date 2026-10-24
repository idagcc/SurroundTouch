using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SurroundTouch
{
    internal static class Program
    {
        static readonly object logLock=new object();
        internal static uint ShowMessage;
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
            ShowMessage=Native.RegisterWindowMessage("SurroundTouch.Show.28F2E5B7");
            bool startup=Array.Exists(args,delegate(string arg) { return arg=="--startup"; });
            if(args.Length==2 && args[0]=="--diagnostics")
            {
                try { File.WriteAllText(args[1],Diagnostics(),Encoding.UTF8); return 0; }
                catch(Exception ex) { File.WriteAllText(args[1],ex.ToString()); return 1; }
            }
            bool created;
            using(Mutex mutex=new Mutex(true,"Local\\SurroundTouch.Application",out created))
            {
                if(!created) { if(!startup&&ShowMessage!=0)Native.PostMessage(new IntPtr(0xffff),ShowMessage,IntPtr.Zero,IntPtr.Zero);return 0; }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                MainForm main=null;
                Application.ThreadException+=delegate(object sender,ThreadExceptionEventArgs e)
                {
                    if(main!=null)main.EmergencyStop();
                    MessageBox.Show(e.Exception.Message+"\nTouch correction has stopped.","SurroundTouch",MessageBoxButtons.OK,MessageBoxIcon.Error);
                };
                main=new MainForm(startup); Application.Run(main);
            }
            return 0;
        }
        internal static string Diagnostics()
        {
            StringBuilder b=new StringBuilder();
            b.AppendLine("SurroundTouch diagnostics 1.1.2"); b.AppendLine("UTC: "+DateTime.UtcNow.ToString("O"));
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
            b.AppendLine(RuntimeStats.Report());
            try { b.AppendLine("Start at sign-in registered for this executable: "+StartupRegistration.Enabled); }
            catch(Exception ex) { b.AppendLine("Startup registration unavailable: "+ex.Message); }
            try { b.AppendLine("Resume saved correction: "+AppSettings.Load().ResumeCorrection); }
            catch(Exception ex) { b.AppendLine("Startup settings unavailable: "+ex.Message); }
            b.AppendLine(DriverDiagnostics.Report());
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
        readonly CheckBox launchAtSignIn=new CheckBox(),resume=new CheckBox();
        readonly NotifyIcon tray=new NotifyIcon();
        readonly System.Windows.Forms.Timer restoreTimer=new System.Windows.Forms.Timer { Interval=3000 };
        readonly System.Windows.Forms.Timer noticeTimer=new System.Windows.Forms.Timer { Interval=1000 };
        readonly TouchNoticePolicy noticePolicy=new TouchNoticePolicy();
        readonly Stopwatch noticeClock=Stopwatch.StartNew();
        readonly TouchNotice notice;
        AppSettings appSettings=new AppSettings();
        bool preferencesReady,exiting,restoring,reportBusy;
        DateTime restoreDeadline;
        List<TouchDevice> devices=new List<TouchDevice>();
        Screen[] screens=new Screen[0];
        Profile profile;
        bool busy;
        TestForm testForm;

        public MainForm(bool startupLaunch)
        {
            Text="SurroundTouch"; Font=new Font("Segoe UI",10F); ForeColor=Ink; BackColor=Color.FromArgb(245,248,252);
            ClientSize=new Size(980,950); MinimumSize=new Size(980,600); StartPosition=FormStartPosition.CenterScreen; AutoScaleMode=AutoScaleMode.Dpi;
            var scroll=new Panel { Dock=DockStyle.Fill,AutoScroll=true };Controls.Add(scroll);
            var root=new TableLayoutPanel { Dock=DockStyle.Top,Height=950,Padding=new Padding(24),ColumnCount=1,RowCount=9 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,78)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,94));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,110)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,204));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,52)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,84));root.RowStyles.Add(new RowStyle(SizeType.Absolute,64));
            root.RowStyles.Add(new RowStyle(SizeType.Percent,100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,32)); scroll.Controls.Add(root);
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
            var preferences=new FlowLayoutPanel { Dock=DockStyle.Fill,WrapContents=true };
            var load=new Button();var saveAs=new Button();var drivers=new Button();
            MakeButton(load,"Load profile…",136,Color.White,Ink);MakeButton(saveAs,"Save profile as…",158,Color.White,Ink);MakeButton(drivers,"Touch driver check",172,Color.White,Ink);
            launchAtSignIn.Text="Start at Windows sign-in";launchAtSignIn.Size=new Size(230,32);
            resume.Text="Resume saved correction on launch";resume.Size=new Size(310,32);
            preferences.Controls.AddRange(new Control[]{load,saveAs,drivers});preferences.SetFlowBreak(drivers,true);
            preferences.Controls.AddRange(new Control[]{launchAtSignIn,resume});root.Controls.Add(preferences);
            status.Dock=DockStyle.Fill;status.Font=new Font("Segoe UI",11F,FontStyle.Bold);status.Padding=new Padding(10);status.BackColor=Color.White;root.Controls.Add(status);
            details.Dock=DockStyle.Fill;details.ForeColor=Muted;details.Padding=new Padding(4,8,4,0);root.Controls.Add(details);
            var bottom=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2 };bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,65));bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,35));
            activity.Text="Emergency stop: Ctrl + Alt + F10";activity.Dock=DockStyle.Fill;activity.ForeColor=Muted;bottom.Controls.Add(activity);
            var diagnostic=new LinkLabel { Text="Save diagnostic report",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleRight };bottom.Controls.Add(diagnostic);root.Controls.Add(bottom);
            setup.AddRange(new Control[]{display,layout,split,gap,first,second,refresh,swap,save,load,saveAs,drivers});
            notice=new TouchNotice(ShowWindow);
            engine=new TouchEngine(this);
            engine.Stopped=delegate(string reason,bool needsAttention)
            {
                if(exiting)return;
                bool wasBusy=busy;busy=false;SetRunning(false);Status(reason,needsAttention);
                noticePolicy.Cancel();notice.Dismiss();
                tray.Text=needsAttention?"SurroundTouch — needs attention":"SurroundTouch — correction stopped";
                if(wasBusy&&needsAttention)notice.Present(noticePolicy.Attention(reason,noticeClock.ElapsedMilliseconds),reason);
            };
            engine.Started=delegate { if(busy) { tray.Text="SurroundTouch — correction active";Status("Correction active. Close or minimize this window to keep running in the tray.",false); } };
            engine.ConnectionChanged=delegate(bool waiting,List<TouchDevice> current)
            {
                if(!busy||exiting)return;
                if(waiting)noticePolicy.Waiting(noticeClock.ElapsedMilliseconds);
                else
                {
                    // Keep the visible assignments in sync with replacement Windows
                    // handles, preserving the operator's crop/rotation/flip settings.
                    RoleProfile a=first.ReadRole(),b=second.ReadRole();devices=current;
                    first.SetDevices(current);second.SetDevices(current);first.LoadRole(a);second.LoadRole(b);
                    notice.Present(noticePolicy.Recovered(noticeClock.ElapsedMilliseconds));
                }
                tray.Text=waiting?"SurroundTouch — reconnecting touch":"SurroundTouch — correction active";
                Status(waiting?"Touch connection interrupted. Waiting to reconnect automatically; calibration is retained. Stop cancels recovery.":
                    "Touch reconnected. Correction resumed with your existing calibration. Lift and touch again to begin a new gesture.",false);
            };
            engine.Activity=delegate(int contacts,long frames) { if(busy) { status.Text="Correction active  ·  "+contacts+" contacts  ·  "+frames+" frames";status.ForeColor=Color.FromArgb(0,118,86); } };
            start.Click+=delegate { StartCorrection(); }; stop.Click+=delegate { EmergencyStop(); };
            refresh.Click+=delegate { RefreshDevices(); };save.Click+=delegate { TrySave(); };swap.Click+=delegate { var a=first.ReadRole();first.LoadRole(second.ReadRole());second.LoadRole(a); };
            test.Click+=delegate { OpenTest(); };
            diagnostic.LinkClicked+=delegate { SaveDiagnostics(); };
            load.Click+=delegate { LoadProfile(); };saveAs.Click+=delegate { ExportProfile(); };drivers.Click+=delegate { OpenDriverCheck(); };
            launchAtSignIn.CheckedChanged+=delegate
            {
                if(!preferencesReady)return;
                try { StartupRegistration.SetEnabled(launchAtSignIn.Checked);Status(launchAtSignIn.Checked?"SurroundTouch will start in the tray when you sign in to Windows.":"Startup at sign-in is disabled.",false); }
                catch(Exception ex) { preferencesReady=false;launchAtSignIn.Checked=!launchAtSignIn.Checked;preferencesReady=true;Status(ex.Message,true); }
            };
            resume.CheckedChanged+=delegate
            {
                if(!preferencesReady)return;
                try { appSettings.ResumeCorrection=resume.Checked;appSettings.Save();if(!resume.Checked)CancelRestore();Status(resume.Checked?"The last saved profile will resume on launch after controller and layout checks.":"Automatic correction restore is disabled.",false); }
                catch(Exception ex) { preferencesReady=false;resume.Checked=!resume.Checked;appSettings.ResumeCorrection=resume.Checked;preferencesReady=true;Status(ex.Message,true); }
            };
            tray.Icon=SystemIcons.Application;tray.Text="SurroundTouch";tray.Visible=true;
            var menu=new ContextMenuStrip();menu.Items.Add("Open SurroundTouch",null,delegate { ShowWindow(); });
            menu.Items.Add("Stop correction",null,delegate { EmergencyStop(); });
            menu.Items.Add("Exit",null,delegate { exiting=true;Close(); });tray.ContextMenuStrip=menu;
            tray.DoubleClick+=delegate { ShowWindow(); };
            Resize+=delegate { if(WindowState==FormWindowState.Minimized)Hide(); };
            restoreTimer.Tick+=delegate { TryRestore(); };
            noticeTimer.Tick+=delegate { if(busy&&!exiting)notice.Present(noticePolicy.Poll(noticeClock.ElapsedMilliseconds)); };
            noticeTimer.Start();
            layout.SelectedIndexChanged+=delegate { LayoutChanged(); };display.SelectedIndexChanged+=delegate { LayoutChanged(); };split.ValueChanged+=delegate { preview.Invalidate(); };gap.ValueChanged+=delegate { preview.Invalidate(); };
            Shown+=delegate
            {
                string loadError=null;
                try { profile=Profile.Load(); }catch(Exception ex) { profile=new Profile();loadError="Saved profile could not be loaded: "+ex.Message; }
                RefreshDevices();
                try { appSettings=AppSettings.Load();launchAtSignIn.Checked=StartupRegistration.Enabled;resume.Checked=appSettings.ResumeCorrection; }
                catch(Exception ex) { loadError="Startup settings could not be loaded: "+ex.Message; }
                preferencesReady=true;
                if(startupLaunch)Hide();
                if(loadError!=null) { Status(loadError,true);ShowWindow(); }
                else if(appSettings.ResumeCorrection)
                {
                    if(Native.HasUIAccess()) { restoring=true;restoreDeadline=DateTime.UtcNow.AddSeconds(60);SetRunning(true);restoreTimer.Start();TryRestore(); }
                    else Status("Automatic restore needs the installed UI Access application.",true);
                }
            };
            FormClosing+=delegate(object sender,FormClosingEventArgs e)
            {
                if(e.CloseReason==CloseReason.UserClosing&&!exiting) { e.Cancel=true;Hide();return; }
                exiting=true;noticeTimer.Dispose();notice.Dispose();restoreTimer.Dispose();engine.Dispose();if(testForm!=null)testForm.Close();tray.Visible=false;tray.Dispose();menu.Dispose();
            };
            stop.Enabled=false;
        }
        static void MakeButton(Button b,string text,int width,Color back,Color fore) { b.Text=text;b.Size=new Size(width,38);b.FlatStyle=FlatStyle.Flat;b.FlatAppearance.BorderColor=Color.FromArgb(214,222,233);b.BackColor=back;b.ForeColor=fore;b.Cursor=Cursors.Hand; }
        void Status(string text,bool error) { status.Text=text;status.ForeColor=error?Color.FromArgb(180,45,45):Ink; Program.LogStatus(text); }
        void SetRunning(bool value) { foreach(Control c in setup)c.Enabled=!value;start.Enabled=!value;stop.Enabled=value; }
        void ShowWindow() { Show();WindowState=FormWindowState.Normal;Activate(); }
        void CancelRestore() { bool wasRestoring=restoring;restoring=false;restoreTimer.Stop();if(wasRestoring&&!busy)SetRunning(false); }
        public void EmergencyStop() { CancelRestore();noticePolicy.Cancel();notice.Dismiss();engine.Stop("Stopped. Windows is receiving the original touch input."); }
        bool RefreshDevices()
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
                details.Text=physical+" physical touch controllers detected. "+(access?"Windows UI Access is enabled.":"This portable preview can inspect devices and test local touch; global correction needs installation.")+"\nUse Identify to assign the physical sides. Keep fingers lifted when starting. Close minimizes to the tray; Stop or tray > Exit restores original touch.";
                if(physical==2)details.Text+="\nTwo controllers detected automatically. "+devices.FindAll(d=>d.IsPhysical&&d.SupportsTwoTouches).Count+" advertise two or more contacts each. Use Touch driver check to verify real input.";
                if(devices.Exists(d=>d.IsUpdd))details.Text+="\nUPDD virtual devices detected and excluded. Stop UPDD before identification using Stop-UPDD.cmd in the package.";
                if(profile.Width>0 && screens.Length>0 && (screens[chosen].Bounds.Width!=profile.Width||screens[chosen].Bounds.Height!=profile.Height))details.Text+="\nThe display resolution has changed. Review the layout before starting.";
                return true;
            }
            catch(Exception ex) { Status("Device detection failed: "+ex.Message,true);return false; }
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
                d.Source=r.Pixels;d.HasRect=true;
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
                noticePolicy.Cancel();notice.Dismiss();
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
        void TrySave() { try { SaveProfile();Status("Profile saved. "+(appSettings.ResumeCorrection?"It will resume automatically on the next launch after layout checks.":"Press Start correction to apply it."),false); }catch(Exception ex) { Status(ex.Message,true); } }
        void LoadProfile()
        {
            using(var dialog=new OpenFileDialog { Filter="SurroundTouch profile (*.json)|*.json",Title="Load a touchscreen profile" })
            {
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                try
                {
                    Profile loaded=Profile.Load(dialog.FileName);
                    loaded.Save();profile=loaded;RefreshDevices();
                    Status("Loaded "+Path.GetFileName(dialog.FileName)+". Review assignments, then press Start correction.",false);
                }
                catch(Exception ex) { Status("Could not load profile: "+ex.Message,true); }
            }
        }
        void ExportProfile()
        {
            using(var dialog=new SaveFileDialog { Filter="SurroundTouch profile (*.json)|*.json",FileName="SurroundTouch-profile.json",Title="Save a named touchscreen profile" })
            {
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                try { SaveProfile();profile.Save(dialog.FileName);Status("Profile saved as "+Path.GetFileName(dialog.FileName)+".",false); }
                catch(Exception ex) { Status("Could not save profile: "+ex.Message,true); }
            }
        }
        void TryRestore()
        {
            if(!restoring)return;
            bool detected=RefreshDevices();
            string problem="Waiting for Windows to enumerate the saved display and USB touch controllers.";
            if(detected&&display.SelectedIndex>=0)
            {
                Rectangle bounds=screens[display.SelectedIndex].Bounds;
                TouchDevice a=first.SelectedDevice,b=second.SelectedDevice;
                problem=profile.RestoreProblem(screens[display.SelectedIndex].DeviceName,new PixelRect(bounds.X,bounds.Y,bounds.Width,bounds.Height),a==null?null:a.Identity,b==null?null:b.Identity);
                if(problem==null&&devices.Exists(d=>d.IsUpdd))problem="UPDD virtual touch is present. Stop the other mapper before starting correction.";
                if(problem==null)
                {
                    CancelRestore();StartCorrection();return;
                }
            }
            if(DateTime.UtcNow>=restoreDeadline)
            {
                CancelRestore();Status("Automatic restore paused: "+problem,true);
                tray.Text="SurroundTouch — needs attention";
                notice.Present(noticePolicy.Attention(problem,noticeClock.ElapsedMilliseconds),problem);
            }
            else Status("Automatic restore: "+problem+" Stop cancels automatic restore.",false);
        }
        void OpenDriverCheck()
        {
            if(busy||restoring) { Status("Stop correction before checking the physical touch driver.",true);return; }
            if(testForm!=null&&!testForm.IsDisposed) { testForm.Close(); }
            try
            {
                Rectangle bounds=display.SelectedIndex>=0?screens[display.SelectedIndex].Bounds:Screen.PrimaryScreen.Bounds;
                PixelRect[] regions;
                try { regions=Regions(); }catch { regions=new[]{new PixelRect(bounds.X,bounds.Y,bounds.Width,bounds.Height)}; }
                testForm=new TestForm(bounds,regions,false,true);testForm.Show(this);
                Status("Driver check: hold two fingers on each panel, then one on each together. Close with Esc, then save a diagnostic report.",false);
            }
            catch(Exception ex) { Status(ex.Message,true); }
        }
        void OpenTest()
        {
            if(testForm!=null&&!testForm.IsDisposed) { testForm.Activate();return; }
            try { var regions=Regions();testForm=new TestForm(screens[display.SelectedIndex].Bounds,regions,busy);testForm.Show(this); }catch(Exception ex) { Status(ex.Message,true); }
        }
        async void SaveDiagnostics()
        {
            if(reportBusy)return;
            using(var dialog=new SaveFileDialog { Filter="Text report (*.txt)|*.txt",FileName="SurroundTouch-diagnostics.txt" })
            {
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                string path=dialog.FileName;reportBusy=true;Status("Collecting the driver report in the background…",false);
                try
                {
                    string report=await Task.Run<string>(Program.Diagnostics);
                    await Task.Run(delegate { File.WriteAllText(path,report,Encoding.UTF8); });
                    if(!IsDisposed)Status("Diagnostic report saved.",false);
                }
                catch(Exception ex) { if(!IsDisposed)Status(ex.Message,true); }
                finally { reportBusy=false; }
            }
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e);Native.WTSRegisterSessionNotification(Handle,0); }
        protected override void OnHandleDestroyed(EventArgs e) { Native.WTSUnRegisterSessionNotification(Handle);base.OnHandleDestroyed(e); }
        protected override void WndProc(ref Message m)
        {
            if(Program.ShowMessage!=0&&(uint)m.Msg==Program.ShowMessage) { ShowWindow();m.Result=IntPtr.Zero;return; }
            // Check actual controller handles when PnP changes. Our injection device
            // also produces these broadcasts and must not stop a healthy receiver.
            if(engine!=null && m.Msg==0x0219 && (m.WParam.ToInt64()==0x8004||m.WParam.ToInt64()==7))engine.CheckDevices();
            if(engine!=null&&m.Msg==0x007E&&profile!=null&&Array.Exists(Screen.AllScreens,delegate(Screen screen)
                { Rectangle r=screen.Bounds;return profile.MatchesDisplay(screen.DeviceName,new PixelRect(r.X,r.Y,r.Width,r.Height)); }))
            {
                // Windows may broadcast a display change while re-enumerating
                // hardware even though our saved layout has not changed.
                engine.CheckDevices();base.WndProc(ref m);return;
            }
            long session=m.WParam.ToInt64();
            bool sessionEnded=m.Msg==0x02B1&&(session==2||session==4||session==6||session==7); // disconnect, logoff, lock
            if(engine!=null && (m.Msg==0x007E || sessionEnded || (m.Msg==0x0218&&session==4)))
            {
                CancelRestore();
                if(engine.Running||busy)engine.Stop(m.Msg==0x007E?"Display layout changed. Refresh and verify the layout before restarting.":
                    "Windows session changed. Verify the layout before restarting correction.",true);
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
        public RoleProfile ReadRole()
        {
            RoleProfile role=SelectedDevice==null?new RoleProfile():SelectedDevice.Identity;
            role.Rotation=rotation.SelectedIndex*90;role.FlipX=flipX.Checked;role.FlipY=flipY.Checked;
            role.MinX=(double)minX.Value/100;role.MaxX=(double)maxX.Value/100;role.MinY=(double)minY.Value/100;role.MaxY=(double)maxY.Value/100;
            return role;
        }
        public void LoadRole(RoleProfile p)
        {
            SelectDevice(p.Path);rotation.SelectedIndex=p.Rotation>=0&&p.Rotation<=270&&p.Rotation%90==0?p.Rotation/90:0;flipX.Checked=p.FlipX;flipY.Checked=p.FlipY;
            minX.Value=Limit(p.MinX);maxX.Value=Limit(p.MaxX);minY.Value=Limit(p.MinY);maxY.Value=Limit(p.MaxY);
        }
        static decimal Limit(double d) { return Double.IsNaN(d)||Double.IsInfinity(d)?0:(decimal)(Math.Max(0,Math.Min(1,d))*100); }
    }
}

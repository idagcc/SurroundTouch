using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SurroundTouch
{
    // The receiver can never be a hit-test target. Our injected events therefore
    // reach ordinary applications (including our visible UI) without recursion.
    internal sealed class TouchEngine : IDisposable
    {
        // Windows owns one touch-injection context for the application. Creating
        // another receiver thread must not recreate its virtual digitizer.
        static readonly object injectionLock=new object();
        static bool injectionInitialized;
        readonly Control ui;
        volatile Thread thread;
        volatile Receiver receiver;
        volatile bool stopping;
        volatile string requestedStopReason;
        int generation;
        public volatile bool Running;
        public Action<string> Stopped;
        public Action Started;
        public Action<int,long> Activity;
        public TouchEngine(Control dispatcher) { ui=dispatcher; }
        void Dispatch(Action action) { if(!ui.IsDisposed && ui.IsHandleCreated)try { ui.BeginInvoke(action); } catch(InvalidOperationException) { } }
        static void EnsureInjectionInitialized()
        {
            lock(injectionLock)
            {
                if(injectionInitialized)return;
                if(!Native.InitializeTouchInjection(256,3))throw NativeFailure("InitializeTouchInjection");
                injectionInitialized=true;
            }
        }
        static Win32Exception NativeFailure(string operation)
        {
            int code=Marshal.GetLastWin32Error();
            return new Win32Exception(code,operation+" failed: "+new Win32Exception(code).Message);
        }
        static string Describe(Exception exception)
        {
            Win32Exception native=exception as Win32Exception;
            return exception.Message+(native==null?"":" (Windows error "+native.NativeErrorCode+")");
        }
        public void Start(List<TouchDevice> devices,Dictionary<string,Mapping> mappings,Action<TouchDevice> identify)
        {
            if(Running||thread!=null)throw new InvalidOperationException("Touch receiver is already starting or running.");
            if(!Native.HasUIAccess())throw new InvalidOperationException("Install SurroundTouch first using Install.cmd, then open the installed shortcut. Windows requires signed UI Access for touch correction across applications.");
            stopping=false;
            int currentGeneration=Interlocked.Increment(ref generation);
            requestedStopReason="Stopped";
            thread=new Thread(delegate()
            {
                Receiver current=null;
                string reason="Stopped";
                try
                {
                    using(current=new Receiver(this,devices,mappings,identify))
                    {
                        receiver=current;
                        current.Begin(); Running=true;
                        Dispatch(delegate { if(currentGeneration==generation&&Running&&Started!=null)Started(); });
                        Application.Run();
                    }
                }
                catch(Exception ex) { reason=Describe(ex); }
                finally
                {
                    if(current!=null && current.Reason!=null)reason=current.Reason;
                    TouchDevice identified=current==null?null:current.IdentifiedDevice;
                    Running=false; receiver=null; thread=null;
                    // Notify only after the worker and its global registration are gone.
                    // This also permits the UI to start the next identification step.
                    Dispatch(delegate
                    {
                        if(currentGeneration!=generation)return;
                        if(Stopped!=null)Stopped(reason);
                        if(identified!=null && identify!=null)identify(identified);
                    });
                }
            });
            thread.IsBackground=true; thread.Name="SurroundTouch input"; thread.SetApartmentState(ApartmentState.STA); thread.Start();
        }
        public void Stop(string reason)
        {
            requestedStopReason=reason;
            stopping=true;
            Thread worker=thread;
            Receiver r=receiver;
            if(r!=null && r.Handle!=IntPtr.Zero)PostMessage(r.Handle,0x8001,IntPtr.Zero,IntPtr.Zero);
            if(worker==null && Stopped!=null)Stopped(reason);
        }
        public void CheckDevices()
        {
            // The UI thread receives device broadcasts. Defer the actual check to
            // the receiver thread, which owns its pointer state and native HWND.
            Receiver r=receiver;
            if(r!=null && r.Handle!=IntPtr.Zero)PostMessage(r.Handle,0x8002,IntPtr.Zero,IntPtr.Zero);
        }
        public void Dispose()
        {
            Thread worker=thread;
            Stop("Stopped");
            if(worker!=null&&Thread.CurrentThread!=worker)worker.Join(2000);
        }
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd,int message,IntPtr w,IntPtr l);

        sealed class Receiver : NativeWindow,IDisposable
        {
            readonly TouchEngine owner;
            readonly Dictionary<IntPtr,TouchDevice> devices=new Dictionary<IntPtr,TouchDevice>();
            readonly Dictionary<string,Mapping> mappings;
            readonly Action<TouchDevice> identify;
            readonly TouchPipeline pipeline;
            readonly TouchFrameBatch batch;
            readonly List<TouchSample> samples=new List<TouchSample>(16);
            Native.TOUCH[] frameBuffer=new Native.TOUCH[256],historyBuffer=new Native.TOUCH[0];
            readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
            readonly HashSet<uint> seen=new HashSet<uint>();
            readonly Queue<uint> history=new Queue<uint>();
            readonly Dictionary<uint,string> contactDevices=new Dictionary<uint,string>();
            bool registered,hotkey;
            volatile bool done;
            long frames;
            long lastInjection;
            int activityPending,lastContactCount=-1;
            long lastFrameCount=-1;
            DateTime start=DateTime.UtcNow,lastActivity=DateTime.MinValue,lastDeviceCheck=DateTime.UtcNow;
            public string Reason;
            public TouchDevice IdentifiedDevice;
            public Receiver(TouchEngine owner,List<TouchDevice> source,Dictionary<string,Mapping> mappings,Action<TouchDevice> identify)
            {
                this.owner=owner; this.mappings=mappings; this.identify=identify;
                foreach(TouchDevice d in source)devices[d.Handle]=d;
                pipeline=new TouchPipeline(delegate(InjectedContact[] contacts) { Native.Inject(contacts);lastInjection=Stopwatch.GetTimestamp(); });
                batch=new TouchFrameBatch(pipeline.Process);
                CreateHandle(new CreateParams { Caption="SurroundTouch input receiver",Parent=new IntPtr(-3) });
            }
            public void Begin()
            {
                if(owner.stopping)throw new OperationCanceledException("Start cancelled.");
                // Identification only reads physical input; it never needs a
                // synthetic device or an injection context.
                if(identify==null)EnsureInjectionInitialized();
                hotkey=Native.RegisterHotKey(Handle,1,0x4000|2|1,0x79); // Ctrl+Alt+F10; F12 is reserved by Windows.
                if(!hotkey)throw new Win32Exception(Marshal.GetLastWin32Error(),"Emergency shortcut Ctrl+Alt+F10 is in use. Close the conflicting app and try again");
                registered=Native.RegisterPointerInputTarget(Handle,2);
                if(!registered)throw new Win32Exception(Marshal.GetLastWin32Error(),"Windows could not redirect touch. Check UI Access installation and close other global touch remappers");
                timer.Interval=identify==null?1000:200;
                timer.Tick+=delegate
                {
                    try
                    {
                        if(owner.stopping) { End(owner.requestedStopReason); return; }
                        if(identify!=null && (DateTime.UtcNow-start).TotalSeconds>20) { End("Identification timed out. Tap the requested physical screen within 20 seconds."); return; }
                        if((DateTime.UtcNow-lastDeviceCheck).TotalSeconds>=5)
                        {
                            lastDeviceCheck=DateTime.UtcNow;
                            ValidateDevices();
                        }
                        if(identify==null&&pipeline.ActiveCount>0&&Stopwatch.GetTimestamp()-lastInjection>=Stopwatch.Frequency*25/1000)pipeline.Tick();
                        PublishActivity();
                    }
                    catch(Exception ex) { End("Correction stopped: "+Describe(ex)); }
                };
                timer.Start();
            }
            protected override void WndProc(ref Message m)
            {
                if(m.Msg==0x8001 || m.Msg==0x0312) { End(m.Msg==0x0312?"Stopped with Ctrl+Alt+F10":owner.requestedStopReason); m.Result=IntPtr.Zero; return; }
                if(m.Msg==0x8002)
                {
                    if(!done)try { ValidateDevices(); } catch(Exception ex) { End("Correction stopped: "+Describe(ex)); }
                    m.Result=IntPtr.Zero; return;
                }
                if(m.Msg==0x024C)
                {
                    if(!done)try { CancelCapture((uint)(m.WParam.ToInt64()&0xffff)); } catch(Exception ex) { End("Correction stopped: "+Describe(ex)); }
                    m.Result=IntPtr.Zero; return;
                }
                if(m.Msg==0x0245 || m.Msg==0x0246 || m.Msg==0x0247 || m.Msg==0x0241 || m.Msg==0x0242 || m.Msg==0x0243)
                {
                    if(!done)try { ReadFrames((uint)(m.WParam.ToInt64()&0xffff)); } catch(Exception ex) { End("Correction stopped: "+Describe(ex)); }
                    m.Result=IntPtr.Zero; return;
                }
                if(m.Msg>=0x0249 && m.Msg<=0x0253) { m.Result=IntPtr.Zero; return; }
                base.WndProc(ref m);
            }
            void ReadFrames(uint pointer)
            {
                long began=Stopwatch.GetTimestamp();
                try { ReadFramesCore(pointer); }
                finally { RuntimeStats.ReadCompleted(Stopwatch.GetTimestamp()-began); }
            }
            void PublishActivity()
            {
                int n=pipeline.ActiveCount;long f=frames;
                if(identify!=null||(n==lastContactCount&&f==lastFrameCount)||(n==lastContactCount&&(DateTime.UtcNow-lastActivity).TotalMilliseconds<250))return;
                if(Interlocked.CompareExchange(ref activityPending,1,0)!=0)return;
                lastActivity=DateTime.UtcNow;lastContactCount=n;lastFrameCount=f;
                owner.Dispatch(delegate
                {
                    Interlocked.Exchange(ref activityPending,0);
                    if(!done&&owner.Activity!=null)owner.Activity(n,f);
                });
            }
            void ReadFramesCore(uint pointer)
            {
                Native.TOUCH initial;
                if(!Native.GetPointerTouchInfo(pointer,out initial))throw NativeFailure("GetPointerTouchInfo");
                if(identify!=null)
                {
                    if((initial.pointerInfo.pointerFlags&Native.DOWN)!=0)
                    {
                        TouchDevice device;
                        if(!devices.TryGetValue(initial.pointerInfo.sourceDevice,out device))throw new InvalidOperationException("Unknown touch controller. Refresh devices and try again.");
                        if(device.IsUpdd)throw new InvalidOperationException("UPDD is still supplying virtual touch. Stop its service using Stop-UPDD.cmd, then Refresh. If physical touch stays unavailable, use the vendor uninstaller and restart Windows.");
                        if(device.IsInjectionDevice)throw new InvalidOperationException("That input came from a virtual touch device. Tap the physical touchscreen to identify its controller.");
                        IdentifiedDevice=device;
                        End("Controller identified. Lift your finger.");
                    }
                    return;
                }
                TouchDevice origin;
                if(devices.TryGetValue(initial.pointerInfo.sourceDevice,out origin)&&(origin.IsUpdd||origin.IsInjectionDevice))return;
                if(seen.Contains(initial.pointerInfo.frameId)) { Native.SkipPointerFrameMessages(pointer);return; }
                uint entries=1,count=(uint)frameBuffer.Length;
                Native.TOUCH[] all=frameBuffer;
                if(initial.pointerInfo.historyCount>1&&(initial.pointerInfo.pointerFlags&Native.UPDATE)!=0)
                {
                    entries=0;count=0;
                    bool sized=Native.GetPointerFrameTouchInfoHistory(pointer,ref entries,ref count,null);
                    if(!sized&&(entries==0||count==0))throw NativeFailure("GetPointerFrameTouchInfoHistory size query");
                    if(count==0||entries==0||count>256||entries>4096||entries*count>65536)throw new InvalidOperationException("Unsupported touch frame size.");
                    int required=(int)(entries*count);
                    if(historyBuffer.Length<required)historyBuffer=new Native.TOUCH[required];
                    all=historyBuffer;
                    if(!Native.GetPointerFrameTouchInfoHistory(pointer,ref entries,ref count,all))throw NativeFailure("GetPointerFrameTouchInfoHistory");
                }
                else if(!Native.GetPointerFrameTouchInfo(pointer,ref count,all))throw NativeFailure("GetPointerFrameTouchInfo");
                if(count==0||count>256||entries==0||entries>4096||(long)entries*count>all.Length)throw new InvalidOperationException("Touch history changed unexpectedly.");
                int mergedBefore=batch.MergedFrames;
                // Windows returns newest first. Feed the state machine oldest first.
                for(int row=(int)entries-1;row>=0;row--)
                {
                    if(owner.stopping) { End(owner.requestedStopReason);return; }
                    uint id=all[row*(int)count].pointerInfo.frameId;
                    if(seen.Contains(id))continue;
                    samples.Clear();
                    for(int col=0;col<count;col++)
                    {
                        Native.TOUCH t=all[row*(int)count+col]; Native.POINTER_INFO p=t.pointerInfo;
                        if(p.pointerType!=2)continue;
                        TouchDevice device;
                        if(!devices.TryGetValue(p.sourceDevice,out device))
                        {
                            string path=Native.DevicePath(p.sourceDevice);
                            if(path.IndexOf("VIRTUAL_DIGITIZER",StringComparison.OrdinalIgnoreCase)>=0)continue;
                            throw new InvalidOperationException("A touch controller changed. Press Refresh and reassign it.");
                        }
                        // Independent mode must not replay UPDD's second copy of a touch.
                        if(device.IsUpdd||device.IsInjectionDevice)continue;
                        uint flags=p.pointerFlags;
                        TouchPhase phase;
                        if((flags&Native.CANCELED)!=0)phase=TouchPhase.Cancel;
                        else if((flags&Native.UP)!=0)phase=TouchPhase.Up;
                        else if((flags&Native.DOWN)!=0)phase=TouchPhase.Down;
                        else if((flags&Native.INCONTACT)!=0)phase=TouchPhase.Move;
                        else continue;
                        PixelPoint point=new PixelPoint(p.ptPixelLocationRaw.X,p.ptPixelLocationRaw.Y);
                        Mapping map;
                        if(mappings.TryGetValue(device.Path,out map))point=map.Map(point);
                        samples.Add(new TouchSample { Device=String.IsNullOrEmpty(device.Path)?"session:"+device.Handle.ToInt64():device.Path,Id=p.pointerId,Position=point,Phase=phase,Pressure=(t.touchMask&4)!=0?t.pressure:512 });
                    }
                    batch.Add(samples); frames++;Interlocked.Increment(ref RuntimeStats.InputFrames);
                    foreach(TouchSample sample in samples)
                    {
                        if(sample.Phase==TouchPhase.Down)contactDevices[sample.Id]=sample.Device;
                        else if(sample.Phase==TouchPhase.Up || sample.Phase==TouchPhase.Cancel)contactDevices.Remove(sample.Id);
                    }
                    seen.Add(id); history.Enqueue(id); if(history.Count>4096)seen.Remove(history.Dequeue());
                }
                batch.Flush();
                Interlocked.Add(ref RuntimeStats.CoalescedMoves,batch.MergedFrames-mergedBefore);
                timer.Interval=pipeline.ActiveCount>0?30:1000;
                PublishActivity();
                Native.SkipPointerFrameMessages(pointer);
            }
            void CancelCapture(uint pointer)
            {
                // Capture-change messages carry no fresh POINTER_INFO. A normal UP
                // may already have removed this pointer, so only cancel tracked ones.
                string device;
                if(!contactDevices.TryGetValue(pointer,out device))return;
                pipeline.Process(new[] { new TouchSample { Device=device,Id=pointer,Phase=TouchPhase.Cancel } });
                contactDevices.Remove(pointer);
                timer.Interval=pipeline.ActiveCount>0?30:1000;
            }
            void ValidateDevices()
            {
                foreach(TouchDevice device in devices.Values)
                {
                    if(device.IsUpdd || device.IsInjectionDevice || !device.HasRect)continue;
                    Native.RECT physical,display;
                    if(!Native.GetPointerDeviceRects(device.Handle,out physical,out display))
                        throw new Win32Exception(Marshal.GetLastWin32Error(),"A physical touch controller disconnected or could not be queried. Reconnect it, then press Refresh.");
                    PixelRect now=display.Pixels,old=device.Source;
                    if(now.X!=old.X || now.Y!=old.Y || now.Width!=old.Width || now.Height!=old.Height)
                        throw new InvalidOperationException("Windows changed a controller's display mapping. Press Refresh before restarting.");
                }
            }
            void End(string reason)
            {
                if(done)return; done=true; timer.Stop();
                // Return original physical input first, even if cancellation fails.
                if(registered) { Native.UnregisterPointerInputTarget(Handle,2); registered=false; }
                try { pipeline.CancelAll(); } catch(Exception ex) { reason+=" (contact cleanup: "+Describe(ex)+")"; }
                Reason=reason;
                owner.Running=false;
                Application.ExitThread();
            }
            public void Dispose()
            {
                if(registered)Native.UnregisterPointerInputTarget(Handle,2);
                try { pipeline.CancelAll(); } catch { }
                if(hotkey)Native.UnregisterHotKey(Handle,1);
                timer.Dispose(); DestroyHandle();
            }
        }
    }
}

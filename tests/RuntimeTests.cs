using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace SurroundTouch
{
    internal static class RuntimeTests
    {
        static int assertions;
        static void Assert(bool value,string message) { assertions++;if(!value)throw new Exception(message); }
        static TouchSample Sample(string device,uint id,TouchPhase phase,int x)
        { return new TouchSample { Device=device,Id=id,Phase=phase,Position=new PixelPoint(x,10) }; }
        static List<TouchSample> Frame(params TouchSample[] samples) { return new List<TouchSample>(samples); }
        static int Main()
        {
            string directory=Path.Combine(Path.GetTempPath(),"SurroundTouch-runtime-tests-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                Coalescing();LifecycleBoundaries();FailedFlush();ProfileRoundtrip(directory);ProfileRestore();Recovery();RecoveryFailures();Notices();Observation();Interop();
                Console.WriteLine("PASS: "+assertions+" assertions; quiet notices, touch recovery, history coalescing, profile persistence/restore, observations and interop layouts.");return 0;
            }
            catch(Exception ex) { Console.Error.WriteLine("FAIL after "+assertions+" assertions: "+ex);return 1; }
            finally { Directory.Delete(directory,true); }
        }
        static void Coalescing()
        {
            var output=new List<InjectedContact[]>();var pipeline=new TouchPipeline(output.Add);var batch=new TouchFrameBatch(pipeline.Process);
            batch.Add(Frame(Sample("left",7,TouchPhase.Down,0),Sample("right",7,TouchPhase.Down,100)));
            for(int x=1;x<=2000;x++)batch.Add(Frame(Sample("left",7,TouchPhase.Move,x)));
            Assert(output.Count==1,"Movement waits only until this history read is flushed");batch.Flush();
            Assert(output.Count==2&&batch.MergedFrames==1999,"A backlog of 2,000 movement frames emits one fresh frame");
            Assert(output[1][0].Position.X==2000&&output[1][1].Position.X==100,"Newest motion and stationary other controller survive");
            Assert(pipeline.ActiveCount==2,"Same input ID on different controllers stays independent");
            batch.Add(Frame(Sample("right",7,TouchPhase.Move,2500)));batch.Add(Frame(Sample("LEFT",7,TouchPhase.Move,2100)));batch.Flush();
            Assert(output[2].Length==2&&output[2][0].Position.X==2100&&output[2][1].Position.X==2500,"Cross-controller moves are merged without dropping either source");
            batch.Add(Frame(Sample("left",7,TouchPhase.Up,2100),Sample("right",7,TouchPhase.Cancel,0)));batch.Flush();
            Assert(pipeline.ActiveCount==0&&output.Count==4,"Both releases are delivered");
            Assert(output[3][0].Phase==TouchPhase.Up&&output[3][1].Phase==TouchPhase.Cancel,"Release and cancel flags survive merging");
            pipeline.Tick();Assert(output.Count==4,"No idle injection after all fingers lift");
        }
        static void LifecycleBoundaries()
        {
            var frames=new List<InjectedContact[]>();var p=new TouchPipeline(frames.Add);var b=new TouchFrameBatch(p.Process);
            b.Add(Frame(Sample("a",1,TouchPhase.Down,0)));b.Add(Frame(Sample("a",1,TouchPhase.Move,5)));
            b.Add(Frame(Sample("b",1,TouchPhase.Down,100)));b.Add(Frame(Sample("a",1,TouchPhase.Move,10)));
            b.Add(Frame(Sample("a",1,TouchPhase.Up,20)));b.Add(Frame(Sample("a",1,TouchPhase.Down,30)));
            b.Add(Frame(Sample("b",1,TouchPhase.Cancel,0)));b.Flush();
            Assert(frames.Count==8,"No lifecycle boundary is coalesced away, including moved-UP prelude");
            Assert(frames[1][0].Position.X==5&&frames[2][1].Phase==TouchPhase.Down,"Move before another controller's down remains ordered");
            Assert(frames[4][0].Position.X==20&&frames[5][0].Phase==TouchPhase.Up,"UP is preceded by its final position");
            Assert(frames[6][0].Phase==TouchPhase.Down,"A reused pointer starts a new gesture");
            Assert(p.ActiveCount==1,"Only the surviving gesture remains");p.CancelAll();
            p.Process(Frame(Sample("a",1,TouchPhase.Down,9)));p.Tick();p.CancelPointer(1);p.Tick();
            Assert(p.ActiveCount==0,"Cached stationary frames do not resurrect canceled input");
        }
        static void FailedFlush()
        {
            bool fail=false;var frames=new List<InjectedContact[]>();var p=new TouchPipeline(delegate(InjectedContact[] f) { if(fail)throw new IOException("busy");frames.Add(f); });
            var batch=new TouchFrameBatch(p.Process);batch.Add(Frame(Sample("a",1,TouchPhase.Down,1)));
            batch.Add(Frame(Sample("a",1,TouchPhase.Move,2)));fail=true;
            try { batch.Flush();throw new Exception("Expected failure"); }catch(IOException) { assertions++; }
            fail=false;p.Tick();Assert(frames[1][0].Position.X==1,"A failed batch cannot advance committed position");
            batch.Flush();p.Tick();Assert(frames[3][0].Position.X==2,"Pending movement may be retried after failure");
        }
        static Profile SavedProfile()
        {
            return new Profile { Display="DISPLAY1",Width=2160,Height=1920,X=-2160,Y=5,
                First=new RoleProfile { Path="left",SourceX=-2160,SourceY=5,SourceWidth=2160,SourceHeight=1920 },
                Second=new RoleProfile { Path="right",SourceX=-2160,SourceY=5,SourceWidth=2160,SourceHeight=1920 } };
        }
        static void ProfileRoundtrip(string directory)
        {
            string path=Path.Combine(directory,"named.json");Profile p=SavedProfile();p.Save(path);
            Profile loaded=Profile.Load(path);Assert(loaded.First.Path=="left"&&loaded.First.SourceWidth==2160,"Named profile includes stable identity and source layout");
            p.SplitPercent=60;p.Save(path);Assert(Profile.Load(path).SplitPercent==60,"Atomic overwrite loads latest profile");
            p.First.MinX=Double.NaN;
            try { p.Save(path);throw new Exception("Invalid calibration accepted"); }catch(InvalidDataException) { assertions++; }
            Assert(Profile.Load(path).SplitPercent==60,"Rejected save retains previous profile");
            string invalid=Path.Combine(directory,"bad.json");File.WriteAllText(invalid,"{\"Version\":2}");
            try { Profile.Load(invalid);throw new Exception("Unknown version accepted"); }catch(InvalidDataException) { assertions++; }
            File.WriteAllText(invalid,new string(' ',65537));
            try { Profile.Load(invalid);throw new Exception("Oversized import accepted"); }catch(InvalidDataException) { assertions++; }
            Assert(Directory.GetFiles(directory,"*.tmp").Length==0,"Atomic save leaves no temporary files");
        }
        static void ProfileRestore()
        {
            Profile p=SavedProfile();var bounds=new PixelRect(-2160,5,2160,1920);
            Assert(p.RestoreProblem("display1",bounds,p.First,p.Second)==null,"Unchanged hardware and display can resume");
            Assert(p.RestoreProblem("other",bounds,p.First,p.Second)!=null,"Changed display identity blocks resume");
            Assert(p.RestoreProblem("display1",new PixelRect(0,5,2160,1920),p.First,p.Second)!=null,"Changed desktop origin blocks resume");
            Assert(p.RestoreProblem("display1",new PixelRect(-2160,5,1920,2160),p.First,p.Second)!=null,"Changed resolution blocks resume");
            Assert(p.RestoreProblem("display1",bounds,null,p.Second)!=null,"Missing controller waits");
            Assert(p.RestoreProblem("display1",bounds,p.Second,p.First)!=null,"Swapped controller identities cannot silently resume");
            var changed=new RoleProfile { Path="left",SourceWidth=1080,SourceHeight=1920 };
            Assert(p.RestoreProblem("display1",bounds,changed,p.Second)!=null,"Changed Windows source mapping blocks resume");
            p.First.SourceWidth=0;Assert(p.RestoreProblem("display1",bounds,p.First,p.Second)!=null,"Old profiles need one manual confirmation");
            p=SavedProfile();p.Second.Path="LEFT";Assert(p.RestoreProblem("display1",bounds,p.First,p.Second)!=null,"Duplicate device assignments cannot auto-resume");
        }
        static TouchDevice Device(string path,int handle)
        {
            return new TouchDevice { Path=path,Handle=new IntPtr(handle),HasRect=true,Source=new PixelRect(-2160,5,2160,1920),Contacts=10 };
        }
        static Dictionary<string,Mapping> RecoveryMappings()
        {
            return new Dictionary<string,Mapping>(StringComparer.OrdinalIgnoreCase)
            {
                { "left",new Mapping { Source=new PixelRect(-2160,5,2160,1920) } },
                { "right",new Mapping { Source=new PixelRect(-2160,5,2160,1920) } }
            };
        }
        static void Recovery()
        {
            var source=new List<TouchDevice> { Device("left",1),Device("right",2) };
            var output=new List<InjectedContact[]>();var p=new TouchPipeline(output.Add);var b=new TouchFrameBatch(p.Process);
            int resets=0;var transitions=new List<bool>();
            var recovery=new TouchRecovery(source,RecoveryMappings(),delegate { b.Discard();p.CancelAll();resets++; },transitions.Add);
            Assert(recovery.AcceptInput(0,0,false),"Ordinary input is unchanged before any interruption");
            p.Process(Frame(Sample("left",7,TouchPhase.Down,10),Sample("right",7,TouchPhase.Down,100)));
            b.Add(Frame(Sample("left",7,TouchPhase.Move,999)));
            var missing=new List<TouchDevice> { Device("right",2) };
            recovery.Check(delegate { return missing; });
            Assert(recovery.Waiting&&resets==1&&p.ActiveCount==0,"Disconnect cancels both contacts and enters recovery");
            Assert(transitions.Count==1&&transitions[0],"Waiting is announced once");
            Assert(output[1].Length==2&&output[1][0].Phase==TouchPhase.Cancel&&output[1][1].Phase==TouchPhase.Cancel,"Dropout sends a complete cancellation frame");
            Assert(output[1][0].Position.X==10,"Pending movement is discarded before cancellation");
            b.Flush();p.Tick();Assert(output.Count==2,"No pending or keepalive frame resurrects an old gesture");
            Assert(!recovery.AcceptInput(UInt64.MaxValue,0,true),"Even new input is suppressed while a required controller is missing");
            for(int i=0;i<70;i++)recovery.Check(delegate { return missing; });
            Assert(recovery.Waiting&&resets==1&&transitions.Count==1,"Recovery waits indefinitely without repeating cleanup or notifications");
            var partial=new List<TouchDevice> { Device("left",3),Device("right",4) };partial[0].HasRect=false;
            recovery.Check(delegate { return partial; });Assert(recovery.Waiting,"Partially enumerated controllers cannot resume");
            var different=new List<TouchDevice> { Device("other-left",3),Device("right",4) };
            recovery.Check(delegate { return different; });Assert(recovery.Waiting,"Different physical identity cannot inherit calibration");
            var returned=new List<TouchDevice> { Device("RIGHT",4),Device("LEFT",3),Device("HID#VIRTUAL_DIGITIZER",9) };
            recovery.Check(delegate { return returned; });
            Assert(!recovery.Waiting&&resets==1&&transitions.Count==2&&!transitions[1],"Same identities resume despite reordered enumeration, case and new handles");
            Assert(recovery.Devices[new IntPtr(3)].Path=="LEFT"&&!recovery.Devices.ContainsKey(new IntPtr(1)),"Windows handles are rebound atomically");
            Assert(!recovery.AcceptInput((ulong)recovery.ResumedAt,0,true),"Queued event at recovery boundary is rejected");
            Assert(!recovery.AcceptInput((ulong)recovery.ResumedAt-1,0,true),"Historical DOWN from an interrupted gesture is rejected");
            Assert(recovery.AcceptInput((ulong)recovery.ResumedAt+1,0,false),"Fresh high-resolution input is accepted");
            Assert(!recovery.AcceptInput(0,0,false)&&recovery.AcceptInput(0,0,true),"Drivers without timestamps cannot replay history after recovery");
            Assert(!TouchRecovery.AfterBoundary(99,100,101)&&!TouchRecovery.AfterBoundary(100,100,101),"Tick timestamps reject pre-recovery events");
            Assert(TouchRecovery.AfterBoundary(101,100,102),"Fresh tick timestamps are accepted");
            Assert(TouchRecovery.AfterBoundary(3,UInt32.MaxValue-2,4),"Windows tick wraparound accepts fresh events");
            Assert(!TouchRecovery.AfterBoundary(UInt32.MaxValue-3,UInt32.MaxValue-2,4),"Windows tick wraparound still rejects old events");
            Assert(TouchRecovery.AfterBoundary(70001,100,70002),"A long-running receiver does not retain a stale tick cutoff");
            p.Process(Frame(Sample("left",7,TouchPhase.Move,50),Sample("right",7,TouchPhase.Up,100)));
            Assert(p.ActiveCount==0&&output.Count==2,"Returning mid-gesture moves and releases are ignored");
            p.Process(Frame(Sample("left",7,TouchPhase.Down,50),Sample("right",7,TouchPhase.Down,100)));
            Assert(p.ActiveCount==2&&output[2][0].Phase==TouchPhase.Down,"Fresh simultaneous touch works with reused pointer IDs");
            recovery.Check(delegate { return returned; });Assert(resets==1&&p.ActiveCount==2,"Healthy check preserves held contacts");
            p.Process(Frame(Sample("left",7,TouchPhase.Up,50),Sample("left",8,TouchPhase.Down,50)));
            p.CancelPointer(8);recovery.DiscardOldInput();recovery.Check(delegate { return returned; });
            Assert(!recovery.Waiting&&p.ActiveCount==1&&resets==1,"Expired pointer read leaves a healthy panel's gesture running");
            Assert(!recovery.AcceptInput((ulong)recovery.ResumedAt-1,0,true),"Expired reads also discard historical DOWNs");
            p.Process(Frame(Sample("left",8,TouchPhase.Move,90),Sample("right",7,TouchPhase.Move,120)));
            Assert(p.ActiveCount==1&&output[output.Count-1][0].Position.X==120,"Healthy panel continues while expired contact waits for a fresh DOWN");
            returned.RemoveAt(2);recovery.Check(delegate { return returned; });Assert(resets==1,"Synthetic-device removal does not interrupt touch");
            recovery.Pause();recovery.Pause();Assert(resets==2&&p.ActiveCount==0,"Repeated read failures cancel once per interruption");
            recovery.Check(delegate { return returned; });Assert(!recovery.Waiting&&transitions.Count==4,"A second interruption recovers independently");
            var rebound=new List<TouchDevice> { Device("left",11),Device("right",12) };
            recovery.Check(delegate { return rebound; });
            Assert(!recovery.Waiting&&resets==3&&recovery.Devices.ContainsKey(new IntPtr(11)),"Disconnect/reconnect between polls still resets old contacts and replaces handles");
        }
        static void RecoveryFailures()
        {
            var source=new List<TouchDevice> { Device("left",1),Device("right",2) };
            int resets=0;var r=new TouchRecovery(source,RecoveryMappings(),delegate { resets++; },delegate { });
            r.Check(delegate { throw new Win32Exception(6); });Assert(r.Waiting&&resets==1,"Native enumeration failure is recoverable");
            r.Check(delegate { throw new InvalidOperationException("PnP race"); });Assert(resets==1,"Enumeration races stay paused");
            r.Check(delegate { return source; });Assert(!r.Waiting,"A temporary query failure recovers with unchanged handles");
            var changed=new List<TouchDevice> { Device("left",3),Device("right",4) };changed[0].Source=new PixelRect(0,0,1080,1920);
            try { r.Check(delegate { return changed; });throw new Exception("Changed mapping accepted"); }
            catch(InvalidOperationException ex) { Assert(ex.Message.Contains("display mapping"),"Source geometry change requires review"); }
            Assert(r.Devices.ContainsKey(new IntPtr(1)),"Rejected geometry cannot replace the last verified handle snapshot");
            var duplicate=new List<TouchDevice> { Device("left",3),Device("LEFT",4),Device("right",5) };
            try { r.Check(delegate { return duplicate; });throw new Exception("Ambiguous identity accepted"); }
            catch(InvalidOperationException ex) { Assert(ex.Message.Contains("ambiguous"),"Duplicate identities cannot choose an arbitrary panel"); }
            duplicate=new List<TouchDevice> { Device("left",3),Device("right",3) };
            try { r.Check(delegate { return duplicate; });throw new Exception("Duplicate handle accepted"); }
            catch(InvalidOperationException ex) { Assert(ex.Message.Contains("handles"),"Duplicate handles cannot overwrite an assignment"); }
            var failed=new TouchRecovery(source,RecoveryMappings(),delegate { throw new IOException("injection failed"); },delegate { });
            try { failed.Pause();throw new Exception("Cleanup failure swallowed"); }
            catch(IOException) { Assert(!failed.Waiting,"Failed injection cleanup propagates to stop the engine"); }
        }
        static void Notices()
        {
            var n=new TouchNoticePolicy();
            Assert(n.Poll(10000)==TouchNoticeKind.None,"Healthy operation has no popups");
            n.Waiting(10000);Assert(n.Poll(11000)==TouchNoticeKind.None,"Brief loss is silent");
            Assert(n.Recovered(11500)==TouchNoticeKind.None,"Brief automatic reconnection is silent");
            Assert(n.Poll(30000)==TouchNoticeKind.None,"Recovery cancels a delayed waiting notice");
            n.Waiting(30000);n.Waiting(31000);
            Assert(n.Poll(37999)==TouchNoticeKind.None,"Waiting notice has an eight-second delay");
            Assert(n.Poll(38000)==TouchNoticeKind.Waiting,"A longer outage produces one waiting notice");
            Assert(n.Poll(100000)==TouchNoticeKind.None,"A prolonged outage never repeats its notice");
            Assert(n.Recovered(100100)==TouchNoticeKind.Recovered,"Shown outage notice is followed by a recovery confirmation");
            Assert(n.Recovered(100200)==TouchNoticeKind.None,"Repeated connected broadcasts do not repeat notices");
            n.Waiting(101000);Assert(n.Recovered(105000)==TouchNoticeKind.None,"Repeated flapping respects the one-minute cooldown");
            n.Waiting(106000);Assert(n.Poll(114000)==TouchNoticeKind.None,"Cooldown also suppresses repeated waiting popups");
            Assert(n.Poll(160100)==TouchNoticeKind.Waiting,"A persistent later outage can notify after cooldown");
            Assert(n.Recovered(160200)==TouchNoticeKind.Recovered,"Recovery confirmation is allowed for the outage already announced");
            n.Waiting(230000);Assert(n.Recovered(234000)==TouchNoticeKind.Recovered,"A noticeable dropout can show a single recovered summary");
            n.Waiting(235000);n.Cancel();
            Assert(n.Poll(250000)==TouchNoticeKind.None&&n.Recovered(250001)==TouchNoticeKind.None,"Stop cancels pending waiting and recovery notices");
            Assert(n.Attention("mapping changed",250100)==TouchNoticeKind.Attention,"A major change is reported even during routine cooldown");
            Assert(n.Attention("mapping changed",250200)==TouchNoticeKind.None,"Duplicate attention messages are suppressed");
            Assert(n.Attention("mapping changed",310100)==TouchNoticeKind.Attention,"A later independent incident may notify again");
            n.Waiting(320000);Assert(n.Attention("session changed",321000)==TouchNoticeKind.Attention,"Stopping for review cancels automatic recovery notices");
            Assert(n.Poll(400000)==TouchNoticeKind.None&&n.Recovered(400001)==TouchNoticeKind.None,"No stale recovered popup follows a major stop");
            Assert(TouchNoticePolicy.AttentionMessage("Windows session changed").Contains("session"),"Session interruptions have a plain-language explanation");
            Assert(TouchNoticePolicy.AttentionMessage("Windows changed display mapping").Contains("layout or mapping"),"Layout changes explain why review is needed");
            Assert(TouchNoticePolicy.AttentionMessage("ambiguous controller identity").Contains("assignments"),"Assignment problems have a useful explanation");
            Assert(TouchNoticePolicy.AttentionMessage("Windows error 21").Contains("next step"),"Other errors point to detailed guidance without technical popup text");
        }
        static void Observation()
        {
            var o=new TouchObservation();o.Record(1,"left",TouchPhase.Move);Assert(o.PeakContacts==0,"Mid-gesture move does not prove a down");
            o.Record(1,"left",TouchPhase.Down);o.Record(1,"left",TouchPhase.Down);Assert(o.PeakContacts==1,"Duplicate down does not invent a finger");
            o.Record(2,"left",TouchPhase.Down);Assert(o.PeakContacts==2&&o.PeakControllers==1,"Two fingers on one panel proves contacts, not two panels");
            o.Record(3,"right",TouchPhase.Down);Assert(o.PeakContacts==3&&o.PeakControllers==2,"Two source controllers observed concurrently");
            o.Record(1,"left",TouchPhase.Up);o.Record(2,"left",TouchPhase.Cancel);o.ClearActive();
            o.Record(4,"new",TouchPhase.Down);Assert(o.PeakContacts==3,"Ended touches do not contribute to later peaks");
            Assert(o.Report(true).Contains("does not verify physical"),"Injected input cannot be reported as hardware proof");
            Assert(o.Report(false).Contains("left: peak 2"),"Per-controller observation is retained in report");
            var serial=new TouchObservation();serial.Record(1,"left",TouchPhase.Down);
            serial.RecordFrame(Frame(Sample("right",2,TouchPhase.Down,0),Sample("left",1,TouchPhase.Up,0)));
            Assert(serial.PeakContacts==1&&serial.PeakControllers==1,"A release and new down in one frame cannot falsely prove concurrent sources");
            serial.RecordFrame(Frame(Sample("right",3,TouchPhase.Down,0),Sample("right",2,TouchPhase.Move,0)));
            Assert(serial.PeakContacts==2&&serial.PeakControllers==1,"Frame-based observation recognizes real two-finger overlap");
        }
        static void Interop()
        {
            Assert(IntPtr.Size==8,"Test process is 64-bit");
            Assert(Marshal.SizeOf(typeof(Native.POINTER_INFO))==96,"POINTER_INFO x64 layout");
            Assert(Marshal.SizeOf(typeof(Native.TOUCH))==144,"POINTER_TOUCH_INFO x64 layout");
            Assert(Marshal.SizeOf(typeof(Native.DEVICE))==1080,"POINTER_DEVICE_INFO x64 layout");
            var d=new TouchDevice { Path="HID#generic",Contacts=2 };Assert(d.IsPhysical&&d.SupportsTwoTouches,"Generic two-touch controller is supported without brand matching");
            d.Path="HID#UPDDVHID";Assert(!d.IsPhysical,"UPDD duplicate remains excluded");
            d.Path="HID#VIRTUAL_DIGITIZER";Assert(!d.IsPhysical,"Synthetic digitizer remains excluded");
            d.Path="HID#generic";d.Contacts=1;Assert(!d.SupportsTwoTouches,"Single-touch capacity is not presented as multi-touch");
        }
    }
}

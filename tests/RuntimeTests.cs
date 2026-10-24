using System;
using System.Collections.Generic;
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
                Coalescing();LifecycleBoundaries();FailedFlush();ProfileRoundtrip(directory);ProfileRestore();Observation();Interop();
                Console.WriteLine("PASS: "+assertions+" assertions; history coalescing, profile persistence/restore, observations and interop layouts.");return 0;
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

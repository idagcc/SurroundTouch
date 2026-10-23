using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using SurroundTouch;

public static class CoreTests
{
    private static int assertions;
    public static int Main() { return Run(); }
    public static int Run()
    {
        assertions = 0;
        try
        {
            MappingEndpoints(); MappingTransforms(); MappingValidationAndPersistence();
            MergeAndKeepalive(); ReleaseOrdering(); CancellationAndReuse();
            CancelPointer(); SequenceAndDuplicateDown(); SendFailurePreservesState(); Capacity();
            Console.WriteLine("PASS: " + assertions + " assertions; mapping and contact lifecycle tests.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL after " + assertions + " assertions: " + exception);
            return 1;
        }
    }

    private static void Assert(bool condition, string message)
    { assertions++; if (!condition) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual, string message)
    { Assert(EqualityComparer<T>.Default.Equals(expected, actual), message + ": expected " + expected + ", got " + actual); }
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { assertions++; return; }
        throw new Exception(message + ": expected " + typeof(T).Name);
    }

    private static Mapping BasicMapping()
    {
        return new Mapping { DevicePath = "left", Source = new PixelRect(0, 0, 3840, 1080),
            Target = new PixelRect(0, 0, 1920, 1080) };
    }

    private static void MappingEndpoints()
    {
        Mapping m = BasicMapping();
        Equal(new PixelPoint(0, 0), m.Map(new PixelPoint(0, 0)), "Left starts at desktop origin");
        Equal(new PixelPoint(1919, 1079), m.Map(new PixelPoint(3839, 1079)), "Left ends before right half");
        m.Target = new PixelRect(1920, 0, 1920, 1080);
        Equal(new PixelPoint(1920, 0), m.Map(new PixelPoint(0, 0)), "Right starts at seam");
        Equal(new PixelPoint(3839, 1079), m.Map(new PixelPoint(3839, 1079)), "Right ends inside desktop");
        m.Source = new PixelRect(-3840, -100, 3840, 1080);
        m.Target = new PixelRect(-1920, -1080, 1920, 1080);
        Equal(new PixelPoint(-1920, -1080), m.Map(new PixelPoint(-3840, -100)), "Negative source and desktop origins");
        Equal(new PixelPoint(-1, -1), m.Map(new PixelPoint(-1, 979)), "Negative far endpoints");
        Equal(new PixelPoint(-1920, -1), m.Map(new PixelPoint(Int32.MinValue, Int32.MaxValue)), "Out of bounds input clamps");
        m.Source = new PixelRect(7, 9, 1, 1); m.Target = new PixelRect(3, 4, 1, 1);
        Equal(new PixelPoint(3, 4), m.Map(new PixelPoint(7, 9)), "Single pixel rectangle");
    }

    private static void MappingTransforms()
    {
        Mapping m = new Mapping { Source = new PixelRect(0, 0, 101, 101), Target = new PixelRect(10, 20, 201, 301) };
        m.Rotation = 90;
        Equal(new PixelPoint(210, 20), m.Map(new PixelPoint(0, 0)), "90 degrees top left");
        Equal(new PixelPoint(10, 320), m.Map(new PixelPoint(100, 100)), "90 degrees bottom right");
        m.Rotation = 180;
        Equal(new PixelPoint(210, 320), m.Map(new PixelPoint(0, 0)), "180 degrees top left");
        m.Rotation = 270;
        Equal(new PixelPoint(10, 320), m.Map(new PixelPoint(0, 0)), "270 degrees top left");
        m.Rotation = 0; m.FlipX = true; m.FlipY = true;
        Equal(new PixelPoint(210, 320), m.Map(new PixelPoint(0, 0)), "Both flips");
        m.FlipX = false; m.FlipY = false;
        m.MinX = .1; m.MaxX = .9; m.MinY = .2; m.MaxY = .8;
        Equal(new PixelPoint(10, 20), m.Map(new PixelPoint(10, 20)), "Crop lower bounds");
        Equal(new PixelPoint(210, 320), m.Map(new PixelPoint(90, 80)), "Crop upper bounds");
        Equal(new PixelPoint(110, 170), m.Map(new PixelPoint(50, 50)), "Crop center");
        Equal(new PixelPoint(10, 320), m.Map(new PixelPoint(0, 100)), "Crop clamps beyond bounds");
        m.FlipX = true; m.Rotation = 90;
        Equal(new PixelPoint(210, 320), m.Map(new PixelPoint(10, 20)), "Flip happens before rotation");
    }

    private static void MappingValidationAndPersistence()
    {
        Mapping m = BasicMapping();
        m.Rotation = 1; Throws<ArgumentException>(delegate { m.Validate(); }, "Reject invalid rotation");
        m.Rotation = 0; m.MinX = m.MaxX; Throws<ArgumentException>(delegate { m.Validate(); }, "Reject empty crop");
        m.MinX = Double.NaN; Throws<ArgumentException>(delegate { m.Validate(); }, "Reject NaN crop");
        m.MinX = 0; m.MaxY = Double.PositiveInfinity; Throws<ArgumentException>(delegate { m.Validate(); }, "Reject infinite crop");
        m.MaxY = 1; m.Source.Width = 0; Throws<ArgumentException>(delegate { m.Validate(); }, "Reject empty source");
        m.Source.Width = 3840; m.Target = new PixelRect(Int32.MaxValue, 0, 2, 1);
        Throws<ArgumentException>(delegate { m.Validate(); }, "Reject target endpoint overflow");
        m = BasicMapping();
        XmlSerializer serializer = new XmlSerializer(typeof(Mapping));
        StringWriter writer = new StringWriter(); serializer.Serialize(writer, m);
        Assert(!writer.ToString().Contains("<Source>"), "Source rectangle is not persisted");
        Mapping read = (Mapping)serializer.Deserialize(new StringReader(writer.ToString()));
        Equal(1920, read.Target.Width, "Target persists"); Equal(0, read.Source.Width, "Source is resolved again at runtime");
    }

    private static TouchSample Sample(string device, uint id, TouchPhase phase, int x, int y)
    { return new TouchSample { Device = device, Id = id, Phase = phase, Position = new PixelPoint(x, y) }; }
    private static TouchPipeline Pipeline(List<InjectedContact[]> frames)
    { return new TouchPipeline(delegate(InjectedContact[] frame) { frames.Add(frame); }); }

    private static void MergeAndKeepalive()
    {
        List<InjectedContact[]> frames = new List<InjectedContact[]>(); TouchPipeline p = Pipeline(frames);
        p.Process(new[] { Sample("left", 7, TouchPhase.Down, 10, 10), Sample("right", 7, TouchPhase.Down, 2000, 10) });
        Equal(2, p.ActiveCount, "Same hardware ID on different devices remains independent");
        Equal(2, frames[0].Length, "Two contacts are emitted together");
        Assert(frames[0][0].Id != frames[0][1].Id, "Injected IDs are unique");
        Equal(TouchPhase.Down, frames[0][0].Phase, "First starts down");
        Equal(TouchPhase.Down, frames[0][1].Phase, "Second starts down");
        p.Process(new[] { Sample("left", 7, TouchPhase.Move, 20, 20) });
        Equal(2, frames[1].Length, "Other device retained in one-controller frame");
        Equal(new PixelPoint(2000, 10), frames[1][1].Position, "Stationary other device retains position");
        Equal(TouchPhase.Move, frames[1][1].Phase, "Stationary other device remains down");
        p.Tick(); Equal(2, frames[2].Length, "Keepalive contains both controllers");
        p.Process(new[] { Sample("left", 7, TouchPhase.Up, 20, 20) });
        Equal(1, p.ActiveCount, "Only requested controller released");
        Equal(TouchPhase.Up, frames[3][0].Phase, "Release flag");
        Equal(TouchPhase.Move, frames[3][1].Phase, "Other contact retained during release");
        p.Tick(); Equal(1, frames[4].Length, "Released contact omitted next frame");
    }

    private static void ReleaseOrdering()
    {
        List<InjectedContact[]> frames = new List<InjectedContact[]>(); TouchPipeline p = Pipeline(frames);
        p.Process(new[] { Sample("left", 1, TouchPhase.Down, 10, 10) });
        p.Process(new[] { Sample("left", 1, TouchPhase.Up, 100, 200), Sample("right", 2, TouchPhase.Down, 2000, 10) });
        Equal(3, frames.Count, "Moved release creates move then release");
        Equal(1, frames[1].Length, "Prelude does not prematurely start new contact");
        Equal(TouchPhase.Move, frames[1][0].Phase, "Release prelude is a move");
        Equal(new PixelPoint(100, 200), frames[1][0].Position, "Prelude reaches release position");
        Equal(frames[1][0].Position, frames[2][0].Position, "UP exactly matches previous injection");
        Equal(TouchPhase.Up, frames[2][0].Phase, "Final release is up");
        Equal(TouchPhase.Down, frames[2][1].Phase, "Concurrent new contact starts once");
        Equal(1, p.ActiveCount, "New contact survives release");
    }

    private static void CancellationAndReuse()
    {
        List<InjectedContact[]> frames = new List<InjectedContact[]>(); TouchPipeline p = Pipeline(frames);
        p.Process(new[] { Sample("a", 1, TouchPhase.Down, 3, 4), Sample("b", 1, TouchPhase.Down, 5, 6) });
        p.Process(new[] { Sample("a", 1, TouchPhase.Cancel, 999, 999) });
        Equal(2, frames.Count, "Cancellation does not invent movement");
        Equal(TouchPhase.Cancel, frames[1][0].Phase, "Individual contact canceled");
        Equal(new PixelPoint(3, 4), frames[1][0].Position, "Cancel uses last position");
        p.CancelAll(); Equal(0, p.ActiveCount, "CancelAll clears after injection");
        Equal(1, frames[2].Length, "CancelAll cancels remaining contact");
        Equal(TouchPhase.Cancel, frames[2][0].Phase, "CancelAll phase");
        p.CancelAll(); p.Tick(); Equal(3, frames.Count, "Empty operations do not inject empty frames");
        p.Process(new[] { Sample("a", 1, TouchPhase.Down, 1, 2) });
        Equal(1u, frames[3][0].Id, "Released ID may be reused on a later frame");
    }

    private static void SequenceAndDuplicateDown()
    {
        List<InjectedContact[]> frames = new List<InjectedContact[]>(); TouchPipeline p = Pipeline(frames);
        p.Process(new[] { Sample("a", 1, TouchPhase.Move, 0, 0), Sample("b", 2, TouchPhase.Up, 0, 0) });
        Equal(0, frames.Count, "Mid-gesture input does not invent a down");
        p.Process(new[] { Sample("a", 1, TouchPhase.Down, 1, 1), Sample("a", 1, TouchPhase.Move, 2, 2), Sample("a", 1, TouchPhase.Up, 2, 2) });
        Equal(3, frames.Count, "Multiple events for one key retain ordering");
        Equal(TouchPhase.Down, frames[0][0].Phase, "Sequence down");
        Equal(TouchPhase.Move, frames[1][0].Phase, "Sequence move");
        Equal(TouchPhase.Up, frames[2][0].Phase, "Sequence up");
        p.Process(new[] { Sample("a", 1, TouchPhase.Down, 1, 1) });
        p.Process(new[] { Sample("A", 1, TouchPhase.Down, 2, 2) });
        Equal(1, p.ActiveCount, "Device path comparison ignores case");
        Equal(TouchPhase.Move, frames[4][0].Phase, "Duplicate down updates existing contact");
        TouchSample highPressure = Sample("a", 1, TouchPhase.Move, 2, 2); highPressure.Pressure = UInt32.MaxValue;
        p.Process(new[] { highPressure }); Equal(1024u, frames[5][0].Pressure, "Pressure is bounded");
        int priorCount = frames.Count;
        Throws<ArgumentException>(delegate { p.Process(new[] { Sample("a", 1, TouchPhase.Move, 20, 20), new TouchSample() }); }, "Bad input rejected before emission");
        Equal(priorCount, frames.Count, "Malformed batch did not emit partial input");
    }

    private static void CancelPointer()
    {
        List<InjectedContact[]> frames = new List<InjectedContact[]>(); bool fail = false;
        TouchPipeline p = new TouchPipeline(delegate(InjectedContact[] frame)
        { if (fail) throw new InvalidOperationException("Sink failed"); frames.Add(frame); });
        p.Process(new[] { Sample("left", 7, TouchPhase.Down, 10, 20),
            Sample("right", 7, TouchPhase.Down, 2010, 20), Sample("right", 8, TouchPhase.Down, 2020, 30) });
        p.CancelPointer(99); Equal(1, frames.Count, "Inactive pointer cancellation does not inject");
        fail = true; Throws<InvalidOperationException>(delegate { p.CancelPointer(7); }, "Pointer cancellation failure surfaces");
        Equal(3, p.ActiveCount, "Failed pointer cancellation keeps all contacts");
        fail = false; p.CancelPointer(7);
        Equal(3, frames[1].Length, "Cancellation frame includes unrelated active contacts");
        Equal(TouchPhase.Cancel, frames[1][0].Phase, "First matching device is canceled");
        Equal(TouchPhase.Cancel, frames[1][1].Phase, "Second matching device is canceled");
        Equal(TouchPhase.Move, frames[1][2].Phase, "Unrelated contact remains active");
        Equal(new PixelPoint(10, 20), frames[1][0].Position, "Pointer cancellation retains last injected position");
        Equal(new PixelPoint(2010, 20), frames[1][1].Position, "Other controller cancellation retains its position");
        Equal(1, p.ActiveCount, "Only matching hardware IDs are removed");
        p.Tick(); Equal(1, frames[2].Length, "Keepalive continues for unaffected contact");
        p.CancelPointer(7); Equal(3, frames.Count, "Repeated cancellation is a no-op");
        p.CancelPointer(8); Equal(0, p.ActiveCount, "Remaining pointer can be canceled independently");
    }

    private static void SendFailurePreservesState()
    {
        List<InjectedContact[]> frames = new List<InjectedContact[]>(); bool fail = false; int calls = 0; int failCall = -1;
        TouchPipeline p = new TouchPipeline(delegate(InjectedContact[] frame)
        { calls++; if (fail || calls == failCall) throw new InvalidOperationException("Sink failed"); frames.Add(frame); });
        fail = true;
        Throws<InvalidOperationException>(delegate { p.Process(new[] { Sample("a", 1, TouchPhase.Down, 1, 1) }); }, "Down failure surfaces");
        Equal(0, p.ActiveCount, "Failed down did not create contact");
        fail = false; p.Process(new[] { Sample("a", 1, TouchPhase.Down, 1, 1) });
        fail = true;
        Throws<InvalidOperationException>(delegate { p.Process(new[] { Sample("a", 1, TouchPhase.Move, 2, 2) }); }, "Move failure surfaces");
        fail = false; p.Tick(); Equal(new PixelPoint(1, 1), frames[1][0].Position, "Failed move did not advance position");
        failCall = calls + 2;
        Throws<InvalidOperationException>(delegate { p.Process(new[] { Sample("a", 1, TouchPhase.Up, 8, 9) }); }, "Release failure after successful prelude surfaces");
        Equal(1, p.ActiveCount, "Failed release remains active");
        failCall = -1; p.Tick(); Equal(new PixelPoint(8, 9), frames[3][0].Position, "Successful prelude committed independently");
        fail = true; Throws<InvalidOperationException>(delegate { p.CancelAll(); }, "Cancel failure surfaces");
        Equal(1, p.ActiveCount, "Failed cancel preserves active contact");
        fail = false; p.CancelAll(); Equal(0, p.ActiveCount, "Cancel can be retried");
    }

    private static void Capacity()
    {
        List<InjectedContact[]> frames = new List<InjectedContact[]>(); TouchPipeline p = Pipeline(frames);
        List<TouchSample> samples = new List<TouchSample>();
        for (uint id = 0; id < 256; id++) samples.Add(Sample("device", id, TouchPhase.Down, (int)id, 1));
        p.Process(samples); Equal(256, p.ActiveCount, "Maximum capacity accepted");
        HashSet<uint> ids = new HashSet<uint>();
        foreach (InjectedContact contact in frames[0])
        { Assert(contact.Id >= 1 && contact.Id <= 256, "ID remains within supported range"); Assert(ids.Add(contact.Id), "No ID collision"); }
        Throws<InvalidOperationException>(delegate { p.Process(new[] { Sample("other", 0, TouchPhase.Down, 0, 0) }); }, "Overflow rejected");
        Equal(1, frames.Count, "Overflow emits no partial frame"); Equal(256, p.ActiveCount, "Overflow preserves existing contacts");
        p.CancelAll(); Equal(0, p.ActiveCount, "All maximum-capacity contacts released");
    }
}

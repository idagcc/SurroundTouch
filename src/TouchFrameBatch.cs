using System;
using System.Collections.Generic;

namespace SurroundTouch
{
    // Merge only movement within a single Windows history read. Never hold input
    // across messages, or merge through a DOWN/UP/CANCEL boundary.
    internal sealed class TouchFrameBatch
    {
        readonly Action<IEnumerable<TouchSample>> send;
        readonly Dictionary<string,Dictionary<uint,TouchSample>> pending=new Dictionary<string,Dictionary<uint,TouchSample>>(StringComparer.OrdinalIgnoreCase);
        readonly List<TouchSample> output=new List<TouchSample>();
        int pendingFrames;
        internal int MergedFrames { get; private set; }
        internal TouchFrameBatch(Action<IEnumerable<TouchSample>> send) { this.send=send; }
        internal void Add(List<TouchSample> frame)
        {
            if(frame.Count==0)return;
            foreach(TouchSample sample in frame)
                if(sample.Phase!=TouchPhase.Move) { Flush();send(frame);return; }
            if(pendingFrames++>0)MergedFrames++;
            foreach(TouchSample sample in frame)
            {
                Dictionary<uint,TouchSample> device;
                if(!pending.TryGetValue(sample.Device,out device)) { device=new Dictionary<uint,TouchSample>();pending.Add(sample.Device,device); }
                device[sample.Id]=sample;
            }
        }
        internal void Flush()
        {
            if(pendingFrames==0)return;
            output.Clear();
            foreach(var device in pending.Values)foreach(TouchSample sample in device.Values)output.Add(sample);
            send(output);
            foreach(var device in pending.Values)device.Clear();
            pendingFrames=0;
        }
        internal void Discard()
        {
            foreach(var device in pending.Values)device.Clear();
            output.Clear();pendingFrames=0;
        }
    }
}

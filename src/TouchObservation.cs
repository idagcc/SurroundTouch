using System;
using System.Collections.Generic;
using System.Text;

namespace SurroundTouch
{
    // A claimed contact limit is different from observing real simultaneous input.
    internal sealed class TouchObservation
    {
        readonly Dictionary<uint,string> active=new Dictionary<uint,string>();
        readonly Dictionary<string,int> peaks=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,int> counts=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        internal int PeakContacts { get; private set; }
        internal int PeakControllers { get; private set; }
        internal void Record(uint id,string device,TouchPhase phase)
        {
            if(phase==TouchPhase.Up||phase==TouchPhase.Cancel)active.Remove(id);
            else if(phase==TouchPhase.Down||active.ContainsKey(id))active[id]=device;
            UpdatePeaks();
        }
        internal void RecordFrame(IList<TouchSample> frame)
        {
            // Treat a Windows report as one instant. A release and a new down in
            // the same frame must not falsely prove overlapping fingers.
            foreach(TouchSample sample in frame)
                if(sample.Phase==TouchPhase.Up||sample.Phase==TouchPhase.Cancel)active.Remove(sample.Id);
            foreach(TouchSample sample in frame)
                if(sample.Phase==TouchPhase.Down||(sample.Phase==TouchPhase.Move&&active.ContainsKey(sample.Id)))active[sample.Id]=sample.Device;
            UpdatePeaks();
        }
        void UpdatePeaks()
        {
            counts.Clear();
            foreach(string source in active.Values)
            {
                int count;counts.TryGetValue(source,out count);counts[source]=count+1;
            }
            foreach(var count in counts)
            {
                int old;peaks.TryGetValue(count.Key,out old);peaks[count.Key]=Math.Max(old,count.Value);
            }
            PeakContacts=Math.Max(PeakContacts,active.Count);PeakControllers=Math.Max(PeakControllers,counts.Count);
        }
        internal void ClearActive() { active.Clear(); }
        internal string Report(bool corrected)
        {
            var b=new StringBuilder();
            b.AppendLine("Latest local touch test ("+(corrected?"corrected/injected input; does not verify physical driver capability":"original Windows input")+"): "+DateTime.UtcNow.ToString("O"));
            b.AppendLine("Observed simultaneous contacts: "+PeakContacts+"; distinct simultaneous source controllers: "+PeakControllers);
            b.AppendLine("Two simultaneous contacts: "+(PeakContacts>=2?"OBSERVED":"NOT YET OBSERVED; hold two fingers inside the test window"));
            b.AppendLine("Two source controllers together: "+(PeakControllers>=2?"OBSERVED":"NOT YET OBSERVED"));
            foreach(var pair in peaks)b.AppendLine("  "+pair.Key+": peak "+pair.Value+" contact(s)");
            b.AppendLine("No touch coordinates were recorded. A failed or incomplete test does not prove that hardware is single-touch.");
            return b.ToString();
        }
    }
}

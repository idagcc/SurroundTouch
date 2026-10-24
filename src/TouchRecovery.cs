using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;

namespace SurroundTouch
{
    // Runs only on the input thread. Calibration belongs to a stable device path,
    // never to the temporary handle Windows assigns after a USB reconnect.
    internal sealed class TouchRecovery
    {
        readonly Dictionary<string,PixelRect> expected=new Dictionary<string,PixelRect>(StringComparer.OrdinalIgnoreCase);
        readonly Action reset;
        readonly Action<bool> changed;
        internal readonly Dictionary<IntPtr,TouchDevice> Devices=new Dictionary<IntPtr,TouchDevice>();
        internal bool Waiting { get; private set; }
        internal long ResumedAt { get; private set; }
        internal uint ResumedTime { get; private set; }

        internal TouchRecovery(List<TouchDevice> source,Dictionary<string,Mapping> mappings,Action reset,Action<bool> changed)
        {
            this.reset=reset;this.changed=changed;
            foreach(var item in mappings)expected.Add(item.Key,item.Value.Source);
            foreach(TouchDevice device in source)Devices.Add(device.Handle,device);
        }
        internal void Pause()
        {
            if(Waiting)return;
            // If contact cleanup fails, let the caller stop correction. Retrying
            // with uncertain synthetic contact state could create stuck gestures.
            reset();Waiting=true;changed(true);
        }
        internal void Check(Func<List<TouchDevice>> enumerate)
        {
            List<TouchDevice> current;
            try { current=enumerate(); }
            catch(Win32Exception) { Pause();return; }
            catch(InvalidOperationException) { Pause();return; } // enumeration raced a PnP change
            var found=new Dictionary<string,TouchDevice>(StringComparer.OrdinalIgnoreCase);
            var handles=new Dictionary<IntPtr,TouchDevice>();
            foreach(TouchDevice device in current)
            {
                if(handles.ContainsKey(device.Handle))throw new InvalidOperationException("Windows reported duplicate touch controller handles. Refresh devices before restarting.");
                handles.Add(device.Handle,device);
                if(!device.IsPhysical||String.IsNullOrEmpty(device.Path))continue;
                if(found.ContainsKey(device.Path))throw new InvalidOperationException("Windows reported an ambiguous touch controller identity. Refresh and verify the assignments.");
                found.Add(device.Path,device);
            }
            bool missing=false;
            foreach(var item in expected)
            {
                TouchDevice device;
                if(!found.TryGetValue(item.Key,out device)||!device.HasRect) { missing=true;continue; }
                PixelRect old=item.Value,now=device.Source;
                if(old.X!=now.X||old.Y!=now.Y||old.Width!=now.Width||old.Height!=now.Height)
                    throw new InvalidOperationException("Windows changed a controller's display mapping. Refresh and verify the layout before restarting.");
            }
            if(missing) { Pause();return; }
            bool rebound=false;
            foreach(TouchDevice device in Devices.Values)
            {
                if(!device.IsPhysical)continue;
                TouchDevice replacement;
                if(!handles.TryGetValue(device.Handle,out replacement)||!String.Equals(device.Path,replacement.Path,StringComparison.OrdinalIgnoreCase)) { rebound=true;break; }
            }
            // A disconnect/reconnect can complete between two checks. Cancel the
            // old gesture even if both calibrated controllers are already back.
            if(rebound)Pause();
            Devices.Clear();foreach(var item in handles)Devices.Add(item.Key,item.Value);
            if(Waiting)
            {
                DiscardOldInput();
                Waiting=false;changed(false);
            }
        }
        internal void DiscardOldInput()
        {
            ResumedAt=Stopwatch.GetTimestamp();ResumedTime=unchecked((uint)Environment.TickCount);
        }
        internal bool AcceptInput(ulong counter,uint time,bool latestFrame)
        {
            if(Waiting)return false;
            if(ResumedAt==0)return true;
            // Reject history/queued DOWNs from before recovery, as well as moves.
            // Windows normally supplies QPC or tick time; zero-timestamp drivers
            // may contribute only the newest frame, never historical gestures.
            if(counter!=0)return counter>(ulong)ResumedAt;
            if(time==0)return latestFrame;
            return AfterBoundary(time,ResumedTime,unchecked((uint)Environment.TickCount));
        }
        internal static bool AfterBoundary(uint time,uint boundary,uint now)
        {
            // After a minute the queue boundary is no longer needed. This also
            // prevents an old 32-bit cutoff affecting a long-running installation.
            return unchecked(now-boundary)>60000||unchecked((int)(time-boundary))>0;
        }
    }
}

using System;
using System.Diagnostics;
using System.Threading;

namespace SurroundTouch
{
    // Aggregates only: no coordinates or other applications' content.
    internal static class RuntimeStats
    {
        internal static long InputFrames,CoalescedMoves,InjectedFrames,InjectionRetries,SlowReads;
        static long maxReadTicks;
        internal static void ReadCompleted(long ticks)
        {
            if(ticks>Stopwatch.Frequency/100)Interlocked.Increment(ref SlowReads);
            long before=Interlocked.Read(ref maxReadTicks);
            while(ticks>before)
            {
                long actual=Interlocked.CompareExchange(ref maxReadTicks,ticks,before);
                if(actual==before)break;
                before=actual;
            }
        }
        internal static string Report()
        {
            using(Process p=Process.GetCurrentProcess())
                return "Runtime counters (this launch): input frames="+Interlocked.Read(ref InputFrames)+
                    ", merged movement frames="+Interlocked.Read(ref CoalescedMoves)+", injected frames="+Interlocked.Read(ref InjectedFrames)+
                    ", injection retries="+Interlocked.Read(ref InjectionRetries)+", reads over 10 ms="+Interlocked.Read(ref SlowReads)+
                    ", longest read ms="+(Interlocked.Read(ref maxReadTicks)*1000.0/Stopwatch.Frequency).ToString("F2")+
                    Environment.NewLine+"Process: CPU seconds="+p.TotalProcessorTime.TotalSeconds.ToString("F2")+
                    ", working set MiB="+(p.WorkingSet64/1048576.0).ToString("F1")+", GC collections="+
                    GC.CollectionCount(0)+"/"+GC.CollectionCount(1)+"/"+GC.CollectionCount(2);
        }
    }
}

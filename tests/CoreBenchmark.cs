using System;
using System.Diagnostics;
using SurroundTouch;

// Pure managed microbenchmark. Does not call Windows or inject desktop input.
internal static class CoreBenchmark
{
    static long checksum;
    static void Main()
    {
        Run("warmup",1000,false);Run("two-contact movement",200000,false);Run("held-contact keepalive",1000000,true);
        Console.WriteLine("Checksum: "+checksum);
    }
    static void Run(string name,int iterations,bool held)
    {
        var p=new TouchPipeline(delegate(InjectedContact[] frame) { checksum+=frame.Length+frame[0].Position.X; });
        p.Process(new[]{new TouchSample { Device="left",Id=7,Phase=TouchPhase.Down },new TouchSample { Device="right",Id=7,Phase=TouchPhase.Down }});
        var samples=new[]{new TouchSample { Device="left",Id=7,Phase=TouchPhase.Move },new TouchSample { Device="right",Id=7,Phase=TouchPhase.Move }};
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();int collections=GC.CollectionCount(0);
        var watch=Stopwatch.StartNew();
        for(int i=0;i<iterations;i++)
        {
            if(held)p.Tick();
            else { samples[0].Position=new PixelPoint(i%1000,10);samples[1].Position=new PixelPoint(2000+i%1000,10);p.Process(samples); }
        }
        watch.Stop();Console.WriteLine(name+": "+iterations+" iterations, "+watch.Elapsed.TotalMilliseconds.ToString("F2")+" ms, Gen0 collections="+(GC.CollectionCount(0)-collections));
        p.CancelAll();
    }
}

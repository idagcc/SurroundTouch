using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SurroundTouch
{
    internal static class Native
    {
        internal const uint DOWN=0x10000, UPDATE=0x20000, UP=0x40000, INRANGE=2, INCONTACT=4, CANCELED=0x8000;
        [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X,Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left,Top,Right,Bottom; public PixelRect Pixels { get { return new PixelRect(Left,Top,Right-Left,Bottom-Top); } } }
        [StructLayout(LayoutKind.Sequential)] internal struct POINTER_INFO
        {
            public uint pointerType,pointerId,frameId,pointerFlags;
            public IntPtr sourceDevice,hwndTarget;
            public POINT ptPixelLocation,ptHimetricLocation,ptPixelLocationRaw,ptHimetricLocationRaw;
            public uint dwTime,historyCount; public int InputData; public uint dwKeyStates;
            public ulong PerformanceCount; public uint ButtonChangeType;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct TOUCH
        { public POINTER_INFO pointerInfo; public uint touchFlags,touchMask; public RECT rcContact,rcContactRaw; public uint orientation,pressure; }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] internal struct DEVICE
        {
            public uint displayOrientation; public IntPtr device; public uint pointerDeviceType; public IntPtr monitor;
            public uint startingCursorId; public ushort maxActiveContacts;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=520)] public string productString;
        }
        [DllImport("user32.dll",SetLastError=true)] internal static extern bool GetPointerDevices(ref uint count,[In,Out] DEVICE[] devices);
        [DllImport("user32.dll",SetLastError=true)] internal static extern bool GetPointerDeviceRects(IntPtr device,out RECT deviceRect,out RECT displayRect);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern uint GetRawInputDeviceInfo(IntPtr device,uint command,StringBuilder data,ref uint size);
        [DllImport("user32.dll",SetLastError=true)] internal static extern bool GetPointerTouchInfo(uint id,out TOUCH touch);
        [DllImport("user32.dll",SetLastError=true)] internal static extern bool GetPointerFrameTouchInfo(uint id,ref uint count,[Out] TOUCH[] touches);
        [DllImport("user32.dll",SetLastError=true)] internal static extern bool GetPointerFrameTouchInfoHistory(uint id,ref uint entries,ref uint count,[Out] TOUCH[] touches);
        [DllImport("user32.dll",SetLastError=true)] internal static extern bool SkipPointerFrameMessages(uint id);
        [DllImport("user32.dll",SetLastError=true)] internal static extern bool RegisterPointerInputTarget(IntPtr hwnd,uint type);
        [DllImport("user32.dll",SetLastError=true)] internal static extern bool UnregisterPointerInputTarget(IntPtr hwnd,uint type);
        [DllImport("user32.dll",SetLastError=true)] internal static extern bool InitializeTouchInjection(uint max,uint mode);
        [DllImport("user32.dll",SetLastError=true)] internal static extern bool InjectTouchInput(uint count,[In] TOUCH[] touches);
        [DllImport("user32.dll",SetLastError=true)] internal static extern bool RegisterHotKey(IntPtr hwnd,int id,uint modifiers,uint key);
        [DllImport("user32.dll",SetLastError=true)] internal static extern bool UnregisterHotKey(IntPtr hwnd,int id);
        [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
        [DllImport("wtsapi32.dll",SetLastError=true)] internal static extern bool WTSRegisterSessionNotification(IntPtr hwnd,uint flags);
        [DllImport("wtsapi32.dll")] internal static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(IntPtr process,uint desired,out IntPtr token);
        [DllImport("advapi32.dll",SetLastError=true)] static extern bool GetTokenInformation(IntPtr token,int infoClass,out uint value,uint size,out uint returned);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr hwnd,uint message,IntPtr w,IntPtr l);
        internal static bool HasUIAccess()
        {
            IntPtr token; if(!OpenProcessToken(GetCurrentProcess(),8,out token))return false;
            try { uint value,size; return GetTokenInformation(token,26,out value,4,out size)&&value!=0; } finally { CloseHandle(token); }
        }
        internal static void DpiAware() { try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch(EntryPointNotFoundException) { SetProcessDPIAware(); } }
        internal static string DevicePath(IntPtr handle)
        {
            uint size=0; if(GetRawInputDeviceInfo(handle,0x20000007,null,ref size)==uint.MaxValue || size==0 || size>32768)return "";
            StringBuilder b=new StringBuilder((int)size+1);
            return GetRawInputDeviceInfo(handle,0x20000007,b,ref size)==uint.MaxValue ? "" : b.ToString();
        }
        internal static List<TouchDevice> Devices()
        {
            for(int attempt=0;attempt<3;attempt++)
            {
                uint count=0; if(!GetPointerDevices(ref count,null))throw new Win32Exception();
                if(count==0)return new List<TouchDevice>();
                if(count>1024)throw new InvalidOperationException("Unexpected pointer device count.");
                DEVICE[] all=new DEVICE[count]; if(!GetPointerDevices(ref count,all))continue;
                List<TouchDevice> list=new List<TouchDevice>();
                for(int i=0;i<count;i++)
                {
                    DEVICE d=all[i]; if(d.pointerDeviceType!=3)continue;
                    RECT physical,display; bool rects=GetPointerDeviceRects(d.device,out physical,out display);
                    string path=DevicePath(d.device);
                    list.Add(new TouchDevice { Handle=d.device,Path=path,Name=String.IsNullOrWhiteSpace(d.productString)?"Touchscreen":d.productString,
                        Contacts=d.maxActiveContacts,Source=display.Pixels,HasRect=rects,Number=list.Count+1 });
                }
                return list;
            }
            throw new InvalidOperationException("Touch devices changed during detection. Press Refresh.");
        }
        internal static void Inject(InjectedContact[] contacts)
        {
            if(contacts.Length==0)return;
            if(injectionBuffers==null)injectionBuffers=new TOUCH[TouchPipeline.MaximumContacts+1][];
            TOUCH[] frame=injectionBuffers[contacts.Length];
            if(frame==null)injectionBuffers[contacts.Length]=frame=new TOUCH[contacts.Length];
            for(int i=0;i<frame.Length;i++)
            {
                InjectedContact c=contacts[i];
                frame[i].pointerInfo.pointerType=2; frame[i].pointerInfo.pointerId=c.Id;
                frame[i].pointerInfo.ptPixelLocation=new POINT { X=c.Position.X,Y=c.Position.Y };
                frame[i].pointerInfo.pointerFlags=c.Phase==TouchPhase.Down ? DOWN|INRANGE|INCONTACT : c.Phase==TouchPhase.Move ? UPDATE|INRANGE|INCONTACT : c.Phase==TouchPhase.Cancel ? UP|CANCELED : UP;
                frame[i].touchMask=4; frame[i].pressure=Math.Min(1024,c.Pressure);
            }
            for(int retry=0;retry<12;retry++)
            {
                if(InjectTouchInput((uint)frame.Length,frame)) { Interlocked.Increment(ref RuntimeStats.InjectedFrames);return; }
                int error=Marshal.GetLastWin32Error(); if(error!=21)throw new Win32Exception(error,"Windows rejected a corrected touch frame (error "+error+": "+new Win32Exception(error).Message+")");
                Interlocked.Increment(ref RuntimeStats.InjectionRetries);
                Thread.Sleep(1);
            }
            throw new Win32Exception(21,"Windows touch injection remained busy");
        }
        [ThreadStatic] static TOUCH[][] injectionBuffers;
    }
    internal sealed class TouchDevice
    {
        public IntPtr Handle; public string Path,Name; public int Number; public uint Contacts; public PixelRect Source; public bool HasRect;
        public bool IsUpdd { get { return !String.IsNullOrEmpty(Path)&&Path.IndexOf("UPDDVHID",StringComparison.OrdinalIgnoreCase)>=0; } }
        public bool IsInjectionDevice { get { return !String.IsNullOrEmpty(Path)&&Path.IndexOf("VIRTUAL_DIGITIZER",StringComparison.OrdinalIgnoreCase)>=0; } }
        public bool IsPhysical { get { return !IsUpdd&&!IsInjectionDevice; } }
        public bool SupportsTwoTouches { get { return Contacts>=2; } }
        public RoleProfile Identity { get { return new RoleProfile { Path=Path,SourceX=Source.X,SourceY=Source.Y,SourceWidth=HasRect?Source.Width:0,SourceHeight=HasRect?Source.Height:0 }; } }
        public override string ToString() { return Number+". "+Name+" ("+Contacts+" contacts)"; }
    }
}

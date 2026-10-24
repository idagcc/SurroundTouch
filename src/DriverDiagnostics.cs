using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace SurroundTouch
{
    internal static class DriverDiagnostics
    {
        internal static string LastTest="No local two-finger driver test has been completed during this launch.";
        [StructLayout(LayoutKind.Sequential)] struct RAW_DEVICE { public IntPtr handle;public uint type; }
        [StructLayout(LayoutKind.Explicit,Size=32)] struct RAW_INFO
        {
            [FieldOffset(0)] public uint size;
            [FieldOffset(4)] public uint type;
            [FieldOffset(8)] public uint vendor;
            [FieldOffset(12)] public uint product;
            [FieldOffset(20)] public ushort usagePage;
            [FieldOffset(22)] public ushort usage;
        }
        [DllImport("user32.dll",SetLastError=true)] static extern uint GetRawInputDeviceList([In,Out] RAW_DEVICE[] list,ref uint count,uint size);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern uint GetRawInputDeviceInfo(IntPtr handle,uint command,ref RAW_INFO info,ref uint size);
        internal static string Report()
        {
            var b=new StringBuilder();
            b.AppendLine("Driver compatibility: standard Windows HID / WM_POINTER touch; no ASUS-specific driver dependency.");
            b.AppendLine("Windows digitizer flags: 0x"+Native.GetSystemMetrics(94).ToString("X")+"; system contact limit: "+Native.GetSystemMetrics(95));
            List<TouchDevice> devices=Native.Devices();int physical=0;
            foreach(TouchDevice device in devices)
            {
                if(!device.IsPhysical)continue;physical++;
                b.AppendLine(device.Name+": driver reports "+device.Contacts+" contacts; "+(device.SupportsTwoTouches?"two-touch capacity advertised":"two-touch capacity NOT advertised")+
                    "; stable identity="+!String.IsNullOrEmpty(device.Path)+"; usable mapping="+(device.HasRect&&device.Source.Width>0&&device.Source.Height>0));
            }
            b.AppendLine("Independent physical controllers detected: "+physical+". Two controllers are needed to assign two panel regions.");
            try { AppendRawDigitizers(b); }catch(Exception ex) { b.AppendLine("Raw HID enumeration unavailable: "+ex.Message); }
            try
            {
                // Requested report only, on a background thread. Never query WMI in the input loop.
                var options=new EnumerationOptions { ReturnImmediately=false,Timeout=TimeSpan.FromSeconds(5) };
                using(var search=new ManagementObjectSearcher("root\\CIMV2","SELECT DeviceName,DeviceID,DriverProviderName,DriverVersion,InfName,IsSigned FROM Win32_PnPSignedDriver WHERE DeviceClass = 'HIDCLASS'",options))
                using(var drivers=search.Get())
                {
                    b.AppendLine("Installed HID driver inventory:");int count=0;
                    foreach(ManagementObject driver in drivers)
                    using(driver)
                    {
                        if(++count>256) { b.AppendLine("Inventory truncated at 256 drivers.");break; }
                        b.AppendLine("  "+driver["DeviceName"]+" | "+driver["DeviceID"]+" | provider="+driver["DriverProviderName"]+" | version="+driver["DriverVersion"]+" | INF="+driver["InfName"]+" | signed="+driver["IsSigned"]);
                    }
                }
            }
            catch(Exception ex) { b.AppendLine("Driver version inventory unavailable: "+ex.Message); }
            try
            {
                var options=new EnumerationOptions { ReturnImmediately=false,Timeout=TimeSpan.FromSeconds(5) };
                using(var search=new ManagementObjectSearcher("root\\CIMV2","SELECT Name,PNPDeviceID,ConfigManagerErrorCode,Status,Service FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0",options))
                using(var problems=search.Get())
                {
                    b.AppendLine("HID/USB devices with Device Manager errors:");int found=0;
                    foreach(ManagementObject problem in problems)
                    using(problem)
                    {
                        string id=Convert.ToString(problem["PNPDeviceID"]);
                        if(!id.StartsWith("HID\\",StringComparison.OrdinalIgnoreCase)&&!id.StartsWith("USB\\",StringComparison.OrdinalIgnoreCase))continue;
                        b.AppendLine("  "+problem["Name"]+" | "+id+" | error="+problem["ConfigManagerErrorCode"]+" | status="+problem["Status"]+" | service="+problem["Service"]);
                        if(++found>=128) { b.AppendLine("Error list truncated at 128 devices.");break; }
                    }
                    if(found==0)b.AppendLine("  No HID/USB errors reported by WMI.");
                }
            }
            catch(Exception ex) { b.AppendLine("Device Manager error inventory unavailable: "+ex.Message); }
            b.AppendLine(LastTest);
            b.AppendLine("If a panel is absent: check its USB data cable and Device Manager > Human Interface Devices. Check for disabled devices or driver errors and use the panel vendor's Windows HID/multi-touch mode or compatible signed driver.");
            b.AppendLine("Mouse-emulation-only, serial-only, and proprietary drivers that expose no Windows touch contacts cannot supply independent multi-touch here. A panel brand or country of manufacture does not determine compatibility.");
            return b.ToString();
        }
        static void AppendRawDigitizers(StringBuilder b)
        {
            uint size=(uint)Marshal.SizeOf(typeof(RAW_DEVICE));
            for(int attempt=0;attempt<3;attempt++)
            {
                uint count=0;
                if(GetRawInputDeviceList(null,ref count,size)==uint.MaxValue)throw new Win32Exception();
                if(count>4096)throw new InvalidOperationException("Unexpected raw device count.");
                if(count==0) { b.AppendLine("Raw digitizer HID collections: none.");return; }
                var all=new RAW_DEVICE[count];uint read=GetRawInputDeviceList(all,ref count,size);
                if(read==uint.MaxValue)continue;
                b.AppendLine("Raw digitizer HID collections (usage page 0D):");int found=0;
                for(int i=0;i<read;i++)
                {
                    if(all[i].type!=2)continue;
                    var info=new RAW_INFO { size=32 };uint length=32;
                    if(GetRawInputDeviceInfo(all[i].handle,0x2000000b,ref info,ref length)==uint.MaxValue||info.usagePage!=0x0d)continue;
                    found++;
                    b.AppendLine("  VID="+info.vendor.ToString("X4")+" PID="+info.product.ToString("X4")+" usage="+info.usage.ToString("X2")+(info.usage==4?" (touchscreen)":"")+" | "+Native.DevicePath(all[i].handle));
                }
                if(found==0)b.AppendLine("  None exposed through Raw Input.");
                return;
            }
            throw new InvalidOperationException("HID devices changed during enumeration. Retry the report.");
        }
    }
}

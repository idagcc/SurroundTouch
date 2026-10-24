using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SurroundTouch
{
    [DataContract] internal sealed class AppSettings
    {
        [DataMember] public bool ResumeCorrection;
        internal static AppSettings Load()
        {
            string path=Path.Combine(Profile.DirectoryPath,"settings.json");
            if(!File.Exists(path))return new AppSettings();
            if(new FileInfo(path).Length>4096)throw new InvalidDataException("Startup settings are too large.");
            using(var stream=File.OpenRead(path))
                return (AppSettings)new DataContractJsonSerializer(typeof(AppSettings)).ReadObject(stream)??new AppSettings();
        }
        internal void Save() { AtomicJson.Write(Path.Combine(Profile.DirectoryPath,"settings.json"),this); }
    }

    internal static class StartupRegistration
    {
        const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
        internal static string Command { get { return "\""+Application.ExecutablePath+"\" --startup"; } }
        internal static bool Enabled
        {
            get
            {
                using(RegistryKey key=Registry.CurrentUser.OpenSubKey(RunKey))
                    return key!=null&&String.Equals(key.GetValue("SurroundTouch") as string,Command,StringComparison.OrdinalIgnoreCase);
            }
        }
        internal static void SetEnabled(bool enabled)
        {
            if(enabled)
            {
                string installed=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"SurroundTouch","SurroundTouch.exe");
                if(!Native.HasUIAccess()||!String.Equals(Application.ExecutablePath,installed,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Open the installed SurroundTouch app before enabling startup at sign-in.");
                if(Command.Length>260)throw new InvalidOperationException("The installed application path is too long for Windows startup.");
            }
            using(RegistryKey key=Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if(enabled)key.SetValue("SurroundTouch",Command,RegistryValueKind.String);
                else if(String.Equals(key.GetValue("SurroundTouch") as string,Command,StringComparison.OrdinalIgnoreCase))key.DeleteValue("SurroundTouch",false);
            }
        }
    }
}

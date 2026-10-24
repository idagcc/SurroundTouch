using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace SurroundTouch
{
    [DataContract] internal sealed class RoleProfile
    {
        [DataMember] public string Path="";
        [DataMember] public int Rotation;
        [DataMember] public bool FlipX,FlipY;
        [DataMember] public double MinX=0,MinY=0,MaxX=1,MaxY=1;
        [DataMember] public int SourceX,SourceY,SourceWidth,SourceHeight;
    }
    [DataContract] internal sealed class Profile
    {
        [DataMember] public int Version=1;
        [DataMember] public string Display="";
        [DataMember] public int Width,Height,X,Y;
        [DataMember] public bool Vertical;
        [DataMember] public int SplitPercent=50,Gap;
        [DataMember] public RoleProfile First=new RoleProfile(),Second=new RoleProfile();
        internal static string DirectoryPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SurroundTouch"); } }
        internal static Profile Load()
        {
            string file=DefaultPath;
            if(!File.Exists(file))return new Profile();
            return Load(file);
        }
        internal static string DefaultPath { get { return Path.Combine(DirectoryPath,"profile.json"); } }
        internal static Profile Load(string file)
        {
            if(new FileInfo(file).Length>65536)throw new InvalidDataException("Saved profile is too large.");
            using(FileStream s=File.OpenRead(file))
            {
                Profile p=(Profile)new DataContractJsonSerializer(typeof(Profile)).ReadObject(s);
                if(p==null)throw new InvalidDataException("Empty profile.");
                p.Validate();
                return p;
            }
        }
        internal void Save()
        {
            Save(DefaultPath);
        }
        internal void Validate()
        {
            if(Version!=1||First==null||Second==null)throw new InvalidDataException("Unsupported saved profile.");
            if(SplitPercent<5||SplitPercent>95||Gap<0||Gap>1000)throw new InvalidDataException("Saved split is invalid.");
            foreach(RoleProfile role in new[]{First,Second})
            {
                var mapping=new Mapping { Source=new PixelRect(0,0,1,1),Target=new PixelRect(0,0,1,1),Rotation=role.Rotation,
                    MinX=role.MinX,MinY=role.MinY,MaxX=role.MaxX,MaxY=role.MaxY };
                try { mapping.Validate(); }catch(ArgumentException ex) { throw new InvalidDataException("Saved calibration is invalid.",ex); }
            }
        }
        internal void Save(string path)
        {
            Validate();
            AtomicJson.Write(path,this);
        }
        internal bool MatchesDisplay(string display,PixelRect bounds)
        {
            return String.Equals(Display,display,StringComparison.OrdinalIgnoreCase)&&Width==bounds.Width&&Height==bounds.Height&&X==bounds.X&&Y==bounds.Y;
        }
        internal string RestoreProblem(string display,PixelRect bounds,RoleProfile first,RoleProfile second)
        {
            if(!MatchesDisplay(display,bounds))
                return "The saved display or its layout has changed. Review the profile and press Start correction.";
            if(String.IsNullOrEmpty(First.Path)||String.IsNullOrEmpty(Second.Path)||String.Equals(First.Path,Second.Path,StringComparison.OrdinalIgnoreCase))
                return "Identify two different controllers and save the profile before automatic restore.";
            RoleProfile[] saved={First,Second},current={first,second};
            for(int i=0;i<2;i++)
            {
                if(current[i]==null||!String.Equals(saved[i].Path,current[i].Path,StringComparison.OrdinalIgnoreCase))return "Waiting for the saved USB touch controllers.";
                if(saved[i].SourceWidth<=0||saved[i].SourceHeight<=0)return "Press Start correction once to confirm this older profile for automatic restore.";
                if(saved[i].SourceX!=current[i].SourceX||saved[i].SourceY!=current[i].SourceY||saved[i].SourceWidth!=current[i].SourceWidth||saved[i].SourceHeight!=current[i].SourceHeight)
                    return "Windows changed a controller's display mapping. Review the profile and press Start correction.";
            }
            return null;
        }
    }
    internal static class AtomicJson
    {
        internal static void Write<T>(string path,T value)
        {
            path=Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                using(FileStream s=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                { new DataContractJsonSerializer(typeof(T)).WriteObject(s,value);s.Flush(true); }
                if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
            }
            finally { if(File.Exists(temp))File.Delete(temp); }
        }
    }
}

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
            string file=Path.Combine(DirectoryPath,"profile.json");
            if(!File.Exists(file))return new Profile();
            if(new FileInfo(file).Length>65536)throw new InvalidDataException("Saved profile is too large.");
            using(FileStream s=File.OpenRead(file))
            {
                Profile p=(Profile)new DataContractJsonSerializer(typeof(Profile)).ReadObject(s);
                if(p==null||p.Version!=1||p.First==null||p.Second==null)throw new InvalidDataException("Unsupported saved profile.");
                if(p.SplitPercent<5||p.SplitPercent>95||p.Gap<0||p.Gap>1000)throw new InvalidDataException("Saved split is invalid.");
                return p;
            }
        }
        internal void Save()
        {
            Directory.CreateDirectory(DirectoryPath);
            string path=Path.Combine(DirectoryPath,"profile.json"),temp=path+".tmp";
            using(FileStream s=File.Create(temp))new DataContractJsonSerializer(typeof(Profile)).WriteObject(s,this);
            if(File.Exists(path))File.Replace(temp,path,null); else File.Move(temp,path);
        }
    }
}

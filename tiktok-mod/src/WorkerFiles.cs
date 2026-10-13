using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace ScamWYF.TikTokPocket
{
    internal static class WorkerFiles
    {
        internal static bool Verify(string executable)
        {
            try
            {
                string root=Path.GetFullPath(Path.GetDirectoryName(executable));
                string prefix=root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
                string manifestPath=Path.Combine(root,"worker-files.json");
                if(Reparse(root) || Reparse(Path.GetDirectoryName(root)) || !File.Exists(manifestPath) || Reparse(manifestPath)
                    || new FileInfo(manifestPath).Length>1048576) return false;
                var manifest=JObject.Parse(File.ReadAllText(manifestPath));
                if((int?)manifest["schemaVersion"]!=1 || (string)manifest["appVersion"]!="1.0.1"
                    || (string)manifest["executable"]!="ScamWYF.TikTokBrowser.exe") return false;
                var entries=manifest["files"] as JArray;if(entries==null || entries.Count<1 || entries.Count>512) return false;
                long total=0;var known=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var entry in entries)
                {
                    string relative=(string)entry["path"],expected=(string)entry["sha256"];
                    var sizeToken=entry["size"];
                    if(relative==null || relative.Length<1 || relative.Length>240 || Path.IsPathRooted(relative)
                        || relative.IndexOf(':')>=0 || sizeToken==null || sizeToken.Type!=JTokenType.Integer
                        || expected==null || expected.Length!=64) return false;
                    foreach(char digit in expected) if(!((digit>='0' && digit<='9') || (digit>='a' && digit<='f') || (digit>='A' && digit<='F'))) return false;
                    var segments=relative.Replace('/',Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar);
                    string target=root;foreach(var segment in segments)
                    {
                        if(segment.Length==0 || segment=="." || segment==".." || segment.Trim()!=segment) return false;
                        target=Path.Combine(target,segment);if(Reparse(target)) return false;
                    }
                    target=Path.GetFullPath(target);
                    long size=(long)sizeToken;
                    if(size<0 || size>268435456 || (total+=size)>268435456 || !target.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)
                        || !known.Add(target) || !File.Exists(target) || new FileInfo(target).Length!=size) return false;
                    using(var stream=File.OpenRead(target)) using(var hash=SHA256.Create())
                        if(!string.Equals(BitConverter.ToString(hash.ComputeHash(stream)).Replace("-",""),expected,StringComparison.OrdinalIgnoreCase)) return false;
                }
                if(!known.Contains(Path.GetFullPath(executable))) return false;
                // Reject extra executable or library files as well as unexpected content. Never
                // follow a link while checking the helper directory and never inspect profiles.
                var directories=new Queue<string>();directories.Enqueue(root);int files=0,folders=0;
                while(directories.Count>0)
                {
                    string directory=directories.Dequeue();
                    foreach(string child in Directory.GetDirectories(directory))
                    {if(Reparse(child) || ++folders>512) return false;directories.Enqueue(child);}
                    foreach(string file in Directory.GetFiles(directory))
                    {
                        if(Reparse(file) || ++files>513) return false;
                        if(!string.Equals(file,manifestPath,StringComparison.OrdinalIgnoreCase) && !known.Contains(Path.GetFullPath(file))) return false;
                    }
                }
                return files==known.Count+1;
            }
            catch {return false;}
        }
        private static bool Reparse(string path)
        {
            return (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0;
        }
    }
}

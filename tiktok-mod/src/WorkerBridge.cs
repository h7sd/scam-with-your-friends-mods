using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ScamWYF.TikTokPocket
{
    // Only this local helper receives pixels and user input. Neither is persisted or logged.
    internal sealed class WorkerBridge : IDisposable
    {
        internal sealed class Frame
        {
            internal byte[] Pixels;
            internal int Width,Height;
        }
        private const int MaxFrameBytes=8388608,MaxMessageBytes=8192,MaxCommands=128;
        private readonly string executable;
        private readonly bool remember;
        private readonly object gate=new object();
        private readonly Queue<string> commands=new Queue<string>();
        private readonly Queue<string> statuses=new Queue<string>();
        private readonly List<byte[]> buffers=new List<byte[]>();
        private readonly AutoResetEvent commandReady=new AutoResetEvent(false);
        private NamedPipeClientStream control,frames;
        private Process process;
        private Frame pending;
        private volatile bool closed,ready,startupFault;
        internal bool Ready {get {return ready && !closed;}}
        internal WorkerBridge(string executable,bool remember) {this.executable=executable;this.remember=remember;}
        internal void Start(int width,int height)
        {
            var thread=new Thread(()=>Connect(width,height)){IsBackground=true,Name="TikTok browser connection"};thread.Start();
        }
        private void Connect(int width,int height)
        {
            try
            {
                if(closed) return;
                if(!File.Exists(executable)) {Status("helper_invalid");return;}
                if(!WorkerFiles.Verify(executable)) {Status("helper_invalid");return;}
                if(closed) return;
                string pipe="ScamWYF_TikTok_"+Guid.NewGuid().ToString("N");
                var secret=new byte[32];using(var random=new RNGCryptoServiceProvider()) random.GetBytes(secret);
                string nonce=BitConverter.ToString(secret).Replace("-","");
                var info=new ProcessStartInfo(executable,"--pipe "+pipe+" --parent-pid "+Process.GetCurrentProcess().Id)
                {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
                    WorkingDirectory=Path.GetDirectoryName(executable),RedirectStandardInput=true,
                    RedirectStandardOutput=true,RedirectStandardError=true};
                process=Process.Start(info);
                if(process==null) throw new IOException();
                if(closed) return;
                process.OutputDataReceived+=(sender,args)=>{};process.ErrorDataReceived+=(sender,args)=>{};
                process.BeginOutputReadLine();process.BeginErrorReadLine();
                process.StandardInput.WriteLine(nonce);process.StandardInput.Close();
                control=new NamedPipeClientStream(".",pipe+".control",PipeDirection.InOut);
                control.Connect(10000);
                VerifyServer(control,process.Id);
                WriteMessage(control,new JObject{{"type","hello"},{"version",1},{"nonce",nonce}}.ToString(Formatting.None));
                WriteMessage(control,new JObject{{"type","init"},{"width",width},{"height",height},{"remember",remember}}.ToString(Formatting.None));
                frames=new NamedPipeClientStream(".",pipe+".frames",PipeDirection.InOut);
                frames.Connect(10000);
                VerifyServer(frames,process.Id);
                WriteMessage(frames,new JObject{{"type","hello"},{"version",1},{"nonce",nonce}}.ToString(Formatting.None));
                var frameReady=ReadMessage(frames);
                if((string)frameReady["type"]!="ready" || (int?)frameReady["version"]!=1) throw new IOException();
                var controlReady=ReadReady(control);
                if((string)controlReady["type"]!="ready" || (int?)controlReady["version"]!=1) throw new IOException();
                if((int?)controlReady["width"]!=width || (int?)controlReady["height"]!=height) throw new IOException();
                if(closed) return;
                ready=true;Status("ready");
                new Thread(WriteCommands){IsBackground=true,Name="TikTok browser input"}.Start();
                new Thread(ReadControl){IsBackground=true,Name="TikTok browser status"}.Start();
                ReadFrames();
            }
            catch {if(!closed && !startupFault) Status("start_failed");}
            finally {ready=false;CloseTransport();StopProcess();}
        }
        internal void Send(JObject command)
        {
            if(closed || !ready) return;
            string json=command.ToString(Formatting.None);
            if(Encoding.UTF8.GetByteCount(json)>MaxMessageBytes) return;
            bool full=false;lock(gate)
            {
                if(commands.Count>=MaxCommands) {StatusLocked("input_unavailable");full=true;}
                else commands.Enqueue(json);
            }
            if(full) {CloseTransport();return;}
            commandReady.Set();
        }
        private void WriteCommands()
        {
            try
            {
                while(!closed && ready)
                {
                    string next=null;lock(gate) if(commands.Count>0) next=commands.Dequeue();
                    if(next==null) {commandReady.WaitOne(100);continue;}
                    WriteMessage(control,next);
                }
            }
            catch {if(!closed) Status("disconnected");CloseTransport();}
        }
        private void ReadControl()
        {
            try
            {
                while(!closed && ready)
                {
                    var message=ReadMessage(control);
                    if((string)message["type"]=="status") Status((string)message["code"]);
                    else if((string)message["type"]=="probe")
                    {
                        string[] names={"h264","aac","webm","av1","videoPresent"};
                        bool valid=true;foreach(var name in names) if(message[name]==null || message[name].Type!=JTokenType.Boolean) valid=false;
                        var error=message["mediaError"];
                        if(!valid || error==null || error.Type!=JTokenType.Integer || (long)error<0 || (long)error>4) continue;
                        string result="Video compatibility: H.264="+Support((bool)message["h264"])+", AAC="+Support((bool)message["aac"])
                            +", WebM="+Support((bool)message["webm"])+", AV1="+Support((bool)message["av1"])
                            +"; video element="+((bool)message["videoPresent"]?"present":"absent")+"; media error="+(int)error+".";
                        lock(gate) QueueStatusLocked(result);
                    }
                }
            }
            catch {if(!closed) Status("disconnected");CloseTransport();}
        }
        private static string Support(bool value) {return value?"supported":"unavailable";}
        private void ReadFrames()
        {
            long previous=-1;
            var reader=new BinaryReader(frames,Encoding.UTF8,true);
            while(!closed && ready)
            {
                if(reader.ReadInt32()!=0x544B5046 || reader.ReadInt32()!=1) throw new IOException();
                long sequence=reader.ReadInt64();int width=reader.ReadInt32(),height=reader.ReadInt32(),length=reader.ReadInt32();
                if(sequence<=previous || width<1 || height<1 || width>1080 || height>1920
                    || length<1 || length>MaxFrameBytes || (long)width*height*4!=length) throw new IOException();
                previous=sequence;
                byte[] pixels=null;
                lock(gate) for(int i=buffers.Count-1;i>=0;i--) if(buffers[i].Length==length) {pixels=buffers[i];buffers.RemoveAt(i);break;}
                if(pixels==null) pixels=new byte[length];
                ReadExactly(frames,pixels,length);
                lock(gate)
                {
                    if(pending!=null && buffers.Count<2) buffers.Add(pending.Pixels);
                    pending=new Frame {Width=width,Height=height,Pixels=pixels};
                }
            }
        }
        internal Frame TakeFrame() {lock(gate) {var frame=pending;pending=null;return frame;}}
        internal void Release(Frame frame) {lock(gate) if(!closed && buffers.Count<2) buffers.Add(frame.Pixels);}
        internal string TakeStatus() {lock(gate) return statuses.Count==0?null:statuses.Dequeue();}
        private void Status(string value) {lock(gate) StatusLocked(value);}
        private void StatusLocked(string value)
        {
            if(value==null) return;
            switch(value)
            {
                case "runtime_missing":case "profile_error":case "start_failed":case "navigation_failed":case "ready":case "video_unavailable":case "input_unavailable":case "disconnected":case "helper_invalid":break;
                default:return;
            }
            if(!ready && (value=="runtime_missing" || value=="helper_invalid" || value=="profile_error" || value=="start_failed")) startupFault=true;
            QueueStatusLocked(value);
        }
        private void QueueStatusLocked(string value)
        {
            if(statuses.Count>=16) statuses.Dequeue();statuses.Enqueue(value);
        }
        private static void WriteMessage(Stream stream,string json)
        {
            byte[] body=Encoding.UTF8.GetBytes(json);if(body.Length>MaxMessageBytes) throw new IOException();
            byte[] prefix=BitConverter.GetBytes(body.Length);stream.Write(prefix,0,4);stream.Write(body,0,body.Length);stream.Flush();
        }
        private static JObject ReadMessage(Stream stream)
        {
            var prefix=new byte[4];ReadExactly(stream,prefix,4);int length=BitConverter.ToInt32(prefix,0);
            if(length<1 || length>MaxMessageBytes) throw new IOException();
            var body=new byte[length];ReadExactly(stream,body,length);
            return JObject.Parse(Encoding.UTF8.GetString(body));
        }
        private JObject ReadReady(Stream stream)
        {
            for(int i=0;i<16;i++)
            {
                var message=ReadMessage(stream);
                if((string)message["type"]=="ready") return message;
                if((string)message["type"]!="status") throw new IOException();
                Status((string)message["code"]);
            }
            throw new IOException();
        }
        private static void ReadExactly(Stream stream,byte[] buffer,int count)
        {
            int read=0;while(read<count) {int part=stream.Read(buffer,read,count-read);if(part==0) throw new EndOfStreamException();read+=part;}
        }
        [DllImport("kernel32.dll",SetLastError=true)]
        private static extern bool GetNamedPipeServerProcessId(IntPtr pipe,out uint serverProcessId);
        private static void VerifyServer(NamedPipeClientStream pipe,int expected)
        {
            uint actual;
            if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(),out actual) || actual!=(uint)expected) throw new IOException();
        }
        private void CloseTransport()
        {
            ready=false;try {control?.Dispose();} catch {} try {frames?.Dispose();} catch {}
        }
        public void Dispose()
        {
            if(closed) return;closed=true;CloseTransport();commandReady.Set();
            lock(gate) {pending=null;buffers.Clear();commands.Clear();statuses.Clear();}
            StopProcess();
        }
        private void StopProcess()
        {
            Process child;lock(gate) {child=process;process=null;}
            if(child!=null) new Thread(()=>{try {if(!child.WaitForExit(7000)) child.Kill();} catch {} finally {child.Dispose();}}){IsBackground=true,Name="TikTok browser shutdown"}.Start();
        }
    }
}

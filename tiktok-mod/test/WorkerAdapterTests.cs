using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ScamWYF.TikTokPocket;

internal static class WorkerAdapterTests
{
    private static Action<bool,string> check;
    internal static void Run(Action<bool,string> assertion,string workspace)
    {
        check=assertion;
        TestMessages();TestStartupReady();TestStatuses();TestFrames();TestQueue();TestWorkerManifest(workspace);
    }
    private static MethodInfo Method(string name) {return AccessTools.Method(typeof(WorkerBridge),name);}
    private static void Set(WorkerBridge bridge,string name,object value) {AccessTools.Field(typeof(WorkerBridge),name).SetValue(bridge,value);}
    private static Exception Invoke(MethodInfo method,object target,params object[] args)
    {
        try {method.Invoke(target,args);return null;}
        catch(TargetInvocationException ex) {return ex.InnerException;}
    }
    private static byte[] Packet(string text)
    {
        var bytes=Encoding.UTF8.GetBytes(text);return Concat(BitConverter.GetBytes(bytes.Length),bytes);
    }
    private static byte[] Concat(params byte[][] parts)
    {
        using(var output=new MemoryStream()) {foreach(var bytes in parts) output.Write(bytes,0,bytes.Length);return output.ToArray();}
    }
    private static void TestMessages()
    {
        using(var input=new MemoryStream(Packet("{\"type\":\"ready\",\"version\":1}")))
        {
            var result=(JObject)Method("ReadMessage").Invoke(null,new object[]{input});
            check((string)result["type"]=="ready" && (int)result["version"]==1,"actual bridge JSON parser reads a framed ready message");
        }
        foreach(int length in new[]{-1,0,8193,int.MaxValue})
            using(var input=new MemoryStream(BitConverter.GetBytes(length)))
                check(Invoke(Method("ReadMessage"),null,input) is IOException,"bridge rejects invalid message length "+length);
        foreach(byte[] bytes in new[]{new byte[]{1,2},Concat(BitConverter.GetBytes(5),new byte[]{123}),Packet("not JSON"),Packet("[]")})
            using(var input=new MemoryStream(bytes))
                check(Invoke(Method("ReadMessage"),null,input)!=null,"bridge rejects truncated or non-object JSON");
        string maximum="{\"x\":\""+new string('a',8184)+"\"}";
        using(var input=new MemoryStream(Packet(maximum)))
            check(Invoke(Method("ReadMessage"),null,input)==null,"bridge accepts JSON at its exact 8192-byte boundary");
        using(var output=new MemoryStream())
        {
            check(Invoke(Method("WriteMessage"),null,output,new string('a',8193)) is IOException && output.Length==0,
                "oversized outgoing message is rejected before writing its header");
            check(Invoke(Method("WriteMessage"),null,output,"{\"text\":\"Grüße\"}")==null,
                "bounded UTF-8 text is written using the real bridge encoder");
            output.Position=0;var result=(JObject)Method("ReadMessage").Invoke(null,new object[]{output});
            check((string)result["text"]=="Grüße","UTF-8 control message round trips without losing characters");
        }
    }
    private static void TestStatuses()
    {
        using(var bridge=new WorkerBridge("unused",true))
        {
            Method("Status").Invoke(bridge,new object[]{"ready"});
            Method("Status").Invoke(bridge,new object[]{"https://private.invalid/account"});
            Method("Status").Invoke(bridge,new object[]{"Video compatibility: untrusted arbitrary text"});
            check(bridge.TakeStatus()=="ready" && bridge.TakeStatus()==null,"status allowlist drops arbitrary and forged diagnostic strings");
            for(int i=0;i<20;i++) Method("Status").Invoke(bridge,new object[]{"ready"});
            int count=0;while(bridge.TakeStatus()!=null) count++;
            check(count==16,"status queue retains at most sixteen entries");
        }
        string valid="{\"type\":\"probe\",\"h264\":true,\"aac\":true,\"webm\":false,\"av1\":false,\"videoPresent\":true,\"mediaError\":0}";
        using(var bridge=new WorkerBridge("unused",true))
        {
            ReadPipe(bridge,"control","ReadControl",Packet(valid));
            string value=bridge.TakeStatus();
            check(value!=null && value.StartsWith("Video compatibility: H.264=supported, AAC=supported",StringComparison.Ordinal)
                && value.EndsWith("media error=0.",StringComparison.Ordinal),"real control parser generates a bounded diagnostic from typed values");
        }
        foreach(string invalid in new[]{valid.Replace("\"h264\":true","\"h264\":\"secret\""),valid.Replace("\"mediaError\":0","\"mediaError\":5"),
            valid.Replace("\"mediaError\":0","\"mediaError\":-1"),valid.Replace("\"mediaError\":0","\"mediaError\":1.5")})
            using(var bridge=new WorkerBridge("unused",true))
            {
                ReadPipe(bridge,"control","ReadControl",Packet(invalid));
                bool diagnostic=false;string value;while((value=bridge.TakeStatus())!=null) diagnostic|=value.StartsWith("Video compatibility:",StringComparison.Ordinal);
                check(!diagnostic,"invalid probe types or media errors cannot generate diagnostic text");
            }
    }
    private static void TestStartupReady()
    {
        using(var bridge=new WorkerBridge("unused",true)) using(var input=new MemoryStream(Concat(Packet("{\"type\":\"status\",\"code\":\"profile_error\"}"),Packet("{\"type\":\"ready\",\"version\":1,\"width\":480,\"height\":854}"))))
        {
            var ready=(JObject)Method("ReadReady").Invoke(bridge,new object[]{input});
            check((string)ready["type"]=="ready" && bridge.TakeStatus()=="profile_error","startup parser preserves fixed worker status before the canonical ready message");
        }
        using(var bridge=new WorkerBridge("unused",true)) using(var input=new MemoryStream(Packet("{\"type\":\"arbitrary\"}")))
            check(Invoke(Method("ReadReady"),bridge,input) is IOException,"startup parser rejects a non-status/non-ready message");
        using(var bridge=new WorkerBridge("unused",true)) using(var input=new MemoryStream())
        {
            byte[] status=Packet("{\"type\":\"status\",\"code\":\"ready\"}");for(int i=0;i<16;i++) input.Write(status,0,status.Length);input.Position=0;
            check(Invoke(Method("ReadReady"),bridge,input) is IOException,"startup status messages cannot bypass the sixteen-message limit");
        }
        string name="ScamWYF_TikTok_ServerTest_"+Guid.NewGuid().ToString("N");
        using(var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous))
        using(var client=new NamedPipeClientStream(".",name,PipeDirection.InOut))
        {
            var connected=server.BeginWaitForConnection(null,null);client.Connect(2000);server.EndWaitForConnection(connected);
            int pid=System.Diagnostics.Process.GetCurrentProcess().Id;
            check(Invoke(Method("VerifyServer"),null,client,pid)==null,"actual Windows pipe-server PID check accepts the owned test server");
            check(Invoke(Method("VerifyServer"),null,client,pid+1) is IOException,"actual Windows pipe-server PID check rejects a different expected process");
        }
    }
    private static byte[] Frame(long sequence,int width,int height,int length,byte[] body,int magic=0x544B5046,int version=1)
    {
        using(var output=new MemoryStream()) using(var writer=new BinaryWriter(output))
        {writer.Write(magic);writer.Write(version);writer.Write(sequence);writer.Write(width);writer.Write(height);writer.Write(length);writer.Write(body);return output.ToArray();}
    }
    private static void TestFrames()
    {
        byte[] pixels=new byte[48];for(int i=0;i<pixels.Length;i++) pixels[i]=(byte)i;
        using(var bridge=new WorkerBridge("unused",true))
        {
            Exception end=ReadPipe(bridge,"frames","ReadFrames",Frame(1,4,3,48,pixels));
            var frame=bridge.TakeFrame();
            check(end is EndOfStreamException && frame!=null && frame.Width==4 && frame.Height==3 && frame.Pixels[47]==47,
                "real bridge frame parser preserves BGRA bytes and dimensions");
            check(bridge.TakeFrame()==null,"taking a frame consumes the pending slot exactly once");
            bridge.Release(frame);
            check(((IList)AccessTools.Field(typeof(WorkerBridge),"buffers").GetValue(bridge)).Count==1,"released frame becomes available for bounded reuse");
        }
        byte[][] malformed={
            Frame(1,4,3,48,pixels,0),Frame(1,4,3,48,pixels,0x544B5046,2),Frame(-1,4,3,48,pixels),
            Frame(1,0,3,48,pixels),Frame(1,1081,3,48,pixels),Frame(1,4,1921,48,pixels),
            Frame(1,int.MaxValue,int.MaxValue,4,new byte[4]),Frame(1,4,3,47,pixels),
            Frame(1,4,3,-1,pixels),Frame(1,4,3,8388609,new byte[0]),Frame(1,4,3,48,new byte[1]),new byte[5]
        };
        foreach(byte[] bytes in malformed) using(var bridge=new WorkerBridge("unused",true))
        {
            Exception failure=ReadPipe(bridge,"frames","ReadFrames",bytes);
            check(failure is IOException && bridge.TakeFrame()==null,"malformed/overflowing/truncated frame is rejected before publication");
        }
        foreach(long next in new[]{1L,0L}) using(var bridge=new WorkerBridge("unused",true))
        {
            Exception failure=ReadPipe(bridge,"frames","ReadFrames",Concat(Frame(1,4,3,48,pixels),Frame(next,4,3,48,new byte[48])));
            var frame=bridge.TakeFrame();
            check(failure is IOException && frame!=null && frame.Pixels[47]==47,"duplicate or decreasing frame sequence cannot overwrite an accepted frame");
        }
        using(var bridge=new WorkerBridge("unused",true))
        {
            ReadPipe(bridge,"frames","ReadFrames",Concat(Frame(1,4,3,48,pixels),Frame(2,2,2,16,new byte[16])));
            var frame=bridge.TakeFrame();
            check(frame!=null && frame.Width==2 && frame.Height==2 && frame.Pixels.Length==16,"latest valid frame replaces an older pending frame, including resize");
            check(((IList)AccessTools.Field(typeof(WorkerBridge),"buffers").GetValue(bridge)).Count<=2,"superseded frame buffer pool stays bounded");
        }
        using(var bridge=new WorkerBridge("unused",true))
        {
            ReadPipe(bridge,"frames","ReadFrames",Frame(1,4,3,48,pixels));var held=bridge.TakeFrame();
            byte[] newer=new byte[48];for(int i=0;i<newer.Length;i++) newer[i]=99;
            ReadPipe(bridge,"frames","ReadFrames",Frame(2,4,3,48,newer));var second=bridge.TakeFrame();
            check(!ReferenceEquals(held.Pixels,second.Pixels) && held.Pixels[47]==47 && second.Pixels[47]==99,
                "an uploaded/taken frame cannot be reused or overwritten before explicit Release");
            bridge.Release(held);ReadPipe(bridge,"frames","ReadFrames",Frame(3,4,3,48,new byte[48]));var third=bridge.TakeFrame();
            check(ReferenceEquals(held.Pixels,third.Pixels) && second.Pixels[47]==99,
                "real frame parser reuses only released memory and leaves other held frames unchanged");
        }
    }
    private static Exception ReadPipe(WorkerBridge bridge,string field,string reader,byte[] bytes)
    {
        string name="ScamWYF_TikTok_Test_"+Guid.NewGuid().ToString("N");
        using(var server=new NamedPipeServerStream(name,PipeDirection.Out,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous))
        using(var client=new NamedPipeClientStream(".",name,PipeDirection.In))
        {
            Exception writeFailure=null;
            var thread=new Thread(()=>{try{server.WaitForConnection();server.Write(bytes,0,bytes.Length);server.Flush();}catch(IOException){}catch(Exception ex){writeFailure=ex;}finally{server.Dispose();}}){IsBackground=true};
            thread.Start();client.Connect(2000);Set(bridge,field,client);Set(bridge,"ready",true);
            Exception result=Invoke(Method(reader),bridge);
            client.Dispose();
            if(!thread.Join(2000)) throw new TimeoutException("Isolated pipe writer did not stop.");
            if(writeFailure!=null) throw writeFailure;
            return result;
        }
    }
    private static void TestQueue()
    {
        using(var bridge=new WorkerBridge("unused",true))
        {
            bridge.Send(new JObject{{"type","refresh"}});
            var queue=(ICollection)AccessTools.Field(typeof(WorkerBridge),"commands").GetValue(bridge);
            check(queue.Count==0,"bridge drops commands until its authenticated connection is ready");
            Set(bridge,"ready",true);bridge.Send(new JObject{{"type","text"},{"text",new string('a',9000)}});
            check(queue.Count==0,"oversized input is never queued");
            for(int i=0;i<129;i++) bridge.Send(new JObject{{"type","refresh"}});
            check(queue.Count==128 && !bridge.Ready && bridge.TakeStatus()=="input_unavailable","input queue overflow disables transport instead of silently dropping ordered key events");
            bridge.Dispose();
            check(queue.Count==0 && bridge.TakeFrame()==null && bridge.TakeStatus()==null,"dispose clears pending control, frames and statuses");
        }
    }
    private static void TestWorkerManifest(string workspace)
    {
        string root=Path.Combine(workspace,"build","tiktok-tests","manifest-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string executable=Path.Combine(root,"ScamWYF.TikTokBrowser.exe"),manifestPath=Path.Combine(root,"worker-files.json");
        byte[] contents=Encoding.UTF8.GetBytes("non-executable isolated manifest fixture");File.WriteAllBytes(executable,contents);
        string hash;using(var algorithm=SHA256.Create()) hash=BitConverter.ToString(algorithm.ComputeHash(contents)).Replace("-","");
        var entry=new JObject{{"path","ScamWYF.TikTokBrowser.exe"},{"size",contents.Length},{"sha256",hash}};
        var manifest=new JObject{{"schemaVersion",1},{"appVersion","1.0.1"},{"executable","ScamWYF.TikTokBrowser.exe"},{"files",new JArray(entry)}};
        Action<JObject> write=value=>File.WriteAllText(manifestPath,value.ToString(Formatting.None));
        write(manifest);check(WorkerFiles.Verify(executable),"actual manifest verifier accepts an exact versioned hash/size inventory");
        File.WriteAllText(Path.Combine(root,"extra.dll"),"fixture");
        check(!WorkerFiles.Verify(executable),"unlisted extra helper library is rejected");File.Delete(Path.Combine(root,"extra.dll"));
        var mutations=new List<Action<JObject>> {
            value=>value["appVersion"]="1.0.0",value=>value["schemaVersion"]=2,value=>value["executable"]="other.exe",
            value=>((JArray)value["files"]).Add(entry.DeepClone()),value=>value["files"]=new JArray(),
            value=>FirstEntry(value)["path"]="../ScamWYF.TikTokBrowser.exe",
            value=>FirstEntry(value)["path"]="C:/ScamWYF.TikTokBrowser.exe",
            value=>FirstEntry(value)["path"]="ScamWYF.TikTokBrowser.exe:stream",
            value=>FirstEntry(value)["path"]="./ScamWYF.TikTokBrowser.exe",
            value=>FirstEntry(value)["path"]=" ScamWYF.TikTokBrowser.exe",
            value=>FirstEntry(value)["size"]=-1,value=>FirstEntry(value)["size"]=268435457,
            value=>FirstEntry(value)["size"]="40",value=>FirstEntry(value)["size"]=contents.Length+1,
            value=>FirstEntry(value)["sha256"]=new string('g',64),value=>FirstEntry(value)["sha256"]=new string('0',64)
        };
        foreach(var mutate in mutations){var altered=(JObject)manifest.DeepClone();mutate(altered);write(altered);check(!WorkerFiles.Verify(executable),"invalid worker metadata, inventory, path, size or hash is rejected");}
        File.WriteAllText(manifestPath,new string('x',1048577));check(!WorkerFiles.Verify(executable),"oversized manifest is rejected before JSON parsing");
        write(manifest);File.WriteAllText(executable,"tampered fixture");check(!WorkerFiles.Verify(executable),"modified helper bytes fail manifest verification");
        // Test files remain only below ignored build/tiktok-tests; no executable is launched.
    }
    private static JObject FirstEntry(JObject manifest) {return (JObject)manifest["files"][0];}
}

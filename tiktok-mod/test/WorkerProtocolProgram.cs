using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using ScamWYF.TikTokBrowser;

internal static class WorkerProtocolProgram
{
    static int passed,failed;
    static void Check(bool result,string name) {if(result) passed++;else {failed++;Console.WriteLine("FAIL: "+name);}}
    static JsonDocument Json(string value) => JsonDocument.Parse(value);
    static async Task<bool> Rejects(byte[] packet)
    {
        try {using var stream=new MemoryStream(packet);using var document=await Protocol.ReadAsync(stream,CancellationToken.None);return false;}
        catch(Exception ex) when(ex is InvalidDataException or JsonException or EndOfStreamException) {return true;}
    }
    static byte[] Packet(string value)
    {
        byte[] data=Encoding.UTF8.GetBytes(value),output=new byte[data.Length+4];BinaryPrimitives.WriteInt32LittleEndian(output,data.Length);data.CopyTo(output,4);return output;
    }
    static async Task<int> Main()
    {
        Check(Protocol.ValidPipeName("ScamWYF_TikTok_"+Guid.NewGuid().ToString("N")),"actual corrected adapter pipe-name pattern is accepted by the production worker");
        foreach(string name in new[]{"ScamWYF.TikTok.test","short","../invalidname",new string('a',81),"validname\n"})
            Check(!Protocol.ValidPipeName(name),"unsafe or out-of-bounds pipe name rejected");
        foreach(var size in new[]{(64,64),(480,854),(1080,1920)}) Check(Protocol.ValidSize(size.Item1,size.Item2),"supported production frame size accepted");
        foreach(var size in new[]{(63,64),(64,63),(1081,100),(100,1921),(int.MaxValue,int.MaxValue),(-1,854)})
            Check(!Protocol.ValidSize(size.Item1,size.Item2),"unsupported or overflowing frame size rejected");
        foreach(int length in new[]{-1,0,1,8193,int.MaxValue})
        {byte[] prefix=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(prefix,length);Check(await Rejects(prefix),"invalid worker message length rejected");}
        foreach(byte[] bytes in new[]{new byte[]{1},new byte[]{5,0,0,0,123},Packet("[]"),Packet("true"),Packet("null"),Packet("not json"),Packet("{\"x\":"+new string('[',9)+"0"+new string(']',9)+"}")})
            Check(await Rejects(bytes),"truncated, non-object or over-deep worker JSON rejected");
        using(var stream=new MemoryStream(Packet("{\"type\":\"init\",\"width\":480,\"height\":854,\"remember\":true}")))
        using(var document=await Protocol.ReadAsync(stream,CancellationToken.None))
        {
            var root=document.RootElement;
            Check(Protocol.Text(root,"type",out var type) && type=="init" && Protocol.Integer(root,"width",out int width) && width==480
                && Protocol.Boolean(root,"remember",out bool remember) && remember,"real worker decoder preserves typed init fields");
        }
        using(var stream=new MemoryStream())
        {
            await Protocol.WriteAsync(stream,new {type="text",text="Grüße 你好"},CancellationToken.None);stream.Position=0;
            using var document=await Protocol.ReadAsync(stream,CancellationToken.None);
            Check(Protocol.Text(document.RootElement,"text",out var text) && text=="Grüße 你好","actual worker encoder/decoder round-trip multilingual Unicode");
        }
        using(var stream=new MemoryStream())
        {
            bool rejected=false;try{await Protocol.WriteAsync(stream,new {text=new string('a',9000)},CancellationToken.None);}catch(InvalidDataException){rejected=true;}
            Check(rejected && stream.Length==0,"worker rejects oversized output before writing a prefix");
        }
        foreach(string value in new[]{"\"1\"","true","null","1.5","2147483648"}) using(var document=Json("{\"v\":"+value+"}"))
            Check(!Protocol.Integer(document.RootElement,"v",out _),"integer extractor rejects strings/booleans/decimal/overflow without throwing");
        foreach(string value in new[]{"1","\"true\"","null"}) using(var document=Json("{\"v\":"+value+"}"))
            Check(!Protocol.Boolean(document.RootElement,"v",out _),"boolean extractor rejects wrong primitive types");
        byte[] nonce=Enumerable.Range(0,32).Select(value=>(byte)value).ToArray();string hex=Convert.ToHexString(nonce);
        string hello="{\"type\":\"hello\",\"version\":1,\"nonce\":\""+hex+"\"}";
        using(var document=Json(hello)) Check(Protocol.ValidateHello(document.RootElement,nonce),"production authentication accepts the exact thirty-two-byte secret");
        using(var document=Json(hello.Replace(hex,hex.ToLowerInvariant()))) Check(Protocol.ValidateHello(document.RootElement,nonce),"hex casing does not change nonce authentication");
        foreach(string invalid in new[]{hello.Replace(hex,new string('0',64)),hello.Replace(hex,new string('g',64)),hello.Replace(hex,hex.Substring(0,62)),
            hello.Replace("\"version\":1","\"version\":2"),hello.Replace("\"version\":1","\"version\":\"1\""),hello.Replace("\"type\":\"hello\"","\"type\":\"init\""),"[]","{}"})
            using(var document=Json(invalid)) Check(!Protocol.ValidateHello(document.RootElement,nonce),"wrong nonce, schema or protocol version cannot authenticate");
        using(var document=Json(hello)) Check(!Protocol.ValidateHello(document.RootElement,new byte[31]),"authentication rejects an invalid expected secret length");
        using(var document=Json(hello)) Check(Protocol.ValidateHello(document.RootElement,nonce) && nonce[31]==31,"nonce comparison does not mutate the caller's expected secret");
        var validCommands=new[]{"{\"type\":\"back\"}","{\"type\":\"refresh\"}","{\"type\":\"probe\"}","{\"type\":\"close\"}","{\"type\":\"leave\"}",
            "{\"type\":\"show\",\"visible\":false}","{\"type\":\"focus\",\"active\":true}","{\"type\":\"resize\",\"width\":480,\"height\":854}",
            "{\"type\":\"text\",\"text\":\"Grüße\"}","{\"type\":\"keyDown\",\"vk\":13,\"modifiers\":15}","{\"type\":\"keyUp\",\"vk\":255,\"modifiers\":0}",
            "{\"type\":\"move\",\"x\":0,\"y\":0}","{\"type\":\"down\",\"x\":479,\"y\":853,\"button\":\"left\"}","{\"type\":\"up\",\"x\":10,\"y\":10,\"button\":\"right\"}",
            "{\"type\":\"wheel\",\"x\":10,\"y\":10,\"delta\":-1200}"};
        foreach(string command in validCommands) using(var document=Json(command)) Check(Protocol.ValidateCommand(document.RootElement,480,854),"actual command allowlist accepts bounded supported user input");
        var invalidCommands=new[]{"{\"type\":\"navigate\",\"url\":\"https://private.invalid/\"}","{\"type\":\"executeScript\",\"script\":\"document.cookie\"}","{\"type\":\"launch\"}","{\"type\":\"init\"}",
            "{\"type\":\"show\",\"visible\":1}","{\"type\":\"focus\",\"active\":\"true\"}","{\"type\":\"resize\",\"width\":2147483647,\"height\":2147483647}",
            "{\"type\":\"keyDown\",\"vk\":256,\"modifiers\":0}","{\"type\":\"keyUp\",\"vk\":13,\"modifiers\":16}","{\"type\":\"keyDown\",\"vk\":-1,\"modifiers\":0}",
            "{\"type\":\"move\",\"x\":480,\"y\":0}","{\"type\":\"move\",\"x\":0,\"y\":854}","{\"type\":\"down\",\"x\":0,\"y\":0,\"button\":\"unknown\"}",
            "{\"type\":\"wheel\",\"x\":0,\"y\":0,\"delta\":1201}","{\"type\":\"text\",\"text\":\"bad\\u0000input\"}","{\"type\":\"text\",\"text\":\""+new string('a',257)+"\"}","[]","{}"};
        foreach(string command in invalidCommands) using(var document=Json(command)) Check(!Protocol.ValidateCommand(document.RootElement,480,854),"unsupported operations and malformed/out-of-bounds input are rejected");
        Console.WriteLine($"TikTok worker protocol tests: {passed} passed, {failed} failed");return failed==0?0:1;
    }
}

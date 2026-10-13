using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace ScamWYF.TikTokBrowser;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        using var context = new ApplicationContext();
        using var timer = new System.Windows.Forms.Timer { Interval = 1 };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            try { await Worker.RunAsync(args); }
            catch { Environment.ExitCode = 1; }
            finally { context.ExitThread(); }
        };
        timer.Start(); Application.Run(context);
    }
}

internal sealed class Worker
{
    readonly NamedPipeServerStream control, frames;
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim controlLock = new(1, 1);
    readonly TaskCompletionSource framesReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Channel<PackedFrame> queue = Channel.CreateBounded<PackedFrame>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true }, frame => frame.Dispose());
    readonly byte[] nonce;
    readonly string? proofProfile;
    Renderer? renderer;

    Worker(string pipeBase, byte[] nonce, string? proofProfile)
    {
        this.nonce = nonce; this.proofProfile = proofProfile;
        control = new NamedPipeServerStream(pipeBase + ".control", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        frames = new NamedPipeServerStream(pipeBase + ".frames", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    public static async Task RunAsync(string[] args)
    {
        string? pipeBase = null; int parentPid = 0; bool proof = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--pipe" && ++i < args.Length) pipeBase = args[i];
            else if (args[i] == "--parent-pid" && ++i < args.Length) int.TryParse(args[i], out parentPid);
            else if (args[i] == "--proof") proof = true;
            else throw new ArgumentException();
        }
        if (pipeBase == null || !Protocol.ValidPipeName(pipeBase) || parentPid <= 0 || parentPid == Environment.ProcessId) throw new ArgumentException();
        string? line = await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if (line == null || line.Length != 64 || !Regex.IsMatch(line, "^[0-9A-Fa-f]{64}$")) throw new ArgumentException();
        byte[] nonce = Convert.FromHexString(line);
        using var parent = Process.GetProcessById(parentPid);
        string? profile = null;
        if (proof)
        {
            // Only copied proof binaries under this explicit build folder may enable the fake page.
            string current = Path.GetFullPath(AppContext.BaseDirectory);
            string marker = Path.DirectorySeparatorChar + "build" + Path.DirectorySeparatorChar + "tiktok-webview2-proof" + Path.DirectorySeparatorChar;
            int markerIndex = current.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0) throw new ArgumentException();
            string proofRoot = current[..(markerIndex + marker.Length)];
            string run = Path.Combine(proofRoot, "worker-runs", Guid.NewGuid().ToString("N"));
            profile = Path.GetFullPath(Path.Combine(run, "profile"));
            if (!profile.StartsWith(proofRoot, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException();
        }
        var worker = new Worker(pipeBase, nonce, profile);
        try
        {
            var frameTask = worker.FrameLoopAsync();
            var controlTask = worker.ControlLoopAsync();
            var parentExit = parent.WaitForExitAsync(worker.stop.Token);
            await Task.WhenAny(frameTask, controlTask, parentExit);
            worker.stop.Cancel(); worker.queue.Writer.TryComplete();
            try { await Task.WhenAll(frameTask, controlTask); } catch (OperationCanceledException) { } catch (IOException) { }
        }
        finally
        {
            worker.stop.Cancel(); worker.queue.Writer.TryComplete();
            if (worker.renderer != null) await worker.renderer.DisposeAsync();
            while (worker.queue.Reader.TryRead(out var frame)) frame.Dispose();
            worker.control.Dispose(); worker.frames.Dispose(); worker.controlLock.Dispose(); worker.stop.Dispose();
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    async Task AuthenticateAsync(NamedPipeServerStream stream)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await stream.WaitForConnectionAsync(timeout.Token);
        using var message = await Protocol.ReadAsync(stream, timeout.Token);
        var root = message.RootElement;
        if (!Protocol.ValidateHello(root, nonce)) throw new InvalidDataException();
    }

    async Task ControlLoopAsync()
    {
        await AuthenticateAsync(control);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var init = await Protocol.ReadAsync(control, timeout.Token);
        var first = init.RootElement;
        if (!Protocol.Text(first, "type", out var type) || type != "init" || !Protocol.Integer(first, "width", out int width) || !Protocol.Integer(first, "height", out int height)
            || !Renderer.ValidSize(width, height) || !Protocol.Boolean(first, "remember", out bool remember)) throw new InvalidDataException();
        await framesReady.Task.WaitAsync(timeout.Token);
        renderer = new Renderer();
        renderer.Frame += frame => { if (!queue.Writer.TryWrite(frame)) frame.Dispose(); };
        renderer.Status += code => _ = SendStatusAsync(code);
        try { await renderer.InitializeAsync(width, height, remember, proofProfile); }
        catch (RendererFailure e) { await SendStatusAsync(e.Code); return; }
        catch { await SendStatusAsync("start_failed"); return; }
        await SendAsync(new { type = "ready", version = Protocol.Version, width, height });
        while (!stop.IsCancellationRequested)
        {
            using var document = await Protocol.ReadAsync(control, stop.Token);
            var root = document.RootElement;
            if (!Protocol.ValidateCommand(root, width, height) || !Protocol.Text(root, "type", out type)) continue;
            try
            {
                switch (type)
                {
                    case "close": return;
                    case "show": if (Protocol.Boolean(root, "visible", out bool visible)) renderer.Show(visible); break;
                    case "focus": if (Protocol.Boolean(root, "active", out bool active)) await renderer.FocusAsync(active); break;
                    case "leave": renderer.Leave(); break;
                    case "resize": if (Protocol.Integer(root, "width", out width) && Protocol.Integer(root, "height", out height) && Renderer.ValidSize(width, height)) renderer.Resize(width, height); break;
                    case "back": renderer.Back(); break;
                    case "refresh": renderer.Refresh(); break;
                    case "probe": await SendAsync(await renderer.ProbeAsync()); break;
                    case "text": if (Protocol.Text(root, "text", out var text) && text.Length <= 256) await renderer.TextAsync(text); break;
                    case "keyDown": case "keyUp":
                        if (Protocol.Integer(root, "vk", out int vk) && Protocol.Integer(root, "modifiers", out int modifiers)) await renderer.KeyAsync(type == "keyDown", vk, modifiers); break;
                    case "move": case "down": case "up": case "wheel":
                        if (!Protocol.Integer(root, "x", out int x) || !Protocol.Integer(root, "y", out int y)) break;
                        Protocol.Text(root, "button", out var button); Protocol.Integer(root, "delta", out int delta);
                        renderer.Mouse(type, x, y, button, delta); break;
                }
            }
            catch (RendererFailure e) { await SendStatusAsync(e.Code); }
            catch { await SendStatusAsync("start_failed"); }
        }
    }

    async Task FrameLoopAsync()
    {
        await AuthenticateAsync(frames);
        await Protocol.WriteAsync(frames, new { type = "ready", version = Protocol.Version }, stop.Token);
        framesReady.TrySetResult();
        byte[] header = new byte[28];
        await foreach (var frame in queue.Reader.ReadAllAsync(stop.Token))
        {
            using (frame)
            {
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), Protocol.Magic);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), Protocol.Version);
                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8, 8), frame.Sequence);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), frame.Width);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20, 4), frame.Height);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24, 4), frame.Length);
                await frames.WriteAsync(header, stop.Token); await frames.WriteAsync(frame.Bytes.AsMemory(0, frame.Length), stop.Token); await frames.FlushAsync(stop.Token);
            }
        }
    }
    async Task SendStatusAsync(string code)
    {
        if (code is not ("runtime_missing" or "profile_error" or "start_failed" or "navigation_failed" or "ready" or "video_unavailable")) return;
        try { await SendAsync(new { type = "status", code }); } catch (IOException) { } catch (OperationCanceledException) { } catch (ObjectDisposedException) { }
    }
    async Task SendAsync(object value)
    {
        await controlLock.WaitAsync(stop.Token);
        try { await Protocol.WriteAsync(control, value, stop.Token); }
        finally { controlLock.Release(); }
    }
}

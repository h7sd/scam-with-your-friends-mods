using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.UI.Composition;
using WinRT;

namespace ScamWYF.TikTokBrowser;

internal sealed class PackedFrame : IDisposable
{
    public required byte[] Bytes;
    public required int Width, Height, Length;
    public required long Sequence;
    public void Dispose() => ArrayPool<byte>.Shared.Return(Bytes);
}

internal sealed class Renderer : IAsyncDisposable
{
    const string Home = "https://www.tiktok.com/";
    readonly HiddenWindow window = new();
    readonly Stopwatch frameClock = Stopwatch.StartNew();
    CoreWebView2CompositionController? controller;
    CoreWebView2Environment? environment;
    Compositor? compositor;
    ContainerVisual? visual;
    ID3D11Device? device;
    ID3D11DeviceContext? deviceContext;
    IDirect3DDevice? captureDevice;
    Direct3D11CaptureFramePool? pool;
    GraphicsCaptureSession? capture;
    IntPtr dispatcher;
    string? disposableProfile;
    string? disposableRoot;
    int width, height;
    long sequence, previousFrameMs = -100;
    bool disposed, active = true, visible = true;
    bool isProof;
    bool navigationBlocked, navigationSucceeded;
    CoreWebView2MouseEventVirtualKeys heldButtons;
    Point lastPoint;
    readonly Dictionary<int, int> heldKeys = new();
    public event Action<PackedFrame>? Frame;
    public event Action<string>? Status;

    public async Task InitializeAsync(int requestedWidth, int requestedHeight, bool remember, string? proofProfile = null)
    {
        if (!ValidSize(requestedWidth, requestedHeight) || !GraphicsCaptureSession.IsSupported()) throw new RendererFailure("start_failed");
        width = requestedWidth; height = requestedHeight;
        isProof = proofProfile != null;
        string profile;
        if (proofProfile != null)
        {
            // Test callers are restricted by Worker to a fresh proof run below the workspace build folder.
            profile = proofProfile;
            disposableProfile = proofProfile;
            disposableRoot = Path.GetDirectoryName(Path.GetFullPath(proofProfile));
        }
        else if (remember)
            profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScamWYF-Mods", "TikTokPocket", "WebView2Profile");
        else
        {
            profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScamWYF-Mods", "TikTokPocket", "WebView2Guest", Guid.NewGuid().ToString("N"));
            disposableProfile = profile;
            disposableRoot = Path.GetDirectoryName(profile);
        }
        try { Directory.CreateDirectory(profile); }
        catch { throw new RendererFailure("profile_error"); }

        var queueOptions = new Native.DispatcherQueueOptions { Size = Marshal.SizeOf<Native.DispatcherQueueOptions>(), ThreadType = 2, ApartmentType = 2 };
        Marshal.ThrowExceptionForHR(Native.CreateDispatcherQueueController(queueOptions, out dispatcher));
        compositor = new Compositor(); visual = compositor.CreateContainerVisual();
        visual.Size = new Vector2(width, height); window.MakeHandle(width, height);
        var options = new CoreWebView2EnvironmentOptions { AllowSingleSignOnUsingOSPrimaryAccount = false, ExclusiveUserDataFolderAccess = true };
        try { environment = await CoreWebView2Environment.CreateAsync(null, profile, options); }
        catch (WebView2RuntimeNotFoundException) { throw new RendererFailure("runtime_missing"); }
        var controllerOptions = environment.CreateCoreWebView2ControllerOptions();
        controllerOptions.IsInPrivateModeEnabled = !remember;
        controller = await environment.CreateCoreWebView2CompositionControllerAsync(window.Handle, controllerOptions);
        controller.Bounds = new Rectangle(0, 0, width, height);
        controller.RootVisualTarget = visual; controller.IsVisible = true;
        var core = controller.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (e.IsUserInitiated && Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme == "https") core.Navigate(e.Uri);
        };
        core.NavigationStarting += (_, e) =>
        {
            if (e.Uri == "about:blank" || isProof && e.Uri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase)) return;
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            { e.Cancel = true; navigationBlocked = true; Status?.Invoke("navigation_failed"); }
        };
        core.NavigationCompleted += (_, e) => { navigationSucceeded = e.IsSuccess; if (!e.IsSuccess) Status?.Invoke("navigation_failed"); };
        core.ProcessFailed += (_, _) => Status?.Invoke("start_failed");

        D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, new[] { FeatureLevel.Level_11_0 }, out device, out deviceContext).CheckError();
        using (var dxgi = device.QueryInterface<IDXGIDevice>())
        {
            Marshal.ThrowExceptionForHR(Native.CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var pointer));
            try { captureDevice = MarshalInterface<IDirect3DDevice>.FromAbi(pointer); }
            finally { Marshal.Release(pointer); }
        }
        var item = GraphicsCaptureItem.CreateFromVisual(visual);
        pool = Direct3D11CaptureFramePool.Create(captureDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, new Windows.Graphics.SizeInt32 { Width = width, Height = height });
        pool.FrameArrived += CaptureFrame;
        capture = pool.CreateCaptureSession(item); capture.IsCursorCaptureEnabled = false; capture.StartCapture();
        if (proofProfile != null) core.NavigateToString(ProofPage); else core.Navigate(Home);
    }

    public static bool ValidSize(int width, int height) => Protocol.ValidSize(width, height);

    public void Resize(int newWidth, int newHeight)
    {
        if (!ValidSize(newWidth, newHeight) || controller == null || pool == null || visual == null || captureDevice == null) throw new RendererFailure("start_failed");
        width = newWidth; height = newHeight;
        controller.Bounds = new Rectangle(0, 0, width, height); visual.Size = new Vector2(width, height);
        pool.Recreate(captureDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, new Windows.Graphics.SizeInt32 { Width = width, Height = height });
    }

    public void Show(bool value)
    {
        visible = value;
        if (controller != null) { controller.CoreWebView2.IsMuted = !value; controller.IsVisible = value; }
    }
    public async Task FocusAsync(bool value)
    {
        if (!value && controller != null)
        {
            foreach (var pair in heldKeys.ToArray()) await DispatchKeyAsync(false, pair.Key, pair.Value);
            heldKeys.Clear();
            foreach (var item in new[] { (CoreWebView2MouseEventVirtualKeys.LeftButton, CoreWebView2MouseEventKind.LeftButtonUp), (CoreWebView2MouseEventVirtualKeys.MiddleButton, CoreWebView2MouseEventKind.MiddleButtonUp), (CoreWebView2MouseEventVirtualKeys.RightButton, CoreWebView2MouseEventKind.RightButtonUp) })
            {
                if ((heldButtons & item.Item1) == 0) continue;
                heldButtons &= ~item.Item1;
                controller.SendMouseInput(item.Item2, heldButtons, 0, lastPoint);
            }
            Leave();
        }
        active = value;
    }
    public void Leave() => controller?.SendMouseInput(CoreWebView2MouseEventKind.Leave, heldButtons, 0, Point.Empty);
    public void Back() { if (controller?.CoreWebView2.CanGoBack == true) controller.CoreWebView2.GoBack(); }
    public void Refresh() => controller?.CoreWebView2.Reload();

    public void Mouse(string type, int x, int y, string button, int delta)
    {
        if (!active || !visible || controller == null) return;
        if (x < 0 || y < 0 || x >= width || y >= height) return;
        var point = new Point(x, y); lastPoint = point;
        var key = button switch { "left" => CoreWebView2MouseEventVirtualKeys.LeftButton, "middle" => CoreWebView2MouseEventVirtualKeys.MiddleButton, "right" => CoreWebView2MouseEventVirtualKeys.RightButton, _ => CoreWebView2MouseEventVirtualKeys.None };
        var kind = type switch
        {
            "move" => CoreWebView2MouseEventKind.Move,
            "wheel" => CoreWebView2MouseEventKind.Wheel,
            "down" when button == "left" => CoreWebView2MouseEventKind.LeftButtonDown,
            "up" when button == "left" => CoreWebView2MouseEventKind.LeftButtonUp,
            "down" when button == "middle" => CoreWebView2MouseEventKind.MiddleButtonDown,
            "up" when button == "middle" => CoreWebView2MouseEventKind.MiddleButtonUp,
            "down" when button == "right" => CoreWebView2MouseEventKind.RightButtonDown,
            "up" when button == "right" => CoreWebView2MouseEventKind.RightButtonUp,
            _ => throw new RendererFailure("start_failed")
        };
        if (type == "down") heldButtons |= key;
        if (type == "up") heldButtons &= ~key;
        var virtualKeys = heldButtons;
        if (heldKeys.ContainsKey(16)) virtualKeys |= CoreWebView2MouseEventVirtualKeys.Shift;
        if (heldKeys.ContainsKey(17)) virtualKeys |= CoreWebView2MouseEventVirtualKeys.Control;
        controller.SendMouseInput(kind, virtualKeys, type == "wheel" ? unchecked((uint)Math.Clamp(delta, -1200, 1200)) : 0, point);
    }

    public async Task KeyAsync(bool down, int vk, int modifiers)
    {
        if (!active || !visible || controller == null) return;
        if (vk < 0 || vk > 255 || modifiers < 0 || modifiers > 15) return;
        if (down) heldKeys[vk] = modifiers; else heldKeys.Remove(vk);
        await DispatchKeyAsync(down, vk, modifiers);
    }
    async Task DispatchKeyAsync(bool down, int vk, int modifiers)
    {
        if (controller == null) return;
        (string key, string code) = vk switch
        {
            8 => ("Backspace", "Backspace"), 9 => ("Tab", "Tab"), 13 => ("Enter", "Enter"), 27 => ("Escape", "Escape"),
            32 => (" ", "Space"), 33 => ("PageUp", "PageUp"), 34 => ("PageDown", "PageDown"), 35 => ("End", "End"), 36 => ("Home", "Home"),
            37 => ("ArrowLeft", "ArrowLeft"), 38 => ("ArrowUp", "ArrowUp"), 39 => ("ArrowRight", "ArrowRight"), 40 => ("ArrowDown", "ArrowDown"), 46 => ("Delete", "Delete"),
            16 => ("Shift", "ShiftLeft"), 17 => ("Control", "ControlLeft"), 18 => ("Alt", "AltLeft"),
            >= 65 and <= 90 => (((modifiers & 8) != 0 ? (char)vk : char.ToLowerInvariant((char)vk)).ToString(), "Key" + (char)vk),
            >= 48 and <= 57 => (((char)vk).ToString(), "Digit" + (char)vk),
            _ => ("Unidentified", "")
        };
        await controller.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new { type = down ? "rawKeyDown" : "keyUp", key, code, windowsVirtualKeyCode = vk, nativeVirtualKeyCode = vk, modifiers }));
    }
    public async Task TextAsync(string text)
    {
        if (!active || !visible || controller == null || text.Length > 256 || text.Contains('\0')) return;
        await controller.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text }));
    }
    public async Task<object> ProbeAsync()
    {
        if (controller == null) throw new RendererFailure("start_failed");
        if (isProof)
        {
            string result = await controller.CoreWebView2.ExecuteScriptAsync("JSON.stringify({type:'probe',h264:!!document.createElement('video').canPlayType('video/mp4; codecs=\"avc1.42E01E\"'),aac:!!document.createElement('video').canPlayType('audio/mp4; codecs=\"mp4a.40.2\"'),loaded:!!document.getElementById('entry'),clicked:!!window.proof&&proof.clicks>0,textAndBackspace:!!document.getElementById('entry')&&document.getElementById('entry').value==='AB',entered:!!window.proof&&proof.enters>0,scrolled:!!document.getElementById('scroll')&&document.getElementById('scroll').scrollTop>0})");
            using var parsed = JsonDocument.Parse(JsonSerializer.Deserialize<string>(result) ?? "{}");
            var safe = parsed.RootElement;
            bool B(string name) => safe.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
            return new { type = "probe", h264 = B("h264"), aac = B("aac"), loaded = B("loaded"), clicked = B("clicked"), textAndBackspace = B("textAndBackspace"), entered = B("entered"), scrolled = B("scrolled"), navigationBlocked, navigationSucceeded };
        }
        string json = await controller.CoreWebView2.ExecuteScriptAsync("JSON.stringify((()=>{const p=document.createElement('video'),v=document.querySelector('video');const c=t=>!!p.canPlayType(t);return {h264:c('video/mp4; codecs=\"avc1.42E01E\"'),aac:c('audio/mp4; codecs=\"mp4a.40.2\"'),webm:c('video/webm; codecs=\"vp9,opus\"'),av1:c('video/mp4; codecs=\"av01.0.05M.08\"'),videoPresent:!!v,mediaError:v&&v.error?v.error.code:0}})())");
        if (json.Length > 8192) throw new RendererFailure("video_unavailable");
        using var value = JsonDocument.Parse(JsonSerializer.Deserialize<string>(json) ?? "{}");
        var root = value.RootElement;
        bool Bool(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        int error = Protocol.Integer(root, "mediaError", out int number) && number is >= 0 and <= 4 ? number : 0;
        return new { type = "probe", h264 = Bool("h264"), aac = Bool("aac"), webm = Bool("webm"), av1 = Bool("av1"), videoPresent = Bool("videoPresent"), mediaError = error };
    }

    void CaptureFrame(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (disposed || !visible || frame == null || device == null || deviceContext == null || Frame == null) return;
        long now = frameClock.ElapsedMilliseconds; if (now - previousFrameMs < 33) return; previousFrameMs = now;
        if (frame.ContentSize.Width != width || frame.ContentSize.Height != height) return;
        IntPtr surface = MarshalInterface<IDirect3DSurface>.FromManaged(frame.Surface), access = IntPtr.Zero, texturePointer = IntPtr.Zero;
        try
        {
            var accessId = new Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surface, ref accessId, out access));
            var getter = Marshal.GetDelegateForFunctionPointer<Native.GetInterfaceDelegate>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(access), 3 * IntPtr.Size));
            var textureId = typeof(ID3D11Texture2D).GUID;
            Marshal.ThrowExceptionForHR(getter(access, ref textureId, out texturePointer));
            using var texture = new ID3D11Texture2D(texturePointer); texturePointer = IntPtr.Zero;
            var description = texture.Description;
            description.Usage = ResourceUsage.Staging; description.BindFlags = BindFlags.None; description.CPUAccessFlags = CpuAccessFlags.Read; description.MiscFlags = ResourceOptionFlags.None;
            using var staging = device.CreateTexture2D(description);
            deviceContext.CopyResource(staging, texture);
            deviceContext.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
            try
            {
                int length = checked(width * height * 4), stride = width * 4;
                byte[] bytes = ArrayPool<byte>.Shared.Rent(length);
                // Unity LoadRawTextureData expects the first row at the bottom of the texture.
                for (int y = 0; y < height; y++) Marshal.Copy(mapped.DataPointer + y * (int)mapped.RowPitch, bytes, (height - 1 - y) * stride, stride);
                Frame(new PackedFrame { Bytes = bytes, Width = width, Height = height, Length = length, Sequence = ++sequence });
            }
            finally { deviceContext.Unmap(staging, 0); }
        }
        catch { Status?.Invoke("start_failed"); }
        finally
        {
            if (texturePointer != IntPtr.Zero) Marshal.Release(texturePointer);
            if (access != IntPtr.Zero) Marshal.Release(access);
            Marshal.Release(surface);
        }
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        int pid = checked((int)(controller?.CoreWebView2.BrowserProcessId ?? 0u));
        if (pool != null) pool.FrameArrived -= CaptureFrame;
        capture?.Dispose(); pool?.Dispose(); controller?.Close();
        visual?.Dispose(); compositor?.Dispose(); captureDevice?.Dispose(); deviceContext?.Dispose(); device?.Dispose();
        window.DestroyHandle();
        if (dispatcher != IntPtr.Zero) { Marshal.Release(dispatcher); dispatcher = IntPtr.Zero; }
        if (pid > 0)
        {
            try { using var process = Process.GetProcessById(pid); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (ArgumentException) { }
            catch (TimeoutException) { }
        }
        if (disposableProfile != null)
        {
            // The only eligible directory is this worker's freshly generated private profile.
            string path = Path.GetFullPath(disposableProfile);
            string root = Path.GetFullPath(disposableRoot!).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && (Path.GetFileName(path).Length == 32 || Path.GetFileName(path) == "profile"))
            { try { Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }

    const string ProofPage = """
    <!doctype html><meta charset="utf-8"><style>body{margin:20px;background:#112337;color:white;font:22px sans-serif}button,input{display:block;width:320px;height:52px;font:24px sans-serif;margin:20px 0}#scroll{width:360px;height:180px;overflow:auto;background:#368}#inner{height:1500px;background:linear-gradient(#ea667a,#22c5a5)}</style><h1>Local worker fake input test</h1><button id="button">Click once</button><input id="entry" autocomplete="off"><div id="scroll"><div id="inner">Scroll test</div></div><script>window.proof={clicks:0,enters:0};button.onclick=()=>proof.clicks++;entry.onkeydown=e=>{if(e.key==='Enter'){proof.enters++;e.preventDefault();}};</script>
    """;
}

internal sealed class RendererFailure(string code) : Exception { public string Code { get; } = code; }
internal sealed class HiddenWindow : NativeWindow
{
    public void MakeHandle(int width, int height) => CreateHandle(new CreateParams { Caption = "TikTok Pocket Browser", Width = width, Height = height, Style = unchecked((int)0x80000000) });
}
internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct DispatcherQueueOptions { public int Size, ThreadType, ApartmentType; }
    [DllImport("CoreMessaging.dll")] public static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);
    [DllImport("d3d11.dll")] public static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr device, out IntPtr graphicsDevice);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int GetInterfaceDelegate(IntPtr self, ref Guid iid, out IntPtr result);
}

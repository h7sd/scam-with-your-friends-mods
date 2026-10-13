using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScamWYF.TikTokBrowser;

internal static class Protocol
{
    public const int MaxJson = 8192, MaxFrame = 8388608, Version = 1, Magic = 0x544B5046;
    public static bool ValidPipeName(string name) => Regex.IsMatch(name, "\\A[A-Za-z0-9_-]{8,80}\\z", RegexOptions.CultureInvariant);
    public static bool ValidSize(int width, int height) => width >= 64 && width <= 1080 && height >= 64 && height <= 1920 && (long)width * height * 4 <= MaxFrame;
    public static async Task<JsonDocument> ReadAsync(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 2 || length > MaxJson) throw new InvalidDataException();
        byte[] data = new byte[length]; await stream.ReadExactlyAsync(data, token);
        var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) { document.Dispose(); throw new InvalidDataException(); }
        return document;
    }
    public static async Task WriteAsync(Stream stream, object value, CancellationToken token)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(value);
        if (data.Length > MaxJson) throw new InvalidDataException();
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(data, token); await stream.FlushAsync(token);
    }
    public static bool Text(JsonElement root, string name, out string text)
    {
        text = ""; if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        text = property.GetString() ?? ""; return true;
    }
    public static bool Integer(JsonElement root, string name, out int value)
    {
        value = 0; return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out value);
    }
    public static bool Boolean(JsonElement root, string name, out bool value)
    {
        value = false; if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var p) || p.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = p.GetBoolean(); return true;
    }
    public static bool ValidateHello(JsonElement root, ReadOnlySpan<byte> expectedNonce)
    {
        if (expectedNonce.Length != 32 || !Text(root, "type", out var type) || type != "hello" || !Integer(root, "version", out int version) || version != Version
            || !Text(root, "nonce", out var text) || text.Length != 64) return false;
        byte[] supplied;
        try { supplied = Convert.FromHexString(text); } catch (FormatException) { return false; }
        try { return CryptographicOperations.FixedTimeEquals(expectedNonce, supplied); }
        finally { CryptographicOperations.ZeroMemory(supplied); }
    }
    public static bool ValidateCommand(JsonElement root, int width, int height)
    {
        if (!Text(root, "type", out var type)) return false;
        switch (type)
        {
            case "back": case "refresh": case "probe": case "close": case "leave": return true;
            case "show": return Boolean(root, "visible", out _);
            case "focus": return Boolean(root, "active", out _);
            case "resize": return Integer(root, "width", out int w) && Integer(root, "height", out int h) && ValidSize(w, h);
            case "text": return Text(root, "text", out var text) && text.Length <= 256 && !text.Contains('\0');
            case "keyDown": case "keyUp": return Integer(root, "vk", out int vk) && vk is >= 0 and <= 255 && Integer(root, "modifiers", out int modifiers) && modifiers is >= 0 and <= 15;
            case "move": case "down": case "up": case "wheel":
                if (!Integer(root, "x", out int x) || !Integer(root, "y", out int y) || x < 0 || y < 0 || x >= width || y >= height) return false;
                if (type is "down" or "up") return Text(root, "button", out var button) && button is "left" or "middle" or "right";
                return type != "wheel" || Integer(root, "delta", out int delta) && delta is >= -1200 and <= 1200;
            default: return false;
        }
    }
}

using System.Reflection;
using System.Text.Json;
using HostPause;

class Program
{
    static readonly List<string> checks = new();
    static void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
    static void Set(Type type, string name, object value) => type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(null, value);
    static void Main(string[] args)
    {
        var device = new ReachDevice();
        byte[] pixels = new byte[4];
        Visuals.ReadBackBuffer(device, pixels);
        Check(pixels[0] == 42 && !device._profileCapabilities.GetBackBufferData,
            "XNA Reach readback guard is lifted only for the readback call and restored");
        device.FailReadback = true;
        bool failed = false;
        try { Visuals.ReadBackBuffer(device, pixels); } catch (TargetInvocationException) { failed = true; }
        Check(failed && !device._profileCapabilities.GetBackBufferData,
            "XNA Reach capability is restored even when the GPU readback throws");

        Set(typeof(Runtime), "main", typeof(FakeMain));
        Set(typeof(Runtime), "netplay", typeof(FakeNetplay));
        Set(typeof(Runtime), "keyDown", typeof(FakeKeyboard).GetMethod("IsKeyDown")!);
        Set(typeof(Runtime), "key", "F9");
        Set(typeof(Runtime), "paused", true);
        Set(typeof(Runtime), "ready", true);
        Set(typeof(Runtime), "revision", (uint)1);
        Set(typeof(Visuals), "main", typeof(FakeMain));
        Runtime.AfterDraw(); // FakeGame.GraphicsDevice throws, as an unsupported capture would.
        Check(Runtime.IsPaused, "A graphical capture failure never resumes a network client");
        Check(Runtime.BeforeSimulation(new FakeGame()), "Simulation gate remains closed after a graphical failure");
        Check(!Runtime.DrawFrozenFrame() && Runtime.IsPaused, "Disabling visuals leaves authoritative pause active");
        var buffer = new FakeBuffer();
        byte[] state = { 254, (byte)'H', (byte)'P', (byte)'Z', (byte)'1', 2, 2, 0, 2, 0, 0, 0 };
        Array.Copy(state, buffer.readBuffer, state.Length);
        int messageType;
        Check(Runtime.HandlePacket(buffer, 0, state.Length, out messageType) && !Runtime.IsPaused,
            "Server resume packets still work after a graphical failure");
        Check(!Runtime.BeforeSimulation(new FakeGame()), "Simulation gate opens after the server resumes");
        File.WriteAllText(args[0], JsonSerializer.Serialize(new { passed = checks.Count, checks }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
public class ReachCapabilities { public bool GetBackBufferData; }
public class ReachDevice
{
    public ReachCapabilities _profileCapabilities = new();
    public bool FailReadback;
    public void GetBackBufferData<T>(T[] data) where T : struct
    {
        if (!_profileCapabilities.GetBackBufferData || FailReadback) throw new NotSupportedException("Simulated XNA Reach capture failure");
        ((byte[])(object)data)[0] = 42;
    }
}
public class FakeKeyboard { public bool IsKeyDown(object key) => false; }
public class FakeMonitor { public void Update() { } }
public class FakeGame { public object GraphicsDevice => throw new NotSupportedException("Simulated GPU failure"); }
public class FakeMain
{
    public static int netMode = 1;
    public static bool dedServ, gameMenu, drawingPlayerChat, editSign, editChest, gamePaused;
    public static FakeKeyboard keyState = new();
    public static FakeMonitor chatMonitor = new();
    public static FakeGame instance = new();
    public static void NewText(string text, byte r, byte g, byte b) { }
}
public class FakeConnection { public int TimeOutTimer; }
public class FakeNetplay { public static FakeConnection Connection = new(); }
public class FakeBuffer { public byte[] readBuffer = new byte[64]; public int whoAmI = 256; }

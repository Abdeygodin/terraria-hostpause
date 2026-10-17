using System;
using System.IO;
using Terraria;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Graphics;

public static class HostPauseRenderClient
{
    static int frames, connectedFrames, pausedFrames, resumedFrames;
    static bool joined, requested, captured, done;
    static int overlays;
    static uint frozenTick;
    static double frozenTime;
    static long started;
    static readonly bool guest = Environment.GetEnvironmentVariable("HOSTPAUSE_RENDER_ROLE") == "guest";
    static readonly bool injectCaptureFailure = Environment.GetEnvironmentVariable("HOSTPAUSE_TEST_CAPTURE_FAILURE") == "1";
    static bool failureInjected;

    public static void BeforeGame()
    {
        // SDL's offscreen driver cannot resize its pbuffer: choose the final size before Game creates it.
        typeof(GraphicsDeviceManager).GetField("DefaultBackBufferWidth").SetValue(null, 800);
        typeof(GraphicsDeviceManager).GetField("DefaultBackBufferHeight").SetValue(null, 600);
    }

    public static void InitSocialWithoutSteam(Terraria.Social.SocialMode? ignored)
    {
        Terraria.Social.SocialAPI.Initialize(Terraria.Social.SocialMode.None);
        var manager = typeof(Main).GetField("graphics", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null);
        manager.GetType().GetProperty("PreferredBackBufferWidth").SetValue(manager, 800, null);
        manager.GetType().GetProperty("PreferredBackBufferHeight").SetValue(manager, 600, null);
    }

    public static void OnFrame()
    {
        frames++;
        if (started == 0) started = DateTime.UtcNow.Ticks;
        if (!joined && frames > 300 && Main.gameMenu && Main.menuMode == 0 && NetMessage.buffer[256] != null)
        {
            joined = true;
            var data = Player.GetFileData("../test-client/Players/RenderTest.plr", false);
            data.Player.name = "HP-Render-Client";
            Main.myPlayer = 0;
            data.SetAsActive();
            Netplay.SetRemoteIP("127.0.0.1");
            Netplay.ListenPort = 17777;
            Netplay.HostToken = guest ? null : "HostPause-Isolated-Test";
            Main.StartClientGameplay();
            Log("Joining isolated test server");
        }
        if (DateTime.UtcNow.Ticks - started > TimeSpan.TicksPerSecond * 90 && !done) { Log("TIMEOUT frames=" + frames + " connected=" + connectedFrames + " paused=" + pausedFrames); Main.instance.Exit(); done = true; }
    }

    public static void DriveKey()
    {
        if (Main.gameMenu || Main.netMode != 1 || Netplay.Connection.State != 10) return;
        connectedFrames++;
        bool press = false;
        if (!guest && !requested && connectedFrames == 120) { requested = true; press = true; Log("PRESS F9: pause at frame " + frames); }
        if (HostPause.Runtime.IsPaused)
        {
            pausedFrames++;
            if (injectCaptureFailure && !failureInjected)
            {
                failureInjected = true;
                typeof(HostPause.Visuals).GetField("textureType", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).SetValue(null, null);
                Log("Injected screenshot failure on the graphical guest");
            }
            if (pausedFrames == 1) Log("Pause received at frame " + frames);
            if (pausedFrames == 20) { frozenTick = Main.GameUpdateCount; frozenTime = Main.time; }
            if (pausedFrames == 150)
            {
                if (Main.GameUpdateCount != frozenTick || Main.time != frozenTime) throw new Exception("Client simulation moved during pause");
                press = !guest;
                if (guest) File.WriteAllText("../test-data/guest-frozen.signal", "frozen");
                Log(guest ? "PASS: graphical guest simulation frozen despite screenshot failure" : "PASS: actual FNA client simulation frozen; PRESS F9: resume");
            }
        }
        else if (pausedFrames > 0 && ++resumedFrames == 60 && !done)
        {
            done = true;
            if (guest && pausedFrames < 150) throw new Exception("Guest resumed locally before authoritative resume");
            if (Main.GameUpdateCount == frozenTick) throw new Exception("Client did not resume");
            Log(guest ? "PASS: server resume reached graphical guest after screenshot failure" : "PASS: actual F9 resumed client simulation; captured=" + captured);
            Main.instance.Exit();
        }
        Main.keyState = press ? new KeyboardState(Keys.F9) : new KeyboardState();
    }

    public static void OnDraw()
    {
        if (captured || pausedFrames < 40) return;
        captured = true;
        Log("Draw state: hideUI=" + Main.hideUI + " main=" + Main.screenWidth + "x" + Main.screenHeight + " overlays=" + overlays);
        var device = Main.instance.GraphicsDevice;
        int width = device.PresentationParameters.BackBufferWidth;
        int height = device.PresentationParameters.BackBufferHeight;
        Color[] pixels = new Color[width*height];
        device.GetBackBufferData(pixels);
        using (var image = new Texture2D(device, width, height))
        {
            image.SetData(pixels);
            using (var stream = File.Create(guest ? "../test-data/guest-pause-client.png" : "../test-data/pause-client.png")) image.SaveAsPng(stream, width, height);
        }
        Log("Captured pause-client.png");
    }

    public static void OnOverlay() { overlays++; }

    static void Log(string message) { File.AppendAllText(guest ? "../test-data/guest-render-test.log" : "../test-data/render-test.log", message + "\n"); }
}

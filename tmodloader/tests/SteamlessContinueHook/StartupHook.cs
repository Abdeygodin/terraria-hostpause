using System;
using System.Linq;
using System.Reflection;
using System.Threading;

// Loaded by the runtime before tModLoader's Main through DOTNET_STARTUP_HOOKS.
internal class StartupHook
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    public static void Initialize()
    {
        new Thread(Watch) { IsBackground = true, Name = "SteamlessContinueHook" }.Start();
    }

    static void Watch()
    {
        for (int i = 0; i < 1200; i++)
        {
            Thread.Sleep(500);
            try
            {
                Assembly game = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "tModLoader");
                Type main = game?.GetType("Terraria.Main");
                if (main == null || (int)main.GetField("menuMode", Flags).GetValue(null) != 888) continue;
                object menu = main.GetField("MenuUI", Flags).GetValue(null);
                object state = menu?.GetType().GetProperty("CurrentState", Flags).GetValue(menu);
                if (state == null || state.GetType().Name != "UIErrorMessage") continue;
                string message = (string)state.GetType().GetField("message", Flags).GetValue(state);
                if (message == null || !message.StartsWith("Steam Game Server failed to Init")) continue;
                MethodInfo click = state.GetType().GetMethod("ContinueClick", Flags);
                Action press = () => click.Invoke(state, new object[] { null, null });
                main.GetMethod("QueueMainThreadAction", Flags).Invoke(null, new object[] { press });
                Console.WriteLine("[SteamlessContinueHook] pressed Continue on the Steam notice");
                return;
            }
            catch (Exception ex) { Console.WriteLine("[SteamlessContinueHook] " + ex.Message); }
        }
    }
}

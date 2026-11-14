using Terraria.Localization;
using Terraria.ModLoader;

namespace HostPause.Common
{
    // Server console: "pause", "resume", "hostpause" (no slash, like vanilla console commands).
    // In chat: "/pause", "/resume", "/hostpause"; in multiplayer they run on the server with the
    // same host-only rule as the pause key. Russian aliases: пауза, продолжить.
    public abstract class PauseCommandBase : ModCommand
    {
        protected abstract PauseCommandKind Kind { get; }
        public override CommandType Type => CommandType.Console | CommandType.World;
        public override string Description => Language.GetTextValue("Mods.HostPause.Commands." + Kind);
        public override void Action(CommandCaller caller, string input, string[] args) => PauseSystem.Command(caller, Kind);
    }

    public class PauseCommand : PauseCommandBase
    {
        protected override PauseCommandKind Kind => PauseCommandKind.Pause;
        public override string Command => "pause";
    }

    public class PauseRuCommand : PauseCommandBase
    {
        protected override PauseCommandKind Kind => PauseCommandKind.Pause;
        public override string Command => "пауза";
    }

    public class ResumeCommand : PauseCommandBase
    {
        protected override PauseCommandKind Kind => PauseCommandKind.Resume;
        public override string Command => "resume";
    }

    public class ResumeRuCommand : PauseCommandBase
    {
        protected override PauseCommandKind Kind => PauseCommandKind.Resume;
        public override string Command => "продолжить";
    }

    public class StatusCommand : PauseCommandBase
    {
        protected override PauseCommandKind Kind => PauseCommandKind.Status;
        public override string Command => "hostpause";
    }
}

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

/// <summary>
/// GM command to force your own faction's Mobilization Order time state, as a testing shortcut for the
/// real flag chain (place the flag doodad, acquire item 41488, cast the right declaration skill).
/// </summary>
public class MobilizationTimeCmd : ICommand
{
    public string[] CommandNames { get; set; } = ["mobtime", "mobilizationtime"];

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp()
    {
        return "[leap|battle|plunder|none] [minutes]";
    }

    public string GetCommandHelpText()
    {
        return "Show or force your faction's Mobilization Order time state (Leap/Battle/Plunder). " +
               "'none' clears it. Minutes defaults to 30, matching the real declaration skills.";
    }

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        if (character.Faction == null)
        {
            CommandManager.SendErrorText(this, messageOutput, "You have no faction.");
            return;
        }

        var factionId = (uint)character.Faction.Id;

        if (args.Length == 0)
        {
            var current = HeroManager.Instance.GetActiveMobilizationTimeState(factionId);
            CommandManager.SendNormalText(this, messageOutput, $"Your faction's active Mobilization Order time state: {current}");
            return;
        }

        var state = args[0].ToLowerInvariant() switch
        {
            "leap" => (MobilizationTimeStateType?)MobilizationTimeStateType.Leap,
            "battle" or "war" => MobilizationTimeStateType.Battle,
            "plunder" => MobilizationTimeStateType.Plunder,
            "none" or "clear" or "off" => MobilizationTimeStateType.None,
            _ => null
        };

        if (state == null)
        {
            CommandManager.SendErrorText(this, messageOutput, $"'{args[0]}' is not a state. Use leap, battle, plunder, or none.");
            return;
        }

        var minutes = 30;
        if (args.Length > 1 && !int.TryParse(args[1], out minutes))
        {
            CommandManager.SendErrorText(this, messageOutput, "Usage: /mobtime <leap|battle|plunder|none> [minutes]");
            return;
        }

        HeroManager.Instance.SetMobilizationTimeState(factionId, state.Value, TimeSpan.FromMinutes(minutes));
        CommandManager.SendNormalText(this, messageOutput,
            state.Value == MobilizationTimeStateType.None
                ? "Your faction's Mobilization Order time state cleared."
                : $"Your faction's Mobilization Order time state set to {state.Value} for {minutes} minute(s).");
    }
}

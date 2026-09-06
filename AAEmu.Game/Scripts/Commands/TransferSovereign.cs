using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

/// <summary>
/// GM/testing tool, added 2026-08-21 (player-nation system, Phase 1) - the current Sovereign hands leadership
/// to another current member of the same nation (resign/succession).
/// </summary>
public class TransferSovereign : ICommand
{
    public string[] CommandNames { get; set; } = ["transfersovereign"];

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp()
    {
        return "<playerName>";
    }

    public string GetCommandHelpText()
    {
        return "Hands Sovereignty of your nation to another current member. You must be the current Sovereign.";
    }

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        if (args.Length < 1)
        {
            CommandManager.SendDefaultHelpText(this, messageOutput);
            return;
        }

        var target = WorldManager.Instance.GetCharacter(args[0]);
        if (target == null)
        {
            CommandManager.SendErrorText(this, messageOutput, $"Character '{args[0]}' not found or not online.");
            return;
        }

        if (NationManager.Instance.TransferSovereign(character, target))
            CommandManager.SendNormalText(this, messageOutput, $"{target.Name} is now Sovereign.");
        else
            CommandManager.SendErrorText(this, messageOutput,
                "Failed - you're not a Sovereign, or the target isn't a member of your nation.");
    }
}

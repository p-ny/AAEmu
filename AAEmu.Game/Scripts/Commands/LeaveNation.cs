using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

/// <summary>
/// GM/testing tool, added 2026-08-21 (player-nation system, Phase 1) - reverts the caller to their original
/// home faction. Fails if they're still a member of a guild that's permanently bound to a nation (guild-level
/// nation membership is sticky by design - leave the guild first).
/// </summary>
public class LeaveNation : ICommand
{
    public string[] CommandNames { get; set; } = ["leavenation"];

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp()
    {
        return "";
    }

    public string GetCommandHelpText()
    {
        return "Leaves your current nation, reverting to your original home faction.";
    }

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        if (NationManager.Instance.LeaveNation(character))
            CommandManager.SendNormalText(this, messageOutput, "You left your nation and reverted to your home faction.");
        else
            CommandManager.SendErrorText(this, messageOutput,
                "Failed - you're not in a nation, or you're still in a guild that's permanently bound to one (leave the guild first).");
    }
}

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

/// <summary>
/// GM/testing tool, added 2026-08-21 (player-nation system, Phase 1) - invites a Nuia or Haranya character
/// (not already nation-affiliated) into the caller's nation. Caller must be that nation's Sovereign. No
/// client-facing invite-prompt packet exists for this - applies immediately, same reasoning as
/// ClaimTerritory/UnclaimTerritory.
/// </summary>
public class InviteToNation : ICommand
{
    public string[] CommandNames { get; set; } = ["invitetonation"];

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
        return "Invites a Nuia/Haranya character into your nation. You must be that nation's Sovereign.";
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

        if (NationManager.Instance.InviteToNation(character, target))
            CommandManager.SendNormalText(this, messageOutput, $"{target.Name} joined your nation.");
        else
            CommandManager.SendErrorText(this, messageOutput,
                "Failed - you're not a Sovereign, or the target isn't a Nuia/Haranya character free to join (already nation-affiliated, or pirate).");
    }
}

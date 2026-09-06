using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

/// <summary>
/// GM/testing tool, added 2026-08-21 (player-nation system, Phase 1) - disbands the caller's nation.
/// Voluntary (no "force" arg): territory reverts to the founding guild, member guilds are freed (not deleted).
/// Forced (pass "force" - simulates the future 30-day siege-loss-grace-period expiry): territory is left
/// untouched, every member guild is deleted outright. Either way every member reverts to their home faction.
/// </summary>
public class DisbandNation : ICommand
{
    public string[] CommandNames { get; set; } = ["disbandnation"];

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp()
    {
        return "[force]";
    }

    public string GetCommandHelpText()
    {
        return "Disbands your nation. Bare: territory reverts to the founding guild. 'force': territory left "
             + "untouched (simulates losing it to someone else) and every member guild is deleted.";
    }

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        var myZoneId = NationManager.Instance.GetNationOfSovereign(character.Id);
        if (myZoneId == null)
        {
            CommandManager.SendErrorText(this, messageOutput, "You're not a Sovereign.");
            return;
        }

        var forced = args.Length > 0 && args[0].Equals("force", System.StringComparison.OrdinalIgnoreCase);
        if (NationManager.Instance.Disband(myZoneId.Value, forced))
            CommandManager.SendNormalText(this, messageOutput,
                $"Nation (zone {myZoneId}) disbanded ({(forced ? "forced" : "voluntary")}).");
        else
            CommandManager.SendErrorText(this, messageOutput, "Failed to disband.");
    }
}

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

/// <summary>
/// Forces (or releases) the hero election season's current phase, as a testing aid.
/// </summary>
/// <remarks>
/// The shipped hero_schedules windows are weeks to months apart (see HeroManager's class-level doc comment) -
/// without this there is no way to reach the abstain window, the ballot, or the count without waiting, or
/// without hand-editing dates in both the server's AND the client's own compact.sqlite3 (they have to agree,
/// or the client's own phase-resolution check silently disagrees with the server - see the "closed-zone
/// saga"-style investigation in aaemu-siege-castle-hero-nation memory, 2026-08-14, for exactly that bug).
///
/// Ported from a rejected community PR (github.com/AAEmu/AAEmu/pull/1516's HeroPhaseCmd) - rejected by the
/// AAEmu maintainers for code shape, not for this idea, which is genuinely useful. Adapted onto this
/// codebase's own HeroManager (SetOverride/Describe), not the PR's separate HeroElectionManager - see
/// HeroManager.cs's doc comment on _phaseOverride for why.
/// </remarks>
public class HeroPhaseCmd : ICommand
{
    public string[] CommandNames { get; set; } = ["herophase", "heroperiod"];

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp()
    {
        return "[ranking|abstain|voting|period|none|auto]";
    }

    public string GetCommandHelpText()
    {
        return
            "Show or force the hero season's phase.\n" +
            "  /herophase              - where the season stands, and its full schedule\n" +
            "  /herophase ranking      - leadership_ranking: leadership accrues toward candidacy/voting\n" +
            "  /herophase abstain      - hero_abstain: freezes/computes the candidate list, withdrawals open\n" +
            "  /herophase voting       - hero_voting: opens the ballot (CSHeroVotingPacket)\n" +
            "  /herophase period       - hero_period: counts ballots, seats the winner, pays out hero_rewards\n" +
            "  /herophase none         - between phases, as the gaps in hero_schedules genuinely are\n" +
            "  /herophase auto         - stop forcing and follow hero_schedules again\n" +
            "A forced phase sticks until cleared and is announced to everyone online at once. This only " +
            "affects the SERVER's view of the phase - the client also checks the forced phase's season id " +
            "against its OWN local hero_schedules copy, so a client whose compact.sqlite3 has no matching row " +
            "for that season may still disagree; see aaemu-siege-castle-hero-nation memory.";
    }

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        if (args.Length == 0)
        {
            CommandManager.SendNormalText(this, messageOutput, HeroManager.Instance.Describe());
            return;
        }

        var verb = args[0].ToLowerInvariant();

        if (verb is "auto" or "schedule" or "clear")
        {
            HeroManager.Instance.SetOverride(null);
            CommandManager.SendNormalText(this, messageOutput,
                "Following hero_schedules again.\n" + HeroManager.Instance.Describe());
            return;
        }

        var phase = verb switch
        {
            "ranking" or "leadership" or "leadership_ranking" or "1" => (HeroPhase?)HeroPhase.LeadershipRanking,
            "abstain" or "hero_abstain" or "2" => HeroPhase.HeroAbstain,
            "voting" or "vote" or "hero_voting" or "3" => HeroPhase.HeroVoting,
            "period" or "hero_period" or "serving" or "4" => HeroPhase.HeroPeriod,
            "none" or "off" or "0" => HeroPhase.None,
            _ => null
        };

        if (phase == null)
        {
            CommandManager.SendErrorText(this, messageOutput,
                $"'{args[0]}' is not a phase. Use ranking, abstain, voting, period, none or auto.");
            return;
        }

        HeroManager.Instance.SetOverride(phase.Value);
        CommandManager.SendNormalText(this, messageOutput, HeroManager.Instance.Describe());
    }
}

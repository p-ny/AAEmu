using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

/// <summary>
/// Testing aid for the Hero "Dominion Points" feature (X2Hero:GiveDominionPoint/DominionPointCount) - see
/// HeroManager.GiveDominionPoint's own doc comment for the full mechanic and its scope limits.
/// </summary>
public class DominionPoint : ICommand
{
    public string[] CommandNames { get; set; } = ["dominionpoint", "dompoint"];

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp()
    {
        return "[give <zoneId> | reset]";
    }

    public string GetCommandHelpText()
    {
        return "With no args, shows your current Dominion Point daily/weekly usage. 'give <zoneId>' calls "
             + "the same action as the client's 'distribution' button (must be a currently-serving Hero, "
             + "and the dominion at <zoneId> must be owned by your faction). 'reset' clears today's/this "
             + "week's usage for repeat testing (GM only, not a real client-facing feature).";
    }

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        if (args.Length == 0)
        {
            CommandManager.SendNormalText(this, messageOutput, Describe(character));
            return;
        }

        var verb = args[0].ToLowerInvariant();
        switch (verb)
        {
            case "give":
                if (args.Length < 2 || !ushort.TryParse(args[1], out var zoneId))
                {
                    CommandManager.SendErrorText(this, messageOutput, "Usage: /dominionpoint give <zoneId>");
                    return;
                }

                var result = HeroManager.Instance.GiveDominionPoint(character, zoneId);
                CommandManager.SendNormalText(this, messageOutput, $"GiveDominionPoint({zoneId}): {result}");
                break;
            case "reset":
                character.DominionPointWeeklyGiven = 0;
                character.LastDominionPointGiveTime = default;
                HeroManager.Instance.SendDominionPointCount(character);
                CommandManager.SendNormalText(this, messageOutput, "Dominion Point usage reset.");
                break;
            default:
                CommandManager.SendErrorText(this, messageOutput, "Usage: /dominionpoint [give <zoneId> | reset]");
                break;
        }
    }

    private static string Describe(Character character)
    {
        var (daily, dailyMax, weekly, weeklyMax, remainSec) = HeroManager.Instance.GetDominionPointCount(character);
        return $"Dominion Points: today {daily}/{dailyMax} (resets in {remainSec}s), " +
               $"this week {weekly}/{weeklyMax}.";
    }
}

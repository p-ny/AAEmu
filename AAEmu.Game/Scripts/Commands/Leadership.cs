using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

/// <summary>
/// Testing aid for the 4-figure leadership model (current/previous-period/lifetime/daily) - see
/// Character.LeadershipPoint's doc comment and D:\aa\hero-vote-bug-report.txt. Sub-verbs match a rejected
/// community PR's own /leadership design (github.com/AAEmu/AAEmu/pull/1516, see D:\aa\heropullinfo.txt),
/// scoped to the caller only - unlike that PR's version, this does not support targeting an offline
/// character by name.
/// </summary>
public class Leadership : ICommand
{
    public string[] CommandNames { get; set; } = ["leadership", "leader"];

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp()
    {
        return "[<amount> | set <n> | setlast <n> | setcumul <n> | add <n>]";
    }

    public string GetCommandHelpText()
    {
        return "With no args, shows all four leadership figures for yourself. <amount> (bare, or 'add "
             + "<amount>') awards leadership, which also moves the daily cap counter and the lifetime "
             + "total. 'set <n>' sets current-period leadership outright (decides Hero candidacy/rank) "
             + "without touching the daily counter. 'setlast <n>' sets the PREVIOUS period's frozen "
             + "leadership (decides who may vote - see Character.LeadershipPeriodPoint). 'setcumul <n>' "
             + "sets the lifetime total (display only).";
    }

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        if (args.Length == 0)
        {
            CommandManager.SendNormalText(this, messageOutput, Describe(character));
            return;
        }

        var verb = args[0].ToLowerInvariant();
        if (verb is "set" or "setlast" or "setcumul")
        {
            if (args.Length < 2 || !int.TryParse(args[1], out var value))
            {
                CommandManager.SendErrorText(this, messageOutput, $"Usage: /leadership {verb} <n>");
                return;
            }

            switch (verb)
            {
                case "set":
                    character.LeadershipPoint = Math.Max(0, value);
                    character.SendPacket(new SCCharacterGamePointsPacket(character));
                    break;
                case "setlast":
                    character.LeadershipPeriodPoint = Math.Max(0, value);
                    character.SendPacket(new SCCharacterGamePointsPacket(character));
                    character.SendPacket(new SCHeroSeasonOffPacket(0, character.LeadershipPeriodPoint));
                    break;
                case "setcumul":
                    character.AccumulatedLeadershipPoint = Math.Max(0, value);
                    character.SendPacket(new SCCharacterGamePointsPacket(character));
                    break;
            }

            HeroManager.Instance.SendHeroInfo(character);
            CommandManager.SendNormalText(this, messageOutput, Describe(character));
            return;
        }

        var amountArg = verb == "add" ? (args.Length > 1 ? args[1] : null) : args[0];
        if (amountArg == null || !int.TryParse(amountArg, out var amount))
        {
            CommandManager.SendErrorText(this, messageOutput, "Usage: /leadership <amount>, or set/setlast/setcumul/add <n>");
            return;
        }

        character.ChangeGamePoints(GamePointKind.Leadership, amount);
        HeroManager.Instance.SendHeroInfo(character);
        CommandManager.SendNormalText(this, messageOutput, Describe(character));
    }

    private static string Describe(Character character) =>
        $"Leadership: period {character.LeadershipPoint}, last period {character.LeadershipPeriodPoint}, " +
        $"lifetime {character.AccumulatedLeadershipPoint}, earned today {character.DailyLeadershipPoint}.";
}

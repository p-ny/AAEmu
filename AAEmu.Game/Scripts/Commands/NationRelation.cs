using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

/// <summary>
/// No client packet exists for nation-to-nation diplomacy in r575 (checked - no CS*Nation* class anywhere),
/// so this is exposed as a Sovereign-only chat command rather than a guessed opcode. See
/// NationManager.RequestRelation.
/// </summary>
public class NationRelation : ICommand
{
    public string[] CommandNames { get; set; } = ["nationrelation"];

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp() => "<targetZoneId> <friend|hostile>";

    public string GetCommandHelpText() => "Sovereign-only: proposes a friend or hostile relation to another nation, by mail.";

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        if (args.Length < 2 || !ushort.TryParse(args[0], out var targetZoneId))
        {
            CommandManager.SendNormalText(this, messageOutput, "Usage: /nationrelation <targetZoneId> <friend|hostile>");
            return;
        }

        var friend = args[1].Equals("friend", StringComparison.OrdinalIgnoreCase);
        var hostile = args[1].Equals("hostile", StringComparison.OrdinalIgnoreCase);
        if (!friend && !hostile)
        {
            CommandManager.SendNormalText(this, messageOutput, "Second argument must be 'friend' or 'hostile'.");
            return;
        }

        NationManager.Instance.RequestRelation(character, targetZoneId, friend);
        CommandManager.SendNormalText(this, messageOutput, $"Relation proposal sent to zone {targetZoneId}, if you are a Sovereign and that zone is a nation.");
    }
}

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

/// <summary>Sovereign-only: accepts or rejects a pending nation relation request. See NationManager.RespondRelation.</summary>
public class NationRelationRespond : ICommand
{
    public string[] CommandNames { get; set; } = ["nationrelationrespond"];

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp() => "<requesterZoneId> <accept|reject>";

    public string GetCommandHelpText() => "Sovereign-only: accepts or rejects a pending relation request from another nation.";

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        if (args.Length < 2 || !ushort.TryParse(args[0], out var requesterZoneId))
        {
            CommandManager.SendNormalText(this, messageOutput, "Usage: /nationrelationrespond <requesterZoneId> <accept|reject>");
            return;
        }

        var accept = args[1].Equals("accept", StringComparison.OrdinalIgnoreCase);
        var reject = args[1].Equals("reject", StringComparison.OrdinalIgnoreCase);
        if (!accept && !reject)
        {
            CommandManager.SendNormalText(this, messageOutput, "Second argument must be 'accept' or 'reject'.");
            return;
        }

        NationManager.Instance.RespondRelation(character, requesterZoneId, accept);
        CommandManager.SendNormalText(this, messageOutput, accept ? "Relation accepted." : "Relation rejected.");
    }
}

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

/// <summary>
/// Sovereign-only: sets a founded nation's diplomatic relation toward Nuia or Haranya (the pre-existing
/// alliance factions, not other player nations - see NationRelation.cs for that separate case). No client
/// packet exists for this in r575, same reasoning as NationRelation.cs. See NationManager.SetAllianceRelation -
/// this updates the nation's live SystemFaction.Relations entry, so every existing relation-driven check
/// (aggro, doodad interaction, trade, plot conditions) respects it immediately.
/// </summary>
public class NationAllianceRelation : ICommand
{
    public string[] CommandNames { get; set; } = ["nationalliance"];

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp() => "<nuia|haranya> <neutral|allied|hostile>";

    public string GetCommandHelpText() => "Sovereign-only: sets your nation's relation to Nuia or Haranya.";

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        if (args.Length < 2)
        {
            CommandManager.SendNormalText(this, messageOutput, "Usage: /nationalliance <nuia|haranya> <neutral|allied|hostile>");
            return;
        }

        var target = args[0].ToLowerInvariant() switch
        {
            "nuia" => FactionsEnum.NuiaAlliance,
            "haranya" => FactionsEnum.HaranyaAlliance,
            _ => FactionsEnum.Invalid
        };
        if (target == FactionsEnum.Invalid)
        {
            CommandManager.SendNormalText(this, messageOutput, "First argument must be 'nuia' or 'haranya'.");
            return;
        }

        var state = args[1].ToLowerInvariant() switch
        {
            "neutral" => RelationState.Neutral,
            "allied" or "friend" or "friendly" => RelationState.Friendly,
            "hostile" => RelationState.Hostile,
            _ => (RelationState)0
        };
        if (state == 0)
        {
            CommandManager.SendNormalText(this, messageOutput, "Second argument must be 'neutral', 'allied', or 'hostile'.");
            return;
        }

        if (!NationManager.Instance.SetAllianceRelation(character, target, state))
        {
            CommandManager.SendNormalText(this, messageOutput, "Failed - you must be a Sovereign of a founded nation.");
            return;
        }

        CommandManager.SendNormalText(this, messageOutput, $"Relation with {args[0]} set to {state}.");
    }
}

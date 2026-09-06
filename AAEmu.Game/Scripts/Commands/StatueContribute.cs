using System;
using System.Linq;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

/// <summary>
/// GM testing aid for the Hero capital-city Statue (<see cref="DoodadFuncFactionStatueDevote"/>) - directly
/// advances a faction's statue devote counter/phase by a given amount, bypassing the real item cost, but
/// replicating the same phase-advance/completion-buff-grant behavior a real devotion would trigger.
/// </summary>
public class StatueContribute : ICommand
{
    public string[] CommandNames { get; set; } = ["statue"];

    private static readonly (string Name, uint TemplateId, FactionsEnum Faction)[] Statues =
    [
        ("nuia", 10588, (FactionsEnum)148),
        ("haranya", 10650, (FactionsEnum)149),
        ("pirate", 10651, (FactionsEnum)114),
    ];

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp() => "<nuia|haranya|pirate> add <count>";

    public string GetCommandHelpText() =>
        "Advances the named faction's capital statue devote counter by <count> (Hero-only and " +
        "any-player phases both accepted, no item consumed) - same phase-advance/completion-buff-grant " +
        "as a real devotion. GM-only testing aid, not a real client-facing feature.";

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        if (args.Length < 3 || args[1].ToLowerInvariant() != "add" || !int.TryParse(args[2], out var count) || count <= 0)
        {
            CommandManager.SendErrorText(this, messageOutput, "Usage: /statue <nuia|haranya|pirate> add <count>");
            return;
        }

        var faction = Array.Find(Statues, s => s.Name == args[0].ToLowerInvariant());
        if (faction.TemplateId == 0)
        {
            CommandManager.SendErrorText(this, messageOutput, "Usage: /statue <nuia|haranya|pirate> add <count>");
            return;
        }

        var doodad = character.ParentWorld.GetDoodadsByTemplateId(faction.TemplateId).FirstOrDefault();
        if (doodad == null)
        {
            CommandManager.SendErrorText(this, messageOutput, $"No live statue doodad found for {faction.Name} (template {faction.TemplateId}) in this world instance.");
            return;
        }

        var remaining = count;
        var advanced = 0;
        while (remaining > 0)
        {
            var func = DoodadManager.Instance.GetDoodadFuncs(doodad.FuncGroupId)
                .FirstOrDefault(f => DoodadManager.Instance.GetFuncTemplate(f.FuncId, f.FuncType) is DoodadFuncDevote);
            if (func == null)
            {
                CommandManager.SendNormalText(this, messageOutput,
                    $"{faction.Name} statue is not currently on a devote phase (func group {doodad.FuncGroupId}) - stopping. Added {advanced}, {remaining} left over.");
                break;
            }

            var template = (DoodadFuncDevote)DoodadManager.Instance.GetFuncTemplate(func.FuncId, func.FuncType);
            var toFill = template.Count - doodad.Data;
            var thisStep = Math.Min(remaining, Math.Max(toFill, 0));
            doodad.Data += thisStep;
            remaining -= thisStep;
            advanced += thisStep;

            if (doodad.Data < template.Count)
            {
                DoodadFuncDevote.PublishProgress(doodad, doodad.Data);
                break;
            }

            DoodadFuncDevote.PublishProgress(doodad, 0);
            doodad.DoChangePhase(character, func.NextPhase);
            if (DoodadFuncFactionStatueDevote.CompleteGroupIds.Contains(func.NextPhase))
            {
                // Real devotion grants only to whoever's action completed it (see that method's doc
                // comment) - the GM running this command is the closest stand-in for "the completing
                // player" here.
                DoodadFuncFactionStatueDevote.GrantFactionStatueBuff(character);
            }

            if (thisStep == 0)
            {
                // Count was already 0/misconfigured - avoid looping forever on a data error.
                break;
            }
        }

        CommandManager.SendNormalText(this, messageOutput,
            $"{faction.Name} statue: added {advanced}, now at {doodad.Data} in func group {doodad.FuncGroupId}.");
    }
}

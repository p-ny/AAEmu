using System;
using System.Linq;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

/// <summary>
/// GM/testing tool, added 2026-08-21 - claims a Dominion territory without needing a live lodestone-doodad
/// interaction. Ownership model split 2026-08-21: zone groups with a real siege_zones schedule (33/34/43/44 -
/// Salpimari/Nuimari/Marcala/Calmlands) are Hero/faction-only, second argument must be "nuia" or "haranya";
/// zone groups without one (54/56 - Exeloch/Sungold Fields) stay guild-owned, second argument is a guild name
/// (your own guild if omitted). See DominionManager.Declare/DeclareForFaction/ClaimTerritory/
/// ClaimTerritoryForFaction's doc comments for exactly what this does and why it can fail.
/// </summary>
public class ClaimTerritory : ICommand
{
    public string[] CommandNames { get; set; } = ["claimterritory"];

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp()
    {
        return "<zoneGroupId> <nuia|haranya|guildName>";
    }

    public string GetCommandHelpText()
    {
        return "Claims a Dominion territory (by zone GROUP id, e.g. 33 for Salpimari). For a Hero/faction-only "
             + "territory (33/34/43/44), pass 'nuia' or 'haranya'. For a guild-owned territory (54/56), pass a "
             + "guild name, or omit it to use your own guild. Fails if the zone group is already claimed, or if "
             + "this build has no seeded lodestone House for it.";
    }

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        if (args.Length < 1 || !ushort.TryParse(args[0], out var zoneGroupId))
        {
            CommandManager.SendDefaultHelpText(this, messageOutput);
            return;
        }

        if (DominionZoneLockManager.Instance.IsLocked(zoneGroupId))
        {
            CommandManager.SendErrorText(this, messageOutput,
                $"Zone group {zoneGroupId} is locked - use /enablecastle {zoneGroupId} first.");
            return;
        }

        if (DominionManager.Instance.GetByZoneId(zoneGroupId) != null || GuildDominionManager.Instance.GetByZoneId(zoneGroupId) != null)
        {
            CommandManager.SendErrorText(this, messageOutput,
                $"Zone group {zoneGroupId} is already claimed - use /unclaimterritory {zoneGroupId} first.");
            return;
        }

        var isFactionTerritory = SiegeGameData.Instance.GetSiegeZoneSchedule(zoneGroupId) != null;
        if (isFactionTerritory)
        {
            var factionArg = args.Length >= 2 ? args[1].ToLowerInvariant() : null;
            var factionId = factionArg switch
            {
                "nuia" => FactionsEnum.NuiaAlliance,
                "haranya" => FactionsEnum.HaranyaAlliance,
                _ => FactionsEnum.Invalid
            };
            if (factionId == FactionsEnum.Invalid)
            {
                CommandManager.SendErrorText(this, messageOutput,
                    $"Zone group {zoneGroupId} is Hero/faction-only - pass 'nuia' or 'haranya', e.g. /claimterritory {zoneGroupId} nuia");
                return;
            }

            var factionDominion = DominionManager.Instance.ClaimTerritoryForFaction(zoneGroupId, factionId, character);
            if (factionDominion == null)
            {
                CommandManager.SendErrorText(this, messageOutput,
                    $"Failed to claim zone group {zoneGroupId} - this build has no lodestone House for it.");
                return;
            }

            CommandManager.SendNormalText(this, messageOutput,
                $"Zone group {zoneGroupId} claimed for {factionId}.");
            return;
        }

        var expedition = args.Length >= 2
            ? ExpeditionManager.Instance.Expeditions.FirstOrDefault(
                e => string.Equals(e.Name, args[1], StringComparison.OrdinalIgnoreCase))
            : character.Expedition;

        if (expedition == null)
        {
            CommandManager.SendErrorText(this, messageOutput, args.Length >= 2
                ? $"No guild named '{args[1]}' found."
                : "You are not in a guild - specify [guildName] instead.");
            return;
        }

        var dominion = GuildDominionManager.Instance.ClaimTerritory(zoneGroupId, expedition, character);
        if (dominion == null)
        {
            CommandManager.SendErrorText(this, messageOutput,
                $"Failed to claim zone group {zoneGroupId} - this build has no lodestone House for it.");
            return;
        }

        CommandManager.SendNormalText(this, messageOutput,
            $"Zone group {zoneGroupId} claimed for guild '{expedition.Name}'.");
    }
}

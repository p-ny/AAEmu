using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

/// <summary>
/// Wall/gate progression trigger for the castle system - use skill for the 5 real guard-tower blueprint items
/// found via dev-DLL research 2026-08-19 (Guard Tower 47383, Wooden Wall Stairs 47315, Castle Gate 47314, Castle
/// Wall 47313, Defense Tower 47306 - all share use_skill_id=41079, sold by the Territory Agent NPC's merchant
/// pack 377 for item 41488). That skill has zero skill_effects rows in the shipped client data (a real content
/// gap, not a research miss) - this class + the matching effects/special_effects/skill_effects rows inserted
/// into the server's own compact.sqlite3 (not the client copies - the client doesn't need to see this row to
/// already let the item be cast, per how DeclareDominion's own skill_effects chain works) are what makes using
/// a purchased blueprint actually advance DominionManager.AdvanceGuardTowerStep. Item consumption is NOT done
/// here in C# (unlike DeclareDominion's manual backpack consume - that item is equipped, these are plain
/// inventory items with no SkillObject-carried reference back to which one was used) - the inserted
/// skill_effects row instead sets consume_source_item=1/consume_item_count=1, which Skill.cs's own generic
/// pipeline (see its ConsumeSourceItem handling) already resolves and consumes correctly for every one of the
/// 5 shared-skill items without this class needing to know which template id was actually cast.
///
/// Eligibility (fixed 2026-08-23 - see DominionData.OwningFactionId's doc comment): a dominion is either
/// guild-owned (old system, Exeloch/Sungold - ExpeditionId set, OwningFactionId 0) or Hero/faction-owned (new
/// system - OwningFactionId set). The two are mutually exclusive, so the check branches: guild-owned requires
/// only Expedition membership matching dominion.ExpeditionId (no Hero concept applies there at all - the
/// previous version wrongly demanded Hero status here too, which could never be satisfied); faction-owned
/// requires the caster's own faction to match dominion.OwningFactionId AND HeroManager.IsCurrentHero - this
/// also closes the earlier-flagged "any Hero of the owning faction, not just the declaring guild's members"
/// gap, since IsCurrentHero already checks per-faction internally once we've confirmed it's the right faction.
/// </summary>
public class AdvanceGuardTowerStep : SpecialEffectAction
{
    protected override SpecialType SpecialEffectActionType => SpecialType.AdvanceGuardTowerStep;

    public override void Execute(BaseUnit caster,
        SkillCaster casterObj,
        BaseUnit target,
        SkillCastTarget targetObj,
        CastAction castObj,
        Skill skill,
        SkillObject skillObject,
        DateTime time,
        int value1,
        int value2,
        int value3,
        int value4)
    {
        if (caster is not Character character)
            return;

        // 2026-08-23 temporary diagnostic logging - see AdvanceCastleTier.cs's matching comment. Remove once
        // the current "still rejected after a fresh claim" investigation is resolved.
        Logger.Info("AdvanceGuardTowerStep: {0} cast, casterObj={1}", character.Name,
            casterObj is SkillItem si ? $"SkillItem(ItemTemplateId={si.ItemTemplateId})" : casterObj?.GetType().Name ?? "null");

        var zone = ZoneManager.Instance.GetZoneByKey(character.Transform.ZoneId);
        if (zone == null)
        {
            Logger.Info("AdvanceGuardTowerStep: {0} rejected - ZoneManager could not resolve zone for ZoneId={1}",
                character.Name, character.Transform.ZoneId);
            return;
        }

        var zoneId = (ushort)zone.GroupId;
        var pos = character.Transform.World.Position;

        // 2026-08-24: guild-owned (Exeloch/Sungold) and Hero/faction-owned (the 4 siege_zones territories) are
        // two fully separate systems now (GuildDominionManager / DominionManager), each with its own claim
        // registry - the two systems' zone groups are disjoint, so checking guild first and falling through to
        // Hero/faction is safe, not just an ordering guess. See HousingManager.Build's matching split.
        var isGuildOwned = true;
        var dominion = GuildDominionManager.Instance.GetDominionAtPosition(zoneId, pos.X, pos.Y);
        if (dominion == null)
        {
            isGuildOwned = false;
            dominion = DominionManager.Instance.GetDominionAtPosition(zoneId, pos.X, pos.Y);
        }

        if (dominion == null)
        {
            Logger.Info("AdvanceGuardTowerStep: {0} rejected - GetDominionAtPosition(zoneId={1}, x={2}, y={3}) returned null",
                character.Name, zoneId, pos.X, pos.Y);
            character.SendErrorMessage(ErrorMessageType.Invalid);
            return;
        }
        Logger.Info("AdvanceGuardTowerStep: {0} dominion found - ExpeditionId={1}, OwningFactionId={2}",
            character.Name, dominion.ExpeditionId, dominion.OwningFactionId);

        // 2026-08-27: guild branch tightened to guild-leader-only (Expedition.OwnerId), per the user's explicit
        // design call - the guild leader manages the territory (advances guard tower steps), ordinary members
        // do not. Mirrors the Hero-only requirement the faction branch already had.
        var hasPermission = isGuildOwned
            ? character.Expedition != null && (uint)character.Expedition.Id == dominion.ExpeditionId
              && character.Id == character.Expedition.OwnerId
            : character.Faction != null && (uint)character.Faction.Id == dominion.OwningFactionId
              && HeroManager.Instance.IsCurrentHero(character);

        if (!hasPermission)
        {
            Logger.Info("AdvanceGuardTowerStep: {0} rejected - hasPermission false (character.Expedition={1}, character.Faction={2})",
                character.Name, character.Expedition?.Id.ToString() ?? "null", character.Faction?.Id.ToString() ?? "null");
            character.SendErrorMessage(ErrorMessageType.NoPerm);
            return;
        }

        var newStep = isGuildOwned
            ? GuildDominionManager.Instance.AdvanceGuardTowerStep(zoneId)
            : DominionManager.Instance.AdvanceGuardTowerStep(zoneId);
        Logger.Debug("AdvanceGuardTowerStep: {0} advanced zone {1} to step {2}", character.Name, zoneId, newStep);

        // 2026-08-21: some of the 24 items sharing this skill are real buildable structures (item_housings has
        // a design for them - Guardian Altar, Farm, Workshop, Warehouse, Overseer Post, Wall, Gate, Tower -
        // see TryBuildDominionStructure's own doc comment for the full data trail), others (older/leftover
        // items with no item_housings row) are not - TryBuildDominionStructure itself no-ops (returns null)
        // for the latter, so no per-item branching is needed here beyond reading which item was actually cast.
        if (casterObj is SkillItem { ItemTemplateId: > 0 } skillItem)
        {
            if (isGuildOwned)
                GuildDominionManager.Instance.TryBuildDominionStructure(zoneId, skillItem.ItemTemplateId, character);
            else
                DominionManager.Instance.TryBuildDominionStructure(zoneId, skillItem.ItemTemplateId, character);
        }
    }
}

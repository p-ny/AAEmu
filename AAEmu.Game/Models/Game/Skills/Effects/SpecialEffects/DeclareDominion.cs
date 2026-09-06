using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

public class DeclareDominion : SpecialEffectAction
{
    protected override SpecialType SpecialEffectActionType => SpecialType.DeclareDominion;

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
        if (caster is Character) { Logger.Debug("Special effects: DeclareDominion value1 {0}, value2 {1}, value3 {2}, value4 {3}", value1, value2, value3, value4); }

        var declarer = caster as Character;

        // Two valid targets:
        //  - House: the zone group's Guard Tower structure already exists (a previous owner lost it in a
        //    siege) - re-claim by transferring ownership, same as before this pass.
        //  - Doodad: the very first claim on a zone group. Only a native "정화의 수호탑 소환지점" (Purification
        //    Guard Tower Summon Point) doodad exists at this point - baked into the CryEngine level, never
        //    tracked by AAEmu (no `doodad_spawners` table for it), using the "buried expedition tower" model.
        //    Not whitelisting the doodad's exact template id here (many numbered variants exist per region,
        //    ~50+ across the 4 zone groups, not fully enumerated) - trusting the skill's own client-side range/
        //    target restrictions rather than re-verifying the doodad's identity server-side, a deliberate
        //    simplification. HousingManager.CreateDominionHouse spawns the real House at the doodad's position.
        House existingHouse = target as House;
        Doodad marker = target as Doodad;
        if (existingHouse == null && (marker == null || declarer == null))
            return;

        var zoneId = (ushort)ZoneManager.Instance.GetZoneByKey(
            existingHouse != null ? existingHouse.Transform.ZoneId : marker.Transform.ZoneId).GroupId;

        // 2026-08-24: GM-controlled "no new claims" lock (see IDominionZoneLockManager) - checked before
        // anything else here, covering both the first-claim (marker) and re-claim-after-loss (existingHouse)
        // paths uniformly, for either castle system.
        if (DominionZoneLockManager.Instance.IsLocked(zoneId))
        {
            declarer?.SendErrorMessage(ErrorMessageType.Invalid);
            return;
        }

        // 2026-08-25: channeling a lodestone (either a first claim or a recapture) requires the caster to be
        // wearing the "Archeum of Purification" backpack (정화의 아키움, item 16493) - this was never actually
        // enforced server-side (skill 13661 carries no skill_reagents/link_equip_slot_id row in game data for
        // it, so nothing gated the cast), only assumed by the unconditional consume at the bottom of this
        // method - meaning the channel would previously succeed (and the territory get claimed) with nothing
        // equipped at all, silently failing to consume only because GetItemBySlot returned null there. Checked
        // here, before anything with side effects (House creation, Declare/DeclareForFaction) runs.
        Item backpack = null;
        if (declarer != null)
        {
            backpack = declarer.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
            if (backpack == null || backpack.TemplateId != 16493)
            {
                declarer.SendErrorMessage(ErrorMessageType.MustEquipProperItem);
                return;
            }
        }

        // Ownership model split 2026-08-21: zone groups with a real siege_zones schedule row (33/34/43/44 -
        // Salpimari/Nuimari/Marcala/Calmlands) can only be claimed by the Nuia/Haranya alliance factions via
        // that faction's currently-elected Hero, not by an arbitrary guild - guild membership is irrelevant
        // here, matching AdvanceGuardTowerStep's own already-established Hero-eligibility check. Zone groups
        // with no siege_zones row (54/56 - Exeloch/Sungold Fields) stay guild-owned, unchanged from before.
        var isFactionTerritory = SiegeGameData.Instance.GetSiegeZoneSchedule(zoneId) != null;

        uint expeditionId = 0;
        uint owningFactionId = 0;
        if (isFactionTerritory)
        {
            if (declarer == null || !HeroManager.Instance.IsCurrentHero(declarer))
            {
                declarer?.SendErrorMessage(ErrorMessageType.NoPerm);
                return;
            }

            owningFactionId = (uint)DominionManager.ResolveOwningFaction(declarer);
            if (owningFactionId == 0)
            {
                declarer.SendErrorMessage(ErrorMessageType.NoPerm);
                return;
            }
        }
        else
        {
            if (((Unit)caster).Expedition == null)
                return;
            expeditionId = (uint)((Unit)caster).Expedition.Id;
        }

        // Check the declare window BEFORE creating anything - a brand new House must not get spawned only to
        // then be rejected for bad timing.
        if (!SiegeManager.Instance.IsDeclareDominionWindowOpen(zoneId, DateTime.UtcNow))
        {
            declarer?.SendErrorMessage(ErrorMessageType.DominionNotDeclareTime);
            return;
        }

        House lodestone;
        if (existingHouse != null)
        {
            lodestone = existingHouse;
        }
        else
        {
            // Reject an already-claimed zone group BEFORE spawning anything. A zone group's map geometry
            // bakes in 2-3 native "Guard Tower Summon Point" doodads (guard_tower_settings has up to 3
            // candidate spots per region, e.g. Salpimari's left/center/right) but only ONE gets wired to a
            // real housings template (see GetLodestoneHousingTemplateId's comment) - the other native
            // markers stay in the world forever (never AAEmu-tracked, nothing to despawn them) and remain
            // targetable by this skill. Without this guard, interacting with one of those leftover markers
            // after the zone group's real spot was already claimed would spawn a brand-new orphaned House
            // here and only get rejected afterward, by DominionManager.Declare's own already-claimed check.
            // 2026-08-24: check both managers - a not-yet-known-branch check (isFactionTerritory decides which
            // one actually owns this zone group below), so both must be consulted here.
            if (DominionManager.Instance.GetByZoneId(zoneId) != null || GuildDominionManager.Instance.GetByZoneId(zoneId) != null)
            {
                declarer?.SendErrorMessage(ErrorMessageType.DominionAlreadyDedclared);
                return;
            }

            var templateId = SiegeGameData.Instance.GetLodestoneHousingTemplateId(zoneId);
            if (templateId == null)
                return; // this zone group has no claimable Guard Tower in this build's data

            lodestone = HousingManager.Instance.CreateDominionHouse(
                templateId.Value, declarer, declarer.ParentWorld,
                marker.Transform.World.Position.X, marker.Transform.World.Position.Y, marker.Transform.World.Position.Z);
            if (lodestone == null)
                return;
        }

        // DominionManager rejects (and messages the declarer) if zoneId is already claimed - see
        // ErrorMessageType.DominionAlreadyDedclared. Territory radii/gates/walls are resolved from the
        // lodestone's HousingTemplate.GuardTowerSettingId against guard_tower_settings, not hardcoded.
        // Known race, not handled: if two players declare on the same never-claimed zone group's doodad at
        // the same moment, the loser's freshly-created House (from the branch above) is orphaned - rejected
        // here but never cleaned up. Rare in practice (needs two simultaneous first-declares on the same zone
        // group), not worth a two-phase create/commit for this pass.
        // 2026-08-24: guild-owned claims (54/56) now go through GuildDominionManager's own Declare, not
        // DominionManager's - see that method's doc comment for why (DominionManager no longer tracks these
        // zones at all after tonight's guild/Hero-faction split).
        var dominion = isFactionTerritory
            ? DominionManager.Instance.DeclareForFaction(zoneId, owningFactionId, lodestone, declarer)
            : GuildDominionManager.Instance.Declare(zoneId, expeditionId, lodestone, declarer);
        if (dominion == null)
            return;

        if (declarer != null)
        {
            declarer.Inventory.Equipment.ConsumeItem(ItemTaskType.SkillReagents, backpack.TemplateId, 1, backpack);
        }
    }
}

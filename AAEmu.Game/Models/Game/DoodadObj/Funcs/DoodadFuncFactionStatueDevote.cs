using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

/// <summary>
/// The 3 per-faction capital-city Hero Statue doodads (2026-08-31 - the "Hero statue" feature requested
/// by the user): Nuia's "안드리온 2세 석상"/King Andrion II Statue (doodad_almighty 10588, placed in
/// Marianople), Haranya's "아마렌드라 4세 석상"/Amarendra IV Statue (10650), and the Pirates'
/// "모르페우스 석상"/Morpheus Statue (10651) - all 3 confirmed placed in `doodad_spawns.json`.
///
/// 2026-08-31 correction - the earlier "~7-day decay, no new code needed" claim below was WRONG, verified
/// by re-checking every relevant `doodad_func_groups`/`doodad_funcs`/`doodad_func_growths`/
/// `doodad_func_timers` row directly (not re-guessed): the REAL linear construction chain, confirmed
/// unambiguous via `doodad_funcs.next_phase` (Haranya shown, Nuia/Pirate identical shape): Hero devotes 5
/// (start group) → any player devotes 90 → Hero devotes 5 → "완공"/Complete. That part is correct and
/// already working. **But nothing anywhere in the checked data ever transitions a Complete statue INTO
/// its "빛을 잃기 전"/"before losing light" or "빛을 잃은"/decayed groups** - searched every
/// `doodad_funcs.next_phase` and every `doodad_func_growths.next_phase` value in the whole database for a
/// reference to those group ids and found zero hits. **The decay-onset trigger does not exist in this
/// codebase or in the shipped client data at all** - it would need to be a genuinely new scheduled
/// server-side check (e.g. "how long has this statue sat in the Complete group's PhaseTime" → force a
/// phase change), not something that already fires on its own. The only confirmed long-duration figure
/// anywhere in this chain is 604,800,000ms (~7 days) on the ALREADY-decayed "15x general-player restore"
/// group's own `DoodadFuncGrowth` (Haranya/Pirate ids 2066/2072) - but that func's own `next_phase=-1`
/// (self, no further auto-transition), so even this doesn't obviously encode "statue expires after 7
/// days" - its real purpose is unclear (possibly just a cosmetic cap, unconfirmed). **The user's
/// remembered "up for 28-30 days, small ~5-pack rebuild" figures could NOT be confirmed anywhere in this
/// data** - no duration close to 28-30 days exists in any timer table checked, and the only "5"-count
/// phases found are the Hero-only construction/finish steps (already implemented), not a separate
/// maintenance action - the real "keep it lit" action turned out to be **15x**, general-player, item
/// 39457 (`doodad_func_devotes` groups 30054/30062 - Nuia's own equivalent, 29852/29858, is a genuinely
/// different/incomplete shape than Haranya/Pirate's clean pattern, an existing, real data-authoring
/// irregularity, not something this pass invented or fixed). Don't trust the 28-30-day/5-pack figures as
/// confirmed; they need either a live-retail source to pin down or an explicit design decision, not
/// another guess from this data alone.
///
/// This subclass adds exactly the two things the generic <see cref="DoodadFuncDevote"/> cannot express on
/// its own, both confirmed directly from the shipped data (not invented):
/// 1. The construction/restoration-start and restoration-finish phases are explicitly Hero-only per the
///    `doodad_func_groups.comment` column's own text ("...영웅 5회" = "...Hero, 5 times"). Every Hero-only
///    phase across all 3 factions has `Count == 5`; the 90x general-player phase and the 15x restoration
///    phase never do - gating on <see cref="DoodadFuncDevote.Count"/> == 5 is a real, data-derived
///    invariant (checked across all 3 factions' full chains), not a magic number. The `doodad_func_devotes`
///    row id itself isn't retained on the loaded template instance, so this is the only signal available
///    without adding new plumbing to the generic loader.
/// 2. Reaching one of the 3 real "완공"/complete groups (Nuia 29854, Haranya 30053, Pirate 30061) grants
///    buff 32024 "전쟁의 준비"/War Preparation (its own in-game description names "세력 석상"/"Faction
///    Statue" as the source - PvP resist/crit resist/siege dmg reduction/healing) to the character whose
///    devotion completed it - ONLY that character, per the user's explicit correction (2026-08-31: "the
///    buff should only get applied to the player not all faction players at once - that would be
///    overkill"). Originally shipped faction-wide to every online member; narrowed to the single
///    completing player.
///
///    2026-08-31 update: the completion phase groups also carry 2 real, separate, per-player "grace"
///    skills (48777/48778, "Andrion II's Intense/Steadfast Grace" - see DoodadFuncPersistentUse, plain
///    unmodified DoodadFuncUse rows, no skill_reagents cost) that grant buffs 39925/39926 - confirmed
///    2026-09-05 to be personal Andrion-blessing buffs, unrelated to buff 32024 and to the Mobilization
///    Order time-state mechanic below. This class's own 32024 grant on completion is a real, separate,
///    universal reward and was not touched.
/// </summary>
/// <remarks>
/// Wired via a data patch (`doodad_funcs.actual_func_type`) rather than touching the generic
/// <see cref="DoodadFuncDevote"/> class, so the unrelated Auroria-reclamation devote mechanic (bases,
/// walls, purification monoliths) that also uses that class is completely unaffected.
///
/// 2026-09-05: the completion groups carry a THIRD family of skills (Nuia 40152/40176, Haranya
/// 50165/50169, Pirate 43724, "Start of Leap/War/Plunder") granting the real per-state empowerment
/// buffs (Leap 23717, Battle 32025, Plunder 26100 - all distinct from this class's own 32024) - these are
/// the "buff you get from the statue is increased" mechanic described by the user, gated on a Hero having
/// declared the matching Mobilization Order flag state. See DoodadFuncFactionStatueTimeGatedUse.cs and
/// HeroManager.SetMobilizationTimeState/GetActiveMobilizationTimeState - this class's own completion
/// grant is unrelated and unaffected.
/// </remarks>
public class DoodadFuncFactionStatueDevote : DoodadFuncDevote
{
    private const int HeroOnlyCount = 5;

    // doodad_almighty ids for the 3 factions' capital statues (Nuia/Haranya/Pirate). Internal, not
    // private - SpawnManager.SpawnAll needs this to know which world-decoration spawns to restore
    // saved phase/progress for on boot (see LoadState/SaveState below).
    internal static readonly HashSet<uint> TemplateIds = [10588, 10650, 10651];

    // doodad_func_groups.id values for the 3 factions' "완공"/complete state. Internal, not private -
    // GM tooling (/statue) needs this to replicate the completion-buff-grant when force-advancing a
    // phase without going through a real Use() call.
    internal static readonly HashSet<int> CompleteGroupIds = [29854, 30053, 30061];

    private const uint FactionStatueBuffId = 32024; // 전쟁의 준비 / War Preparation

    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        if (caster is not Character character)
            return;

        if (Count == HeroOnlyCount && !HeroManager.Instance.IsCurrentHero(character))
        {
            character.SendErrorMessage(ErrorMessageType.Invalid);
            Logger.Debug($"DoodadFuncFactionStatueDevote: {character.Name} tried a Hero-only statue phase (doodad {owner.TemplateId}, objId {owner.ObjId}) without being the current Hero");
            return;
        }

        base.Use(caster, owner, skillId, nextPhase);

        // This doodad is a world-decoration spawn (doodad_spawns.json, IsPersistent=false by design -
        // same as every other ambient object) so its FuncGroupId/Data setters never auto-save. Persist
        // explicitly to the dedicated faction_statues table (see SpawnManager.SpawnAll for the restore
        // side) so construction/decay progress survives a World restart instead of resetting to phase 1
        // every time - real, live-observed problem 2026-08-31 (many restarts that night each wiped
        // progress). `nextPhase` is used instead of owner.FuncGroupId when a transition is happening,
        // because CompleteFunc (Doodad.cs) only applies the real FuncGroupId change AFTER this Use()
        // call returns - owner.FuncGroupId here would still read the OLD phase mid-transition.
        var groupToSave = owner.ToNextPhase && nextPhase > 0 ? (uint)nextPhase : owner.FuncGroupId;
        SaveState(owner.TemplateId, groupToSave, owner.Data);

        if (owner.ToNextPhase && CompleteGroupIds.Contains(nextPhase))
        {
            GrantFactionStatueBuff(character);
        }
    }

    /// <summary>Upserts this statue's current phase/progress - see the Use() call site's comment.</summary>
    private static void SaveState(uint templateId, uint funcGroupId, int data)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO faction_statues (template_id, func_group_id, data)
            VALUES (@t, @g, @d)
            ON DUPLICATE KEY UPDATE func_group_id=@g, data=@d
            """;
        command.Parameters.AddWithValue("@t", templateId);
        command.Parameters.AddWithValue("@g", funcGroupId);
        command.Parameters.AddWithValue("@d", data);
        command.Prepare();
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Looks up a saved phase/progress row for one of the 3 statue template ids - called once per
    /// statue at world boot (see SpawnManager.SpawnAll) to resume construction/decay state instead of
    /// respawning fresh at phase 1 every restart. Returns null if this statue has never been touched
    /// (a genuinely fresh server, or - same effective case - one that's never had a real devotion yet).
    /// </summary>
    internal static (uint FuncGroupId, int Data)? LoadState(uint templateId)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT func_group_id, data FROM faction_statues WHERE template_id=@t";
        command.Parameters.AddWithValue("@t", templateId);
        command.Prepare();
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return ((uint)reader.GetInt32(0), reader.GetInt32(1));
    }

    /// <summary>
    /// Grants the completion buff to a single character - the one whose devotion completed the statue.
    /// NOT faction-wide (see this class's doc comment for why that was wrong and got corrected).
    /// </summary>
    internal static void GrantFactionStatueBuff(Character character)
    {
        if (character == null)
            return;

        character.Buffs.AddBuff(FactionStatueBuffId, character);
        Logger.Info($"DoodadFuncFactionStatueDevote: {character.Name}'s devotion completed their faction's statue - granted buff {FactionStatueBuffId}");
    }
}

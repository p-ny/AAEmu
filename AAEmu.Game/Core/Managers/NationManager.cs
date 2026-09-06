using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Expeditions;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.StaticValues;

using NLog;

namespace AAEmu.Game.Core.Managers;

/// <summary>
/// Founds independent player nations on top of a claimed Dominion once its owning Expedition completes the
/// founding quest chain (game_decrypted.sqlite3 quest_context 7983-7986, category 96 "Countries") - see
/// D:\aa\siege-castle-hero-nation-brief.md, System 4 addendum, for the full research trail.
///
/// Deliberate simplification, flagged clearly: the client models a further in-person ceremony after quest 7986
/// completes (item 40232 "독립선언문", read at a palace throne, gated on a 50-player "/맹세" vow buff, blocked
/// during siege) via skill 32992/buff 17947 - but that skill has zero rows in `skill_effects` in this build's
/// shipped data, and the buff's own effect chain traces to a generic "use" world-interaction, not to
/// SpecialEffectType.DeclareIndependence (=71, confirmed present in the C# enum but with NO
/// `special_effects` row anywhere in this build using that type id). Rather than guess at wiring a native
/// trigger this data doesn't actually configure, nation founding is triggered here at quest-completion
/// instead - the character still sees the full cosmetic ceremony client/zone-side (all of that content is real
/// and already works natively), but the server-authoritative "this Dominion is now a nation" state change
/// happens on turning in the Great Prosperity Seal rather than on a later reading-ceremony completion signal
/// this pass couldn't reliably hook. DeclareIndependence.cs (the SpecialEffectAction) calls the same method
/// here, so if that native wiring is ever found/fixed in the design data, it converges on identical logic
/// without further server changes.
/// </summary>
// IFactionManager/IGameDataManager are constructor-declared purely for load ordering (unused otherwise) - Load()
// below re-registers every existing nation's faction with FactionManager/HeroGameData on every boot, both of
// which must already be populated first. Same fix pattern as DominionManager's own IGameDataManager dependency
// (ManagerOrchestrator only orders by constructor parameters, not by actual data dependencies).
public class NationManager(IDominionManager dominionManager, IGuildDominionManager guildDominionManager, ISiegeManager siegeManager, IFactionManager factionManager, IGameDataManager gameDataManager) : Singleton<NationManager>, INationManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <summary>quest_contexts.id for "국가의 상징" / "A Nation is Born".</summary>
    private const uint FoundingQuestId = 7986;

    /// <summary>
    /// Old (guild-owned) castle system territories only - Exeloch and Sungold Fields, confirmed 2026-08-21 to be
    /// the two Auroria territories with zero siege_zones/siege_plans rows (the other 4 use the separate Hero/
    /// faction guard-tower system and are not nation-foundable this way). See AdvanceCastleTier.cs.
    /// </summary>
    private static readonly HashSet<ushort> NationFoundableZoneGroups = [54, 56];

    /// <summary>
    /// Player-nation faction ids are allocated far above both the system_factions range (max observed 221) and
    /// the expeditions/guild id range (max observed ~1001) so they can never collide with either - both share
    /// the same FactionsEnum id space in this codebase (Expedition : SystemFaction). zoneId is added on top so
    /// each of the (currently 2) nation-foundable territories gets a deterministic, easy-to-recognize id.
    /// </summary>
    private const uint NationFactionIdBase = 900000;

    /// <summary>hero_rewards template faction id ("국가 독립 기본 세력 설정" / Nation Independence Default Force Configuration) cloned onto every newly-founded nation's own real faction id - see HeroGameData.CloneRewardsForNewFaction.</summary>
    private const uint HeroRewardTemplateFactionId = 166;

    private sealed class NationRecord
    {
        public ushort ZoneId { get; init; }
        public uint SovereignCharacterId { get; set; }
        public uint FactionId { get; set; }
        public uint FoundingExpeditionId { get; init; }
        public string Name { get; set; }
        public RelationState RelationNuia { get; set; } = RelationState.Neutral;
        public RelationState RelationHaranya { get; set; } = RelationState.Neutral;
        public DateTime DeclaredAt { get; init; }
    }

    private Dictionary<ushort, NationRecord> _nations = [];

    public void Load()
    {
        _nations = [];

        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM nations";
        command.Prepare();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var zoneId = (ushort)reader.GetInt32(reader.GetOrdinal("zone_id"));
            var nameOrdinal = reader.GetOrdinal("name");
            _nations[zoneId] = new NationRecord
            {
                ZoneId = zoneId,
                SovereignCharacterId = (uint)reader.GetInt32(reader.GetOrdinal("sovereign_character_id")),
                FactionId = (uint)reader.GetInt32(reader.GetOrdinal("faction_id")),
                FoundingExpeditionId = (uint)reader.GetInt32(reader.GetOrdinal("founding_expedition_id")),
                Name = reader.IsDBNull(nameOrdinal) ? null : reader.GetString(nameOrdinal),
                RelationNuia = (RelationState)reader.GetByte(reader.GetOrdinal("relation_nuia")),
                RelationHaranya = (RelationState)reader.GetByte(reader.GetOrdinal("relation_haranya")),
                DeclaredAt = reader.GetDateTime(reader.GetOrdinal("declared_at"))
            };
        }

        Logger.Info("Loaded {0} nations", _nations.Count);

        // Re-register every existing nation's faction with FactionManager/HeroGameData on every boot - both are
        // in-memory-only registrations (see DeclareIndependence), so a fresh World process starts with none of
        // them until this runs. Idempotent (AddFaction/CloneRewardsForNewFaction both no-op on a second call).
        foreach (var nation in _nations.Values)
        {
            if (nation.FactionId == 0)
                continue;

            var faction = new SystemFaction
            {
                Id = (FactionsEnum)nation.FactionId,
                MotherId = FactionsEnum.Invalid,
                Name = string.IsNullOrEmpty(nation.Name) ? $"Nation of Zone {nation.ZoneId}" : nation.Name,
                OwnerId = nation.SovereignCharacterId,
                OwnerName = NameManager.Instance.GetCharacterName(nation.SovereignCharacterId) ?? "",
                UnitOwnerType = 0,
                PoliticalSystem = 1,
                AggroLink = true,
                GuardHelp = true,
                DiplomacyTarget = true
            };
            FactionManager.Instance.AddFaction(faction);
            HeroGameData.Instance.CloneRewardsForNewFaction(HeroRewardTemplateFactionId, nation.FactionId);
            ApplyAllianceRelations(faction, nation);
        }
    }

    /// <summary>
    /// Sets the nation's own faction.Relations toward Nuia/Haranya. Deliberately one-directional (only the
    /// nation's own faction object is mutated, never Nuia's/Haranya's) - SystemFaction.GetRelationState already
    /// falls back to checking the OTHER faction's stored relation when the caller has none set (see its
    /// TryGetStoredRelation calls), so this alone is enough for both "nation checks relation to Nuia" and
    /// "a Nuia-faction NPC checks relation to the nation" to agree - confirmed by reading GetRelationState's own
    /// logic, not assumed.
    /// </summary>
    private static void ApplyAllianceRelations(SystemFaction faction, NationRecord nation)
    {
        faction.Relations[FactionsEnum.NuiaAlliance] = new FactionRelation
        {
            Id = faction.Id,
            Id2 = FactionsEnum.NuiaAlliance,
            State = nation.RelationNuia
        };
        faction.Relations[FactionsEnum.HaranyaAlliance] = new FactionRelation
        {
            Id = faction.Id,
            Id2 = FactionsEnum.HaranyaAlliance,
            State = nation.RelationHaranya
        };
    }

    public bool SetAllianceRelation(Character sovereign, FactionsEnum target, RelationState state)
    {
        if (target != FactionsEnum.NuiaAlliance && target != FactionsEnum.HaranyaAlliance)
            return false;

        var myZoneId = GetNationOfSovereign(sovereign?.Id ?? 0);
        if (myZoneId == null || !_nations.TryGetValue(myZoneId.Value, out var nation))
            return false;

        if (target == FactionsEnum.NuiaAlliance)
            nation.RelationNuia = state;
        else
            nation.RelationHaranya = state;

        using (var connection = MySQL.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = target == FactionsEnum.NuiaAlliance
                ? "UPDATE nations SET relation_nuia=@s WHERE zone_id=@z"
                : "UPDATE nations SET relation_haranya=@s WHERE zone_id=@z";
            command.Parameters.AddWithValue("@s", (byte)state);
            command.Parameters.AddWithValue("@z", myZoneId.Value);
            command.Prepare();
            command.ExecuteNonQuery();
        }

        if (nation.FactionId != 0)
        {
            var faction = FactionManager.Instance.GetFaction((FactionsEnum)nation.FactionId);
            if (faction != null)
                ApplyAllianceRelations(faction, nation);
        }

        Logger.Info("Nation zone {0} set relation to {1} = {2}", myZoneId.Value, target, state);
        return true;
    }

    public RelationState GetAllianceRelation(ushort zoneId, FactionsEnum target)
    {
        if (!_nations.TryGetValue(zoneId, out var nation))
            return RelationState.Neutral;

        return target switch
        {
            FactionsEnum.NuiaAlliance => nation.RelationNuia,
            FactionsEnum.HaranyaAlliance => nation.RelationHaranya,
            _ => RelationState.Neutral
        };
    }

    /// <summary>
    /// The player-chosen nation name, or null if never set. No live client-facing trigger currently delivers
    /// player-entered text into the founding flow (see DeclareIndependence's doc comment) - this exists so a
    /// name can be recorded server-side (e.g. via a GM command) even though SCDominionDataPacket/DominionData
    /// has no wire field to display it back to the client with yet, confirmed by direct RE this session.
    /// </summary>
    public string GetNationName(ushort zoneId) => _nations.TryGetValue(zoneId, out var record) ? record.Name : null;

    public bool SetNationName(ushort zoneId, string name)
    {
        if (!_nations.TryGetValue(zoneId, out var record))
            return false;

        record.Name = name;

        using (var connection = MySQL.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE nations SET name=@n WHERE zone_id=@z";
            command.Parameters.AddWithValue("@n", (object)name ?? DBNull.Value);
            command.Parameters.AddWithValue("@z", zoneId);
            command.Prepare();
            command.ExecuteNonQuery();
        }

        // Keep the live in-memory faction (and anyone who already has it cached client-side) in sync - Name is
        // real wire data on this packet (see SystemFaction.Write), not display-only bookkeeping.
        if (record.FactionId != 0)
        {
            var faction = FactionManager.Instance.GetFaction((FactionsEnum)record.FactionId);
            if (faction != null)
            {
                faction.Name = name;
                var sovereignChar = WorldManager.Instance.GetCharacterById(record.SovereignCharacterId);
                WorldManager.Instance.BroadcastPacketToServer(new SCFactionCreatedPacket(
                    faction,
                    sovereignChar?.ObjId ?? 0,
                    sovereignChar != null ? [(sovereignChar.ObjId, sovereignChar.Id, sovereignChar.Name)] : []));
            }
        }

        Logger.Info("Nation zone {0} renamed to '{1}'", zoneId, name);
        return true;
    }

    public bool IsNation(ushort zoneId) => _nations.ContainsKey(zoneId);

    public uint? GetSovereign(ushort zoneId) => _nations.TryGetValue(zoneId, out var record) ? record.SovereignCharacterId : null;

    public void OnQuestCompleted(Character character, uint questContextId)
    {
        if (questContextId != FoundingQuestId)
            return;

        DeclareIndependence(character);
    }

    public void DeclareIndependence(Character character)
    {
        if (character?.Expedition == null)
        {
            Logger.Warn("DeclareIndependence: {0} has no Expedition to found a nation for", character?.Name);
            return;
        }

        // 2026-08-27: only the guild leader may found a nation (and so become its Sovereign), per the user's
        // explicit design call - matches the same guild-leader-only gate now applied to every other guild
        // dominion management action (build/demolish/tax/guard-tower/castle-tier).
        if (character.Id != character.Expedition.OwnerId)
        {
            Logger.Warn("DeclareIndependence: {0} is not the guild leader of their Expedition, cannot found a nation", character.Name);
            character.SendErrorMessage(ErrorMessageType.NoPerm);
            return;
        }

        var expeditionId = (uint)character.Expedition.Id;
        // 2026-08-24: nation-founding is exclusively scoped to zone 54/56 (NationFoundableZoneGroups below),
        // the guild-owned old castle system - since that system's live state moved out of DominionManager into
        // GuildDominionManager tonight, the candidate search has to look there, not in dominionManager.Dominions
        // (which no longer holds Exeloch/Sungold at all).
        DominionData dominion = null;
        foreach (var candidate in guildDominionManager.GuildDominions)
        {
            if (candidate.ExpeditionId != expeditionId)
                continue;
            dominion = candidate;
            break;
        }

        if (dominion == null)
        {
            Logger.Warn("DeclareIndependence: {0}'s Expedition holds no claimed Dominion to found a nation on", character.Name);
            return;
        }

        if (!NationFoundableZoneGroups.Contains(dominion.ZoneId))
        {
            Logger.Warn("DeclareIndependence: {0}'s claimed Dominion (zone {1}) is not one of the old-system territories a nation can be founded on", character.Name, dominion.ZoneId);
            character.SendErrorMessage(ErrorMessageType.Invalid);
            return;
        }

        // Palace tier (3) required - see AdvanceCastleTier.cs / the item 40568 "축성 도면: 영지 궁전" description,
        // which explicitly states this tier is what enables declaring a nation. Read from GuildDominionManager -
        // see the search loop above for why (the tier counter lives there too now, not on dominionManager).
        if (guildDominionManager.GetCastleTier(dominion.ZoneId) < 3)
        {
            character.SendErrorMessage(ErrorMessageType.Invalid);
            return;
        }

        if (_nations.ContainsKey(dominion.ZoneId))
            return; // already independent - idempotent if this ever fires twice

        var expedition = character.Expedition;
        var nationFactionId = NationFactionIdBase + dominion.ZoneId;

        var record = new NationRecord
        {
            ZoneId = dominion.ZoneId,
            SovereignCharacterId = character.Id,
            FactionId = nationFactionId,
            FoundingExpeditionId = expeditionId,
            DeclaredAt = DateTime.UtcNow
        };
        _nations[dominion.ZoneId] = record;

        using (var connection = MySQL.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "REPLACE INTO nations (zone_id, sovereign_character_id, faction_id, founding_expedition_id, declared_at) VALUES (@z,@s,@f,@fe,@d)";
            command.Parameters.AddWithValue("@z", record.ZoneId);
            command.Parameters.AddWithValue("@s", record.SovereignCharacterId);
            command.Parameters.AddWithValue("@f", record.FactionId);
            command.Parameters.AddWithValue("@fe", record.FoundingExpeditionId);
            command.Parameters.AddWithValue("@d", record.DeclaredAt);
            command.Prepare();
            command.ExecuteNonQuery();
        }

        // Real, unique third-tier faction (peer to Nuia/Haranya/pirates) - not just a label. Registered
        // in-memory only (no system_factions SQLite row - that's shared static reference data, not something
        // to mutate live); Load() above re-registers this on every boot so it survives a restart.
        var faction = new SystemFaction
        {
            Id = (FactionsEnum)nationFactionId,
            MotherId = FactionsEnum.Invalid,
            Name = $"Nation of Zone {dominion.ZoneId}", // renamed via /setnationname; SCFactionCreatedPacket re-broadcast there
            OwnerId = character.Id,
            OwnerName = character.Name,
            UnitOwnerType = 0,
            PoliticalSystem = 1,
            AggroLink = true,
            GuardHelp = true,
            DiplomacyTarget = true
        };
        FactionManager.Instance.AddFaction(faction);
        HeroGameData.Instance.CloneRewardsForNewFaction(HeroRewardTemplateFactionId, nationFactionId);
        WorldManager.Instance.BroadcastPacketToServer(new SCFactionCreatedPacket(faction, character.ObjId, [(character.ObjId, character.Id, character.Name)]));

        // Territory ownership: guild -> nation. Same-territory handoff (House/guard-tower-step state untouched).
        dominionManager.TransferToFaction(dominion.ZoneId, nationFactionId);

        // The founding guild is now permanently part of the nation (expeditions.mother, existing dead column -
        // see NationManager's own class doc comment). Other guilds may join later (InviteToNation is
        // per-character, not guild-scoped, per the user's spec - a whole second guild joining "as a guild" isn't
        // built this pass, only individual invites; MotherId still marks this founding guild as nation-bound).
        if (expedition != null)
        {
            expedition.MotherId = (FactionsEnum)nationFactionId;
            ExpeditionManager.Save(expedition);

            foreach (var member in expedition.Members.ToArray())
                JoinNationFaction(member.CharacterId, nationFactionId);
        }

        Logger.Info("Nation declared: zone {0}, faction {1}, Sovereign {2} ({3}), founding guild {4}",
            dominion.ZoneId, nationFactionId, character.Name, character.Id, expeditionId);
    }

    /// <summary>
    /// Moves one character into a nation's temp faction, preserving their real home faction to revert to later
    /// (Unit.SetFaction's own OriginFaction assignment, same proven pattern InstantGame already uses for
    /// battlegrounds) - persisted immediately so it survives a relog, not just left to the periodic autosave.
    /// Works for online and offline characters.
    /// </summary>
    private static void JoinNationFaction(uint characterId, uint nationFactionId)
    {
        var online = WorldManager.Instance.GetCharacterById(characterId);
        if (online != null)
        {
            online.SetFaction((FactionsEnum)nationFactionId);
            online.IsTempFaction = true;
            using var connection = MySQL.CreateConnection();
            using var transaction = connection.BeginTransaction();
            online.Save(connection, transaction);
            transaction.Commit();
            return;
        }

        // Offline: can't resolve a live Faction/OriginFaction object pair without a full Character.Load, so this
        // writes the raw columns directly - origin_faction_id only if not already set (don't clobber an
        // already-in-progress temp-faction state on a character who happens to be offline right now).
        using (var connection = MySQL.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "UPDATE characters SET origin_faction_id = IF(origin_faction_id = 0, faction_id, origin_faction_id), faction_id = @f WHERE id = @id";
            command.Parameters.AddWithValue("@f", nationFactionId);
            command.Parameters.AddWithValue("@id", characterId);
            command.Prepare();
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Reverts one character out of a nation's temp faction back to their real home faction. Works for online and offline characters.</summary>
    private static void LeaveNationFaction(uint characterId)
    {
        var online = WorldManager.Instance.GetCharacterById(characterId);
        if (online != null)
        {
            if (online.OriginFaction != null)
                online.SetFaction(online.OriginFaction.Id);
            online.IsTempFaction = false;
            using var connection = MySQL.CreateConnection();
            using var transaction = connection.BeginTransaction();
            online.Save(connection, transaction);
            transaction.Commit();
            return;
        }

        using (var connection = MySQL.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "UPDATE characters SET faction_id = IF(origin_faction_id != 0, origin_faction_id, faction_id), origin_faction_id = 0 WHERE id = @id";
            command.Parameters.AddWithValue("@id", characterId);
            command.Prepare();
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Invites a Nuia or Haranya character (not already in a nation, not a pirate) into the requester's nation -
    /// requester must be the Sovereign. No client-facing invite-prompt packet exists for this (same class of gap
    /// as guild recruit/search from earlier today) - this applies immediately, GM-tool-style, rather than
    /// guessing an invite/accept wire flow.
    /// </summary>
    public bool InviteToNation(Character sovereign, Character target)
    {
        var myZoneId = GetNationOfSovereign(sovereign.Id);
        if (myZoneId == null || !_nations.TryGetValue(myZoneId.Value, out var nation))
        {
            sovereign.SendErrorMessage(ErrorMessageType.NoPerm);
            return false;
        }

        if (target?.Faction == null || target.IsTempFaction)
            return false;

        if (target.Faction.Id != FactionsEnum.NuiaAlliance && target.Faction.Id != FactionsEnum.HaranyaAlliance)
            return false; // pirates (or anyone not Nuia/Haranya) must leave their own faction first - out of scope here

        if (GetMemberCount(nation.FactionId) >= GetPopulationCap(nation.FactionId))
        {
            sovereign.SendErrorMessage(ErrorMessageType.NoPerm);
            return false;
        }

        JoinNationFaction(target.Id, nation.FactionId);
        return true;
    }

    /// <summary>
    /// Real-world spec (Ascension patch): 100 base (the founding guild's own cap), +50 per additional castle the
    /// nation controls, hard cap 200 at 4 castles. In THIS build's architecture only Exeloch/Sungold Fields are
    /// nation-foundable/transferable (the 4 Hero/faction "tower" territories stay alliance-owned, not
    /// nation-ownable - see NationFoundableZoneGroups) - so in practice a nation here tops out at 2 controlled
    /// dominions (150 population), not 4. The formula itself is implemented generically/correctly; the 200 cap
    /// simply isn't reachable under this session's territory-eligibility rules. Flagged, not silently hidden.
    /// </summary>
    public int GetPopulationCap(uint nationFactionId)
    {
        var dominionCount = dominionManager.Dominions.Count(d => d.OwningFactionId == nationFactionId);
        return Math.Min(200, 100 + 50 * Math.Max(0, dominionCount - 1));
    }

    public int GetMemberCount(uint nationFactionId) =>
        WorldManager.Instance.GetAllCharacters().Count(c => c.Faction?.Id == (FactionsEnum)nationFactionId);

    /// <summary>
    /// A nation member leaves back to their original home faction. If they're still a member of the nation's
    /// founding (nation-bound) guild, they must leave that guild first - guild-level nation membership is sticky
    /// by design (the user's spec: "any guild once in the nation cannot be transferred back"), so an individual
    /// can't personally exit the nation while still inside a guild that's permanently part of it.
    /// </summary>
    public bool LeaveNation(Character character)
    {
        if (character?.OriginFaction == null && character?.IsTempFaction != true)
            return false;

        if (character.Expedition != null)
        {
            var guildNation = _nations.Values.FirstOrDefault(n => n.FactionId == (uint)character.Expedition.MotherId);
            if (guildNation != null)
            {
                character.SendErrorMessage(ErrorMessageType.NoPerm);
                return false;
            }
        }

        LeaveNationFaction(character.Id);
        return true;
    }

    /// <summary>Hands Sovereignty to another current member of the same nation. Only the current Sovereign may call this.</summary>
    public bool TransferSovereign(Character currentSovereign, Character newSovereign)
    {
        var myZoneId = GetNationOfSovereign(currentSovereign.Id);
        if (myZoneId == null || !_nations.TryGetValue(myZoneId.Value, out var nation))
        {
            currentSovereign.SendErrorMessage(ErrorMessageType.NoPerm);
            return false;
        }

        if (newSovereign?.Faction == null || newSovereign.Faction.Id != (FactionsEnum)nation.FactionId)
        {
            currentSovereign.SendErrorMessage(ErrorMessageType.Invalid);
            return false;
        }

        nation.SovereignCharacterId = newSovereign.Id;

        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE nations SET sovereign_character_id=@s WHERE zone_id=@z";
        command.Parameters.AddWithValue("@s", newSovereign.Id);
        command.Parameters.AddWithValue("@z", nation.ZoneId);
        command.Prepare();
        command.ExecuteNonQuery();

        Logger.Info("Nation zone {0} Sovereign transferred: {1} -> {2}", nation.ZoneId, currentSovereign.Name, newSovereign.Name);
        return true;
    }

    /// <summary>
    /// Forcibly disbands one guild - same effective steps as ExpeditionManager.Disband(Character) but without
    /// needing a live, currently-online guild-owner Character to call it through (this fires from
    /// NationManager.Disband, which may run with nobody from the guild online at all).
    /// </summary>
    private static void DisbandGuild(Expedition guild)
    {
        foreach (var member in guild.Members.ToArray())
        {
            var c = WorldManager.Instance.GetCharacterById(member.CharacterId);
            if (c != null)
            {
                if (c.IsOnline)
                    c.SendPacket(new SCExpeditionDismissedPacket((uint)guild.Id, true));
                c.Expedition = null;
                using var connection = MySQL.CreateConnection();
                using var transaction = connection.BeginTransaction();
                c.Save(connection, transaction);
                transaction.Commit();
            }
            else
            {
                using var connection = MySQL.CreateConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE characters SET expedition_id = 0 WHERE id = @id";
                command.Parameters.AddWithValue("@id", member.CharacterId);
                command.Prepare();
                command.ExecuteNonQuery();
            }

            guild.RemoveMember(member);
        }

        guild.Name = "$deleted-guild-" + (uint)guild.Id;
        guild.OwnerId = 0;
        guild.isDisbanded = true;
        ExpeditionManager.Save(guild);
    }

    /// <summary>
    /// Disbands a nation. Voluntary (forced=false): territory reverts to the founding guild (only if it still
    /// holds the dominion under this nation's faction - it always does in this pass, since siege-loss-to-another-
    /// owner isn't built yet). Forced (forced=true, for the future 30-day-grace-period timer): territory is left
    /// untouched (someone else already owns it by then) and every member guild - not just the founder - is
    /// disbanded outright, per the user's spec. Either way, every member character reverts to their home faction.
    /// </summary>
    public bool Disband(ushort zoneId, bool forced)
    {
        if (!_nations.TryGetValue(zoneId, out var nation))
            return false;

        var memberGuilds = ExpeditionManager.Instance.Expeditions
            .Where(e => (uint)e.MotherId == nation.FactionId)
            .ToList();

        foreach (var guild in memberGuilds)
        {
            foreach (var member in guild.Members.ToArray())
                LeaveNationFaction(member.CharacterId);

            if (forced)
                DisbandGuild(guild);
            else
            {
                guild.MotherId = FactionsEnum.Invalid;
                ExpeditionManager.Save(guild);
            }
        }

        if (!forced && nation.FoundingExpeditionId != 0)
            dominionManager.TransferToGuild(zoneId, nation.FoundingExpeditionId);

        _nations.Remove(zoneId);
        using (var connection = MySQL.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM nations WHERE zone_id=@z";
            command.Parameters.AddWithValue("@z", zoneId);
            command.Prepare();
            command.ExecuteNonQuery();
        }

        Logger.Info("Nation zone {0} disbanded ({1})", zoneId, forced ? "forced" : "voluntary");
        return true;
    }

    /// <summary>The nation zone a character is Sovereign of, or null if they aren't one.</summary>
    public ushort? GetNationOfSovereign(uint characterId)
    {
        foreach (var nation in _nations.Values)
        {
            if (nation.SovereignCharacterId == characterId)
                return nation.ZoneId;
        }

        return null;
    }

    /// <summary>
    /// Places this Sovereign's National Monument at (x,y,z), which must be inside their own claimed Dominion.
    /// Validation order mirrors the real client's own error-code sequence - ErrorMessageType 748-751
    /// (OnlyNationOwner/LocateInvalidDominionArea/NoMoreNationalMonument/CantPutUpNationalMonumentDuringSiegePeriod),
    /// confirmed real and present in this codebase but completely unreferenced anywhere before this - see the
    /// aaemu-siege-castle-hero-nation memory's 2026-08-19 National Monument research entry for the RE trail
    /// (a Ghidra string-xref search found zero code references to these error strings in x2game-dev.dll, meaning
    /// they're generic server-returned codes displayed by the client's existing generic error-message lookup,
    /// not a client-hardcoded check - and no client UI/skill trigger was found either, hence the GM command
    /// below instead of a guessed opcode/skill, same reasoning as RequestRelation/RespondRelation).
    /// </summary>
    public void PlaceNationalMonument(Character character, float x, float y, float z)
    {
        var myZoneId = GetNationOfSovereign(character.Id);
        if (myZoneId == null)
        {
            character.SendErrorMessage(ErrorMessageType.OnlyNationOwner);
            return;
        }

        var dominion = dominionManager.GetDominionAtPosition(myZoneId.Value, x, y);
        if (dominion == null)
        {
            character.SendErrorMessage(ErrorMessageType.LocateInvalidDominionArea);
            return;
        }

        if (dominion.NationalMonumentDbId != 0)
        {
            character.SendErrorMessage(ErrorMessageType.NoMoreNationalMonument);
            return;
        }

        if (siegeManager.GetScheduledPeriod(dominion.ZoneId, DateTime.UtcNow) == SiegePeriod.Siege)
        {
            character.SendErrorMessage(ErrorMessageType.CantPutUpNationalMonumentDuringSiegePeriod);
            return;
        }

        var doodadTemplateId = SiegeGameData.Instance.GetSiegeZoneSchedule(dominion.ZoneId)?.MonumentDoodadId ?? 0;
        if (doodadTemplateId == 0)
        {
            Logger.Warn("PlaceNationalMonument: zone group {0} has no monument_doodad_id in siege_zones - nothing to spawn", dominion.ZoneId);
            character.SendErrorMessage(ErrorMessageType.Invalid);
            return;
        }

        var doodad = DoodadManager.Instance.Create(character.ParentWorld, 0, doodadTemplateId, character, true);
        if (doodad == null)
        {
            Logger.Warn("PlaceNationalMonument: doodad template {0} not found, zone group {1}", doodadTemplateId, dominion.ZoneId);
            character.SendErrorMessage(ErrorMessageType.Invalid);
            return;
        }

        doodad.SetPosition(x, y, z, 0, 0, 0);
        doodad.InitDoodad();
        doodad.Spawn();

        dominionManager.SetNationalMonument(dominion.ZoneId, doodad.ObjId, x, y, z);

        // 2026-08-19: SCNationalMonumentChangedPacket's "id"/"type" args have never been Ghidra-confirmed (the
        // packet is never constructed anywhere else in this codebase to compare against). id here is the owning
        // zone group, type=1 as a placeholder "built" state - client-facing, so a wrong guess is a cosmetic bug
        // at worst (not the crash-risk class of the WZ*/ZW* Zone-facing packets) - revisit if the monument
        // doesn't render/update correctly on a live test.
        WorldManager.Instance.BroadcastPacketToServer(new SCNationalMonumentChangedPacket(dominion.ZoneId, 1, x, y, z));

        Logger.Info("National Monument placed: zone group {0}, doodad template {1} (objId {2}), Sovereign {3}",
            dominion.ZoneId, doodadTemplateId, doodad.ObjId, character.Name);
    }

    /// <summary>
    /// The requesting Sovereign's nation proposes a friend or hostile relation to another nation, by mail
    /// (__MAIL_NATION_RELATION_FRIEND/_HOSTILE - real client mail templates, no client packet exists to trigger
    /// this in r575 at all, so this is exposed via chat command instead of a guessed opcode).
    /// </summary>
    public void RequestRelation(Character requester, ushort targetZoneId, bool friend)
    {
        var myZoneId = GetNationOfSovereign(requester.Id);
        if (myZoneId == null)
        {
            requester.SendErrorMessage(ErrorMessageType.NoPerm);
            return;
        }

        if (myZoneId == targetZoneId || !_nations.TryGetValue(targetZoneId, out var target))
        {
            requester.SendErrorMessage(ErrorMessageType.Invalid);
            return;
        }

        var (a, b) = CanonicalPair(myZoneId.Value, targetZoneId);

        using (var connection = MySQL.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                REPLACE INTO nation_relations (zone_id_a, zone_id_b, status, requested_by_zone_id, requested_friend, updated_at)
                VALUES (@a, @b, 'pending', @requestedBy, @friend, @now)
                """;
            command.Parameters.AddWithValue("@a", a);
            command.Parameters.AddWithValue("@b", b);
            command.Parameters.AddWithValue("@requestedBy", myZoneId.Value);
            command.Parameters.AddWithValue("@friend", friend);
            command.Parameters.AddWithValue("@now", DateTime.UtcNow);
            command.Prepare();
            command.ExecuteNonQuery();
        }

        var targetName = NameManager.Instance.GetCharacterName(target.SovereignCharacterId);
        if (targetName == null)
            return;

        var mail = new BaseMail
        {
            MailType = friend ? MailType.NationRelationFriend : MailType.NationRelationHostile,
            Title = friend ? "Nation Friendship Proposal" : "Nation Hostility Declaration",
            ReceiverName = targetName
        };
        mail.Header.SenderName = ".nation";
        mail.Header.ReceiverId = target.SovereignCharacterId;
        mail.Header.Status = MailStatus.Unread;
        mail.Body.Text = $"Zone {myZoneId} proposes a {(friend ? "friendly" : "hostile")} relation. Use /nationrelationrespond {myZoneId} accept|reject to answer.";
        mail.Body.RecvDate = DateTime.UtcNow;
        mail.Send();
    }

    /// <summary>The target Sovereign accepts or rejects a pending relation request, mailing the original requester the result.</summary>
    public void RespondRelation(Character responder, ushort requesterZoneId, bool accept)
    {
        var myZoneId = GetNationOfSovereign(responder.Id);
        if (myZoneId == null)
        {
            responder.SendErrorMessage(ErrorMessageType.NoPerm);
            return;
        }

        var (a, b) = CanonicalPair(myZoneId.Value, requesterZoneId);

        bool requestedFriend;
        using (var connection = MySQL.CreateConnection())
        {
            using (var check = connection.CreateCommand())
            {
                check.CommandText = "SELECT requested_friend FROM nation_relations WHERE zone_id_a=@a AND zone_id_b=@b AND status='pending' AND requested_by_zone_id=@requester";
                check.Parameters.AddWithValue("@a", a);
                check.Parameters.AddWithValue("@b", b);
                check.Parameters.AddWithValue("@requester", requesterZoneId);
                check.Prepare();
                var result = check.ExecuteScalar();
                if (result == null)
                {
                    responder.SendErrorMessage(ErrorMessageType.Invalid);
                    return;
                }

                requestedFriend = Convert.ToBoolean(result);
            }

            using (var update = connection.CreateCommand())
            {
                if (accept)
                {
                    update.CommandText = "UPDATE nation_relations SET status=@status, updated_at=@now WHERE zone_id_a=@a AND zone_id_b=@b";
                    update.Parameters.AddWithValue("@status", requestedFriend ? "friend" : "hostile");
                }
                else
                {
                    update.CommandText = "DELETE FROM nation_relations WHERE zone_id_a=@a AND zone_id_b=@b";
                }

                update.Parameters.AddWithValue("@a", a);
                update.Parameters.AddWithValue("@b", b);
                update.Parameters.AddWithValue("@now", DateTime.UtcNow);
                update.Prepare();
                update.ExecuteNonQuery();
            }
        }

        if (!_nations.TryGetValue(requesterZoneId, out var requesterNation))
            return;

        var requesterName = NameManager.Instance.GetCharacterName(requesterNation.SovereignCharacterId);
        if (requesterName == null)
            return;

        var mail = new BaseMail
        {
            MailType = accept
                ? (requestedFriend ? MailType.NationRelationFriendSuccess : MailType.NationRelationHostileSuccess)
                : (requestedFriend ? MailType.NationRelationFriendReject : MailType.NationRelationHostileReject),
            Title = accept ? "Relation Accepted" : "Relation Rejected",
            ReceiverName = requesterName
        };
        mail.Header.SenderName = ".nation";
        mail.Header.ReceiverId = requesterNation.SovereignCharacterId;
        mail.Header.Status = MailStatus.Unread;
        mail.Body.Text = $"Zone {myZoneId} {(accept ? "accepted" : "rejected")} your {(requestedFriend ? "friendship" : "hostility")} proposal.";
        mail.Body.RecvDate = DateTime.UtcNow;
        mail.Send();
    }

    public string GetRelationStatus(ushort zoneIdA, ushort zoneIdB)
    {
        var (a, b) = CanonicalPair(zoneIdA, zoneIdB);
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM nation_relations WHERE zone_id_a=@a AND zone_id_b=@b";
        command.Parameters.AddWithValue("@a", a);
        command.Parameters.AddWithValue("@b", b);
        command.Prepare();
        return command.ExecuteScalar() as string ?? "neutral";
    }

    private static (ushort, ushort) CanonicalPair(ushort x, ushort y) => x < y ? (x, y) : (y, x);
}

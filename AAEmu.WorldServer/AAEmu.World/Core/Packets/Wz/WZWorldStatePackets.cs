using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Models.Game;

namespace AAEmu.World.Core.Packets.Wz;

// World → Zone world state, gimmicks, sieges and schedules (opcodes 0x050-0x06F).
// Each body is the dedicate DLL's own serializer for the type, reached through slot 2

public class WZAttackOnQuestPacket(uint unitId, uint unitId2)
    : ZonePacket(WzOpcodes.AttackOnQuest)
{
    protected override void WriteBody(PacketStream stream)
    {
        stream.WriteBc(unitId);
        stream.WriteBc(unitId2);
    }
}

public class WZFollowUnitOnQuestPacket(uint unitId, uint unitId2)
    : ZonePacket(WzOpcodes.FollowUnitOnQuest)
{
    protected override void WriteBody(PacketStream stream)
    {
        stream.WriteBc(unitId);
        stream.WriteBc(unitId2);
    }
}

public class WZFollowPathOnQuestPacket(uint unitId, uint unitId2, string pathName, byte pathType)
    : ZonePacket(WzOpcodes.FollowPathOnQuest)
{
    protected override void WriteBody(PacketStream stream)
    {
        stream.WriteBc(unitId);
        stream.WriteBc(unitId2);
        stream.Write(pathName ?? string.Empty);
        stream.Write(pathType);
    }
}

public class WZRunCommandSetOnQuestPacket(uint unitId, uint unitId2, int typeValue)
    : ZonePacket(WzOpcodes.RunCommandSetOnQuest)
{
    protected override void WriteBody(PacketStream stream)
    {
        stream.WriteBc(unitId);
        stream.WriteBc(unitId2);
        stream.Write(typeValue);
    }
}

public class WZSandboxOnlineHeightmapAction(string account, long actionNo, byte typeValue, uint posX, uint posY, float radius, float radiusInside, float height, float maxHeight, float hardness, bool noise, float noiseScale, float noiseFreq, bool repositionObjects)
    : ZonePacket(WzOpcodes.SandboxOnlineHeightmapAction)
{
    protected override void WriteBody(PacketStream stream)
    {
        stream.Write(account ?? string.Empty);
        stream.Write(actionNo);
        stream.Write(typeValue);
        stream.Write(posX);
        stream.Write(posY);
        stream.Write(radius);
        stream.Write(radiusInside);
        stream.Write(height);
        stream.Write(maxHeight);
        stream.Write(hardness);
        stream.Write(noise);
        stream.Write(noiseScale);
        stream.Write(noiseFreq);
        stream.Write(repositionObjects);
    }
}

public class WZSandboxOnlineUndo(string account, long actionNo)
    : ZonePacket(WzOpcodes.SandboxOnlineUndo)
{
    protected override void WriteBody(PacketStream stream)
    {
        stream.Write(account ?? string.Empty);
        stream.Write(actionNo);
    }
}

public class WZSandboxOnlinePlayerPos(string account, float posx, float posy, float posz, float rotx, float roty, float rotz, float rotw, float brushPosx, float brushPosy, float brushPosz, float brushRadius)
    : ZonePacket(WzOpcodes.SandboxOnlinePlayerPos)
{
    protected override void WriteBody(PacketStream stream)
    {
        stream.Write(account ?? string.Empty);
        stream.Write(posx);
        stream.Write(posy);
        stream.Write(posz);
        stream.Write(rotx);
        stream.Write(roty);
        stream.Write(rotz);
        stream.Write(rotw);
        stream.Write(brushPosx);
        stream.Write(brushPosy);
        stream.Write(brushPosz);
        stream.Write(brushRadius);
    }
}

public class WZGimmickReloadStaticsPacket()
    : ZonePacket(WzOpcodes.GimmickReloadStatics)
{
    protected override void WriteBody(PacketStream stream) { }
}

public class WZGimmickMovementPacket(int id, int time, ulong x, ulong y, float z, float rotx, float roty, float rotz, float rotw, float velx, float vely, float velz, float angVelx, float angVely, float angVelz, float scale)
    : ZonePacket(WzOpcodes.GimmickMovement)
{
    protected override void WriteBody(PacketStream stream)
    {
        stream.Write(id);
        stream.Write(time);
        stream.Write(x);
        stream.Write(y);
        stream.Write(z);
        stream.Write(rotx);
        stream.Write(roty);
        stream.Write(rotz);
        stream.Write(rotw);
        stream.Write(velx);
        stream.Write(vely);
        stream.Write(velz);
        stream.Write(angVelx);
        stream.Write(angVely);
        stream.Write(angVelz);
        stream.Write(scale);
    }
}

public class WZGimmickGraspedPacket(int id, int grasperUnitId, bool grasped)
    : ZonePacket(WzOpcodes.GimmickGrasped)
{
    protected override void WriteBody(PacketStream stream)
    {
        stream.Write(id);
        stream.Write(grasperUnitId);
        stream.Write(grasped);
    }
}

/// <summary>
/// World → Zone: a zone group has just been claimed (or its claim data needs a resync, e.g. World boot with an
/// existing claim). Real opcode (0x0060, from dev-DLL string mining). Zone had zero awareness that any
/// territory was ever claimed before this, which is the likely root cause of the world-map territory circle
/// never rendering and possibly the Territory Agent NPC's interaction gate (Zone is sim/interaction authority -
/// see aaemu-server-overview memory).
///
/// **2026-08-20: full field layout ground-truth-confirmed**, not a best-effort guess - via direct decompile of
/// the real Zone-side deserializer, `DominionData::Read()` (`FUN_39cc4310` @ `0x39cc4310` in
/// `x2game-dev_dedicate.dll`) and its 5 sub-functions (`FUN_39b113a0` Point, `FUN_39bdc750` TerritoryData,
/// `FUN_39cc4250` roster container, `FUN_39cc2dc0` a single embedded roster record, `FUN_39cc2c50`/
/// `FUN_39cc2cf0` its sub-arrays, `FUN_39c4e9a0` the real "offense" raid-team vector) - read as raw decompiled C
/// directly from `D:\aa\clientstuff\x2game-dev_dedicate_full_decompile.txt`, not summarized from an
/// intermediate research pass (an earlier summary had conflated the single embedded record with the vector's
/// element type - re-reading the actual function bodies resolved that). Full field table + the crash-incident
/// history this supersedes: see the aaemu-siege-castle-hero-nation and aaemu-zone-wire-format-danger memory
/// files. **This exact opcode already crashed the live Zone process once (2026-08-19) from an unverified guess
/// - do not modify this class without re-reading those memory files and the source functions above first.**
///
/// Two top-level scalar fields (payload offset 0x00, a 2-byte value, and offset 0xf48, "point", 4 bytes) and
/// one id-shaped field (offset 0x64, "bc") have confirmed WIDTH/ENCODING but unconfirmed SEMANTIC MEANING - sent
/// as zero, which is safe (a wrong width/count is what causes an out-of-bounds read crash; a wrong *value* in a
/// fixed-size scalar cannot). The "offense" raid-team roster (a real, count-prefixed std::vector of further
/// records) and the one single embedded roster record before it are both sent empty/zeroed - correct and safe
/// for a claim/resync with no active siege-raid-team state, which is the only case this is currently called for
/// (DominionManager has no raid-team-roster data to populate here even if it wanted to - SiegeManager owns that
/// separately, in siege_raid_team_members, not synced into DominionData).
///
/// **`RequiredPaddingBytes` (36) - EMPIRICAL, not yet structurally explained.** Live bisection on 2026-08-20 via
/// `/dominionresynczero` (Zone's own log gracefully reports "not enough buffer for &lt;field&gt;" or "serializer
/// size mismatch" for this opcode - no memory-corruption crash, no .dmp, safe to iterate on) found the modeled
/// field table above is real and correctly *ordered*, but 36 bytes short of Zone's true required total - 0
/// padding fails inside the roster record's Point, 34 fails on the very last field (`nonPvPDuration`), 36
/// succeeds cleanly, 38+ overshoots into "size mismatch". One credible unconfirmed lead for what those 36 bytes
/// really are: `FUN_39c4e9a0` (the "offense" vector's real serializer) opens with an unaccounted-for
/// `slot 0x30("offense", 1)` call gating the rest of the function - possibly a wire-carried block-length prefix
/// or presence flag for a tagged/optional sub-block. Until that's confirmed, this constant is a working
/// stand-in, not a real fix - see the aaemu-siege-castle-hero-nation memory's 2026-08-20 bisection entry for
/// the full data and DO NOT change this number without re-running that same live bisection technique.
/// </summary>
public class WZDominionDataPacket(DominionData dominion, int diagnosticPaddingBytes = WZDominionDataPacket.RequiredPaddingBytes)
    : ZonePacket(WzOpcodes.DominionData)
{
    /// <summary>Empirically-determined via live bisection, 2026-08-20 - see this class's own doc comment. Not yet structurally explained.</summary>
    public const int RequiredPaddingBytes = 36;

    protected override void WriteBody(PacketStream stream)
    {
        // +0x00 (2B) - unnamed, semantic unconfirmed, native slot 0x88
        stream.Write((ushort)0);
        // +0x04 (4B) "FactionId" - confirmed 2026-08-20 via GetOwnerFaction's native read path (see
        // DominionData.FactionId's doc comment, AAEmu.Game project) - top-level FactionsEnum (148 Nuia /
        // 149 Harihara), not a guild id. Was hardcoded 0 here; parity fix with the client-facing packet - zero
        // wire-format risk (same scalar slot, value-only change).
        stream.Write((uint)dominion.FactionId);
        // +0x08 "house"
        stream.Write(dominion.House);
        // +0x0c "taxRate"
        stream.Write(dominion.TaxRate);
        // +0x68 Point (dominion's own claimed center - written HERE in wire order, well before its target
        // offset, matching the native call sequence exactly: house, taxRate, THEN this Point call, THEN the
        // moneyAmount loop below)
        stream.Write(Helpers.ConvertLongX(dominion.X));
        stream.Write(Helpers.ConvertLongY(dominion.Y));
        stream.Write(dominion.Z);
        // 5x "moneyAmount" (8B each): CurHouseTaxMoney, CurHuntTaxMoney, PeaceTaxMoney, CurHouseTaxAaPoint,
        // PeaceTaxAaPoint - all 0 while the tax system stays disabled, but wired for real regardless
        stream.Write((long)dominion.CurHouseTaxMoney);
        stream.Write((long)dominion.CurHuntTaxMoney);
        stream.Write((long)dominion.PeaceTaxMoney);
        stream.Write((long)dominion.CurHouseTaxAaPoint);
        stream.Write((long)dominion.PeaceTaxAaPoint);
        // 3x 8-byte unix-seconds timestamps (native slot 0x78, width confirmed via direct dereference evidence
        // inside FUN_39cc4310 itself; unix-seconds specifically matches this codebase's own established WZ-
        // direction timestamp convention, see WZCreateDoodadPacket.cs)
        stream.Write((ulong)Helpers.UnixTime(dominion.LastPaidTime));
        stream.Write((ulong)Helpers.UnixTime(dominion.LastSiegeEndTime));
        stream.Write((ulong)Helpers.UnixTime(dominion.ReignStartTime));
        // +0xf70 "lastTaxRateChangedTime" - written here in wire order (same 8B unix-seconds encoding)
        stream.Write((ulong)Helpers.UnixTime(dominion.LastTaxRateChangedTime));
        // +0xf48 "point" (4B) - semantic unconfirmed, sent as 0
        stream.Write(0);
        // +0x64 "bc" - an id-shaped field using the same WriteBc encoding as every other id field in this
        // struct (confirmed 2026-08-20: the branch this depends on is constant-true/long-form throughout
        // Read(), so the existing WriteBc is correct as-is) - semantic unconfirmed, sent as 0
        stream.WriteBc(0);
        // +0x84 TerritoryData - reuse the existing, already-confirmed-byte-correct model (matches
        // FUN_39bdc750 field-for-field: Id/Id2/MaxGates/MaxWalls/RadiusDeclare/RadiusDominion/
        // RadiusOffenseHq/RadiusSiege)
        dominion.TerritoryData?.Write(stream);
        // +0x98 roster container (FUN_39cc4250)
        stream.Write(dominion.SiegeTimers?.SiegePeriod ?? (byte)0);
        WriteEmptyRosterRecord(stream); // the single embedded record at the container's +0x38
        stream.Write(0u);               // "offense" vector count = 0 (no active raid-team roster tracked here)
        stream.Write(false);            // siegeSecondHalf
        // +0x50 "nonPvPStart" (8B unix-seconds)
        stream.Write((ulong)Helpers.UnixTime(dominion.NonPvPStart));
        // "nonPvPDuration" (2B, native slot 0x88 - confirmed via the same slot's other use, "nonPvPDuration",
        // being stored into a declared ushort locally) - the real, already-modeled C# field
        stream.Write(dominion.NonPvPDuration);

        // TEMPORARY DIAGNOSTIC PADDING, 2026-08-20 - live tests confirmed Zone's deserializer fails with a
        // graceful, bounds-checked "not enough buffer for <field>" error (not a memory-corruption crash - no
        // .dmp is produced, see aaemu-zone-wire-format-danger memory) partway through the embedded roster
        // record's Point read at 0 padding, and a "serializer size mismatch" (parse actually completed through
        // every field including the last one, nonPvPDuration, just with leftover bytes) at 500 padding - so the
        // true missing length is somewhere in (0, 500). Trailing zero bytes are safe to append here (every
        // remaining unread field in this structure is either a fixed scalar or a count-prefixed array - a
        // stray zero byte read as a "count" just means "no entries", not a runaway read). Exposed as a
        // constructor parameter (see DominionZoneResyncZeroTest's optional second arg) so the exact required
        // length can be bisected live in-game without a rebuild per attempt. REMOVE once confirmed and folded
        // into a real structural fix.
        for (var i = 0; i < diagnosticPaddingBytes; i++)
            stream.Write((byte)0);
    }

    /// <summary>
    /// One roster-record slot (FUN_39cc2dc0's shape, confirmed via direct decompile: an unnamed leading 4-byte
    /// field, id, Point, a Limit/count-prefixed array of up to ~50 8-byte ids, a count-prefixed array of up to
    /// ~100 32-byte sub-records, then teamId/scorePoint) written fully empty. Correct for "no data to report"
    /// (every array here is genuinely count-prefixed - a zero count is unambiguous and safe, not a guess), used
    /// both for the single embedded record and would be reused per-element if the "offense" vector ever needs
    /// real entries.
    ///
    /// **2026-08-20 bug found+fixed here, post-mortem after a second live crash**: this method originally
    /// omitted the leading unnamed 4-byte field (native: `slot 0x80 @ record+0x00`, a plain 4-byte value,
    /// SEPARATE from the id field that follows it at record+0x04 via WriteBc) - a self-review mistake made
    /// while translating the raw decompile to C#, not a gap in the underlying research (the raw function body
    /// was already fully read correctly beforehand; the byte was just dropped in transcription). This
    /// under-sized every occurrence of this record by 4 bytes, most critically desyncing the "offense" vector's
    /// count field read right after it - the same crash mechanism (a garbage count driving a runaway read) as
    /// the original 2026-08-19 incident. Confirmed live: a resync attempt with only this bug present crashed
    /// Zone (zone 205/Nuimari) in the same second, same signature, as the first crash. **Lesson: even a fully
    /// ground-truth-confirmed field table can still be mis-transcribed - re-read the actual decompiled function
    /// body one more time immediately before/while writing the C#, don't work from an interim summary of it,
    /// no matter how recently that summary was made.**
    /// </summary>
    private static void WriteEmptyRosterRecord(PacketStream stream)
    {
        stream.Write(0u);      // unnamed leading 4-byte field (native slot 0x80, tag DAT_3a1eb540 - the bug: this was missing entirely)
        stream.WriteBc(0);     // id
        stream.Write(0L);      // Point.x
        stream.Write(0L);      // Point.y
        stream.Write(0f);      // Point.z
        stream.Write((byte)0); // Limit-array: limit
        stream.Write((byte)0); // Limit-array: count (0 entries)
        stream.Write((byte)0); // 32-byte-sub-record array: count (0 entries)
        stream.Write(0u);      // teamId
        stream.Write(0);       // scorePoint
    }
}

/// <summary>World → Zone: a zone group's claim was removed. See WZDominionDataPacket's doc comment - same 2026-08-19 gap-fill, not wired to an unclaim flow yet since this codebase has none.</summary>
public class WZDominionDeletedPacket(uint zoneGroupId)
    : ZonePacket(WzOpcodes.DominionDeleted)
{
    protected override void WriteBody(PacketStream stream)
    {
        stream.Write(zoneGroupId);
    }
}

public class WZSiegeMemberPacket(int typeValue, ulong typeValue2, bool added)
    : ZonePacket(WzOpcodes.SiegeMember)
{
    protected override void WriteBody(PacketStream stream)
    {
        stream.Write(typeValue);
        stream.Write(typeValue2);
        stream.Write(added);
    }
}

public class WZSiegeSecondHalfPacket(bool secondHalf)
    : ZonePacket(WzOpcodes.SiegeSecondHalf)
{
    protected override void WriteBody(PacketStream stream)
    {
        stream.Write(secondHalf);
    }
}

public class WZTowerDefReloadPacket()
    : ZonePacket(WzOpcodes.TowerDefReload)
{
    protected override void WriteBody(PacketStream stream) { }
}

public class WZTowerDefQueryPlayabilityPacket(int typeValue, short typeValue2)
    : ZonePacket(WzOpcodes.TowerDefQueryPlayability)
{
    protected override void WriteBody(PacketStream stream)
    {
        stream.Write(typeValue);
        stream.Write(typeValue2);
    }
}

public class WZTowerDefStartPacket(int typeValue, short typeValue2, uint spotIdx)
    : ZonePacket(WzOpcodes.TowerDefStart)
{
    protected override void WriteBody(PacketStream stream)
    {
        stream.Write(typeValue);
        stream.Write(typeValue2);
        stream.Write(spotIdx);
    }
}

public class WZTowerDefEndPacket(int typeValue, short typeValue2, uint spotIdx)
    : ZonePacket(WzOpcodes.TowerDefEnd)
{
    protected override void WriteBody(PacketStream stream)
    {
        stream.Write(typeValue);
        stream.Write(typeValue2);
        stream.Write(spotIdx);
    }
}

public class WZTowerDefWaveStartPacket(int typeValue, short typeValue2, uint spotIdx, uint step)
    : ZonePacket(WzOpcodes.TowerDefWaveStart)
{
    protected override void WriteBody(PacketStream stream)
    {
        stream.Write(typeValue);
        stream.Write(typeValue2);
        stream.Write(spotIdx);
        stream.Write(step);
    }
}

// VERIFIED 10.0.2.13: the three GameSchedule bodies each make exactly one serializer call, on
// same table gives 0xF0=f32 (WZTimeOfDay) and 0x30+0x38=Bc (WZUnitRemoved). The single field is
// the game_schedules row id. Layout confirmed field-by-field; no need to re-derive.
//
// These three are what release schedule-linked spawners: the dedicate withholds every placement
// named in game_schedule_spawners until World declares the period open, and never reads
// game_schedules itself. Sent by GameScheduleRelay.

/// <summary>GameScheduleStart (0x06A) — World → Zone. Body: u32 gameScheduleId.</summary>
public class WZGameScheduleStartPacket(int typeValue)
    : ZonePacket(WzOpcodes.GameScheduleStart)
{
    protected override void WriteBody(PacketStream stream)
    {
        stream.Write(typeValue);
    }
}

/// <summary>GameScheduleContinue (0x06B) — World → Zone. Body: u32 gameScheduleId.</summary>
public class WZGameScheduleContinuePacket(int typeValue)
    : ZonePacket(WzOpcodes.GameScheduleContinue)
{
    protected override void WriteBody(PacketStream stream)
    {
        stream.Write(typeValue);
    }
}

/// <summary>GameScheduleEnd (0x06C) — World → Zone. Body: u32 gameScheduleId.</summary>
public class WZGameScheduleEndPacket(int typeValue)
    : ZonePacket(WzOpcodes.GameScheduleEnd)
{
    protected override void WriteBody(PacketStream stream)
    {
        stream.Write(typeValue);
    }
}

public class WZGameActivityStartPacket(uint activityId, uint serverId)
    : ZonePacket(WzOpcodes.GameActivityStart)
{
    protected override void WriteBody(PacketStream stream)
    {
        stream.Write(activityId);
        stream.Write(serverId);
    }
}

public class WZGameActivityEndPacket(uint activityId, uint serverId)
    : ZonePacket(WzOpcodes.GameActivityEnd)
{
    protected override void WriteBody(PacketStream stream)
    {
        stream.Write(activityId);
        stream.Write(serverId);
    }
}

public class WZNpcControlPacket(uint unitId, uint unitId2, int typeValue)
    : ZonePacket(WzOpcodes.NpcControl)
{
    protected override void WriteBody(PacketStream stream)
    {
        stream.WriteBc(unitId);
        stream.WriteBc(unitId2);
        stream.Write(typeValue);
    }
}

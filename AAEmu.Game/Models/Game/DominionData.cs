using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Models.Game;

public class DominionData : PacketMarshaler
{
    public ushort ZoneId { get; set; }
    public uint ExpeditionId { get; set; }

    /// <summary>
    /// Real, persisted ownership for the 4 Hero/faction-only territories (siege_zones-having zone groups
    /// 33/34/43/44) - the raw FactionsEnum id (148 Nuia / 149 Haranya), set directly at claim time from the
    /// declaring Hero's own alliance. 0/unused for guild-owned zone groups (54/56), where ExpeditionId is the
    /// real ownership field instead and FactionId below is derived from it as before. Manager-side bookkeeping
    /// only, not part of the wire Write() below - same treatment as GuardTowerStep/TerritoryAgent.
    /// </summary>
    public uint OwningFactionId { get; set; }

    /// <summary>
    /// 2026-08-20 fix: the wire slot at struct offset+4 (right after ZoneId) is NOT read natively as a guild id.
    /// Deep RE (native `X2Dominion:GetOwnerFaction` = FUN_3997da10, resolver FUN_396ad7b0 -> FUN_39cfc050) proved
    /// it reads `*(uint*)(dominionObj+4)` and treats it as a top-level FactionsEnum value (Nuia=148/Harihara=149
    /// per compact.sqlite3's const_system_faction_types, matching FactionsEnum.NuiaAlliance/HaranyaAlliance) - fed
    /// straight into a fixed compiled-in enum-&gt;string table, not a dynamic guild-name lookup. The reference
    /// client's own tab_dominion.lua (Community-&gt;Nation dominion management panel) gates entirely on
    /// `X2Dominion:GetOwnerFaction(zoneGroup) == X2Faction:GetMyTopLevelFaction()` plus `X2Hero:IsHero()` - since
    /// we were writing the raw guild database id here (a large arbitrary autoincrement number, never 148/149),
    /// that panel could never open for anyone, matching the user's live-confirmed experience. ExpeditionId (the
    /// real guild id) is kept as its own field - genuinely used elsewhere server-side (build permission checks in
    /// HousingManager/AdvanceGuardTowerStep, plot ownership in DominionManager) - but is no longer what gets
    /// written into THIS wire slot; FactionId is, resolved from the claiming guild owner's race at claim time
    /// (see DominionManager.Declare/Load).
    /// </summary>
    public FactionsEnum FactionId { get; set; }
    public uint House { get; set; } // TODO id?
    public int TaxRate { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public int CurHouseTaxMoney { get; set; }
    public int CurHuntTaxMoney { get; set; }
    public int PeaceTaxMoney { get; set; }
    public int CurHouseTaxAaPoint { get; set; }
    public int PeaceTaxAaPoint { get; set; }
    public DateTime LastPaidTime { get; set; }
    public DateTime LastSiegeEndTime { get; set; }
    public DateTime ReignStartTime { get; set; }
    public DateTime LastTaxRateChangedTime { get; set; } // TODO in struct long
    public uint ObjId { get; set; }
    public DominionTerritoryData TerritoryData { get; set; }
    public DominionSiegeTimers SiegeTimers { get; set; } // TODO mb not correct namings
    public DateTime NonPvPStart { get; set; }
    public ushort NonPvPDuration { get; set; }

    /// <summary>
    /// Live-bisected against the Zone-facing sibling packet (WZDominionData), 2026-08-20 - see
    /// WZDominionDataPacket's own doc comment in AAEmu.WorldServer for the full story. Not yet independently
    /// re-verified for the client-facing (SC) direction, but the underlying native deserializer is BYTE-FOR-BYTE
    /// IDENTICAL between the two (confirmed by directly reading both: FUN_39cfcd10 in x2game-dev.dll vs.
    /// FUN_39cc4310 in x2game-dev_dedicate.dll - same offsets, same field names, same call order, only the data-
    /// tag constant addresses differ, which is expected for two separately-linked binaries built from the same
    /// source). This is a strong basis for reuse, not a guess - but "Zone accepts these bytes without erroring"
    /// (confirmed) is not the same as "the client renders correctly from them" (NOT yet confirmed live).
    /// </summary>
    private const int RequiredPaddingBytes = 36; // see WZDominionDataPacket's doc comment - same empirical gap, not yet structurally explained

    public override PacketStream Write(PacketStream stream)
    {
        // +0x00/+0x04 - the native deserializer's first two fields are unnamed (a reused generic tag, not a
        // readable string) but match ZoneId(2B)/FactionId(4B)'s widths exactly. +0x04 is real: confirmed via
        // GetOwnerFaction's read path (see FactionId's doc comment above) - NOT the guild id, a top-level
        // FactionsEnum value (148 Nuia / 149 Harihara).
        stream.Write(ZoneId);
        stream.Write((uint)FactionId);
        stream.Write(House);
        stream.Write(TaxRate);
        // Point (dominion's own claimed center) - written HERE in wire order, matching the native call sequence
        // exactly (house, taxRate, THEN this Point call, THEN the moneyAmount loop below).
        stream.Write(Helpers.ConvertLongX(X));
        stream.Write(Helpers.ConvertLongY(Y));
        stream.Write(Z);
        // 5x "moneyAmount" (8B each, not 4B as the pre-2026-08-20 model assumed)
        stream.Write((long)CurHouseTaxMoney);
        stream.Write((long)CurHuntTaxMoney);
        stream.Write((long)PeaceTaxMoney);
        stream.Write((long)CurHouseTaxAaPoint);
        stream.Write((long)PeaceTaxAaPoint);
        stream.Write((ulong)Helpers.UnixTime(LastPaidTime));
        stream.Write((ulong)Helpers.UnixTime(LastSiegeEndTime));
        stream.Write((ulong)Helpers.UnixTime(ReignStartTime));
        // "lastTaxRateChangedTime" - written here in wire order (8B unix-seconds, matches the 2026-08-19 fix)
        stream.Write((ulong)Helpers.UnixTime(LastTaxRateChangedTime));
        // "point" (4B) - semantic unconfirmed, sent as 0 (safe - see WZDominionDataPacket's doc comment on why
        // a wrong VALUE in a fixed-size scalar is safe while a wrong WIDTH/count is not).
        // ObjId has NO wire representation at all in this packet - confirmed now, not just suspected (the
        // real struct only has this one 4-byte "point" field here).
        stream.Write(0);
        // "bc" - an id-shaped WriteBc field (3B payload), semantic unconfirmed, sent as 0.
        stream.WriteBc(0u);
        stream.Write(TerritoryData ?? new DominionTerritoryData());
        // Roster container - SiegeTimers.SiegePeriod is a real, already-tracked field; the rest of the old
        // DominionSiegeTimers/DominionUnkData model (Durations/Started/Fixed/Bdm/UnkData/Unk2Data) does NOT
        // match the real structure at all (confirmed 2026-08-20) and is no longer written - see
        // WZDominionDataPacket.WriteEmptyRosterRecord for the equivalent, already-verified logic (this codebase
        // has no real raid-team-roster data to populate here regardless, same as the WZ side).
        stream.Write(SiegeTimers?.SiegePeriod ?? (byte)0);
        WriteEmptyRosterRecord(stream);
        stream.Write(0u); // "offense" vector count = 0
        stream.Write(false); // siegeSecondHalf
        stream.Write((ulong)Helpers.UnixTime(NonPvPStart));
        stream.Write(NonPvPDuration);

        // TEMPORARY, 2026-08-20 - see WZDominionDataPacket's RequiredPaddingBytes doc comment. Same struct,
        // same empirically-required gap (live-bisected on the WZ side; not yet independently re-bisected here,
        // but the deserializer is byte-identical so this is a strong-but-not-proven carry-over, not a guess).
        for (var i = 0; i < RequiredPaddingBytes; i++)
            stream.Write((byte)0);

        return stream;
    }

    private static void WriteEmptyRosterRecord(PacketStream stream)
    {
        stream.Write(0u);      // unnamed leading 4-byte field
        stream.WriteBc(0u);    // id
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

public class DominionTerritoryData : PacketMarshaler
{
    public uint Id { get; set; }
    public uint Id2 { get; set; }
    public byte MaxGates { get; set; }
    public byte MaxWalls { get; set; }
    public short RadiusDeclare { get; set; }
    public ushort RadiusDominion { get; set; }
    public short RadiusOffenseHq { get; set; }
    public short RadiusSiege { get; set; }

    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(Id);
        stream.Write(Id2);
        stream.Write(MaxGates);
        stream.Write(MaxWalls);
        stream.Write(RadiusDeclare);
        stream.Write(RadiusDominion);
        stream.Write(RadiusOffenseHq);
        stream.Write(RadiusSiege);
        return stream;
    }
}

public class DominionSiegeTimers : PacketMarshaler
{
    public int[] Durations { get; set; } = new int[5];
    public DateTime Started { get; set; }
    public DateTime Fixed { get; set; }
    public int Bdm { get; set; }

    public byte SiegePeriod { get; set; }

    public DominionUnkData UnkData { get; set; }
    public DominionUnkData Unk2Data { get; set; }

    public override PacketStream Write(PacketStream stream)
    {
        foreach (var duration in Durations)
            stream.Write(duration);
        stream.Write(Started);
        stream.Write(Fixed);
        stream.Write(Bdm);
        // ---------------------------------
        stream.Write(SiegePeriod);
        // ---------------------------------
        stream.Write(UnkData);
        stream.Write(Unk2Data);
        return stream;
    }
}

public class DominionUnkData : PacketMarshaler
{
    public uint Id { get; set; } // TODO ExpeditionId
    public uint ObjId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public byte Ni { get; set; }
    public byte Nr { get; set; }

    public byte Limit { get; set; }
    public uint[] UnkIds { get; set; }

    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(Id);
        stream.WriteBc(ObjId);
        stream.Write(Helpers.ConvertLongX(X));
        stream.Write(Helpers.ConvertLongY(Y));
        stream.Write(Z);
        stream.Write(Ni);
        stream.Write(Nr);
        // -------------------------------
        stream.Write(Limit);
        stream.Write((byte)UnkIds.Length);
        foreach (var unkId in UnkIds)
            stream.Write(unkId);
        return stream;
    }
}

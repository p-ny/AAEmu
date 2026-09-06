using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.G2C;

public class SCFactionPowerScorePacket() : GamePacket(SCOffsets.SCFactionPowerScorePacket, 1)
{
    public override PacketStream Write(PacketStream stream)
    {
        // 2026-08-19 RE fix: the old 21-byte body (4 zero u32s + a season dword + a status byte) didn't match
        // the real client deserializer at all. Traced via RTTI to x2game-dev.dll's FUN_39c87df0: the real
        // fields are superiors/inferiors/weekNum/scores/isDiplomacyConfig/configs. superiors/inferiors/configs
        // are each a std::set<uint32> (a 4-byte count then that many 4-byte faction ids) - confirmed HIGH
        // confidence by directly decompiling their shared serializer (FUN_39c82230): the count and each
        // element both go through the same generic 4-byte-value vtable call, seen twice independently.
        // scores is a std::vector of 40-byte compound elements (FUN_39ace640) - element shape not decoded,
        // not needed since it's sent empty here. weekNum/isDiplomacyConfig's exact vtable-slot widths were
        // NOT independently cross-validated (unlike the array/count format) - written here as a plain 4-byte
        // int and a 1-byte bool, the natural encoding for those types elsewhere in this codebase; treat as
        // best-effort, not confirmed, if this packet is revisited.
        //
        // No real per-faction hierarchy/score data is wired server-side yet, so superiors/inferiors/scores/
        // configs are sent empty (count=0) - matches the honest "empty but correctly-shaped" scope of this
        // fix. isDiplomacyConfig is set true deliberately: tab_relation.lua's
        // `nationRelationButton:Enable(X2Nation:CanDiplomacy())` gates the whole nation-diplomacy UI tab on
        // this exact flag, and the whole point of this fix is to stop that tab from being permanently dead -
        // real per-nation diplomacy business rules (if any) still need to be designed, this only unblocks the
        // client-side gate.
        stream.Write(0u); // superiors: count = 0 (empty set, no real faction-hierarchy data wired yet)
        stream.Write(0u); // inferiors: count = 0
        stream.Write(1); // weekNum: placeholder, matches the old stub's "season/index" value; no real week-tracking wired yet
        stream.Write(0u); // scores: count = 0 (empty vector, no real per-faction score data wired yet)
        stream.Write(true); // isDiplomacyConfig: true, to unblock the client's CanDiplomacy()-gated UI tab
        stream.Write(0u); // configs: count = 0 (empty set, no real per-nation diplomacy-config data wired yet)

        return stream;
    }
}

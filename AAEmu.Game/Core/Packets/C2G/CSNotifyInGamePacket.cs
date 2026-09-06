using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Chat;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSNotifyInGamePacket() : GamePacket(CSOffsets.CSNotifyInGamePacket, 1)
{
    public override void Read(PacketStream stream)
    {
        // No data
    }

    public override void Execute()
    {
        // Commercial World: zone is sim authority. No healthy zone → do not enter on local Game sim.
        if (WorldIntegration.ZoneAuthority)
        {
            if (Connection.ActiveChar == null)
            {
                Logger.Error("NotifyInGame: no active character is available; closing the session");
                Connection.Shutdown();
                return;
            }

            if (WorldIntegration.TryEnterZone == null)
            {
                const string reason = "zone authority is enabled but its enter route is unavailable";
                Logger.Error("NotifyInGame: {0}; returning to character select", reason);
                if (!EnterWorldManager.Instance.ReturnToCharacterSelect(Connection, reason))
                    Connection.Shutdown();
                return;
            }

            var body = WorldIntegration.BuildWzUnitStateBody(Connection.ActiveChar);
            if (!WorldIntegration.TryEnterZone(Connection.ActiveChar.ObjId, body))
            {
                Logger.Error(
                    "NotifyInGame: zone enter refused for {0}; returning to character select",
                    Connection.ActiveChar.Name);
                if (!EnterWorldManager.Instance.ReturnToCharacterSelect(
                        Connection,
                        "the requested zone is not available"))
                    Connection.Shutdown();
                return;
            }
        }

        Connection.ActiveChar.IsOnline = true;

        // First packet the reference pushes once the context reaches INGAME — enables the client's gameplay
        // feature/HUD systems before the player frame renders.
        Connection.ActiveChar.SendPacket(new SCSystemFeatureStateListPacket());

        // Temporary: still Spawn for CS/SC client glue until World relays ZW→SC fully.
        // Zone already owns presence when ZoneAuthority + TryEnterZone succeeded above.
        Connection.ActiveChar.Spawn();

        // DO NOT seed the physics clock from the server's Environment.TickCount64 here. That is the SERVER
        // uptime domain (~tens of millions of ms), NOT the client's physics clock (which starts near 0 at
        // client launch). Seeding it made every self/NPC stand carry a tPhy ~89,000,000 ms in the client's
        // "future"; the client's real clock (~140,000 ms) then saw its own movements time-stamped far ahead,
        // its client-driven-movement binding broke ("can't load client driven connect info"), and it dropped
        // the connection a few seconds after spawn. The anchor is now seeded ONLY from client-reported values
        // (PingPacket.tm / CSMoveUnit.Time), which are in the client's own clock domain. MirrorMovementStream
        // simply waits (HasPhysTimeAnchor == false) until the first client ping arrives — that happens within
        // ~1s, well before any idle watchdog.

        // NOTE: do NOT deliver the local player via a self SCUnitState. It reaches the X+8 bind
        // crash-prone: for the local unit the client builds an actor-less EmptyUnitModel placeholder (its
        // delivery mechanism, not the data. The reference server sends NO self SCUnitState; the client builds the
        // player natively (fully model-loaded) and binds X+8 there. Fixing X+8 must go through that native path.

        // Joining channel 1 (shout) will automatically also join /lfg and /trade for that zone on the client-side
        // Back in 1.x /trade was zone based, not faction based
        var zoneChat = ChatManager.Instance.GetZoneChat(Connection.ActiveChar.Transform.ZoneId);
        if (!zoneChat.JoinChannel(Connection.ActiveChar)) // shout, trade, lfg
            zoneChat.AnnounceTo(Connection.ActiveChar);  // already a member from OnZoneChange - tell the client anyway
        ChatManager.Instance.GetNationChat(Connection.ActiveChar.Race).JoinChannel(Connection.ActiveChar); // nation
        // TODO: Implement crime system, actual jury channel doesn't exist yet
        Connection.ActiveChar.SendPacket(new SCJoinedChatChannelPacket(ChatType.Judge, 0, Connection.ActiveChar.Faction.MotherId)); //trial
        ChatManager.Instance.GetFactionChat(Connection.ActiveChar.Faction.MotherId).JoinChannel(Connection.ActiveChar); // faction
        ChatManager.Instance.GetGlobalChat().JoinChannel(Connection.ActiveChar); // CSM - server-wide, both factions

        // TODO: Maybe move to spawn character?
        TeamManager.Instance.UpdateAtLogin(Connection.ActiveChar);
        Connection.ActiveChar.Expedition?.OnCharacterLogin(Connection.ActiveChar);

        // Recovered 2026-08-13 via Ghidra: the client's baseline UnitState sync has no expedition/guild
        // field at all - a unit's guild membership only ever reaches the client through this delta
        // packet. Previously only sent at the moment of an actual membership change (create/
        // invite-accept/leave/kick), so an observer who wasn't watching at that exact moment - including
        // the character's own client on every later login/relog - never learned it, leaving the
        // nameplate tag permanently blank despite correct server-side data. Must fire after Spawn()
        // (above), matching every other post-spawn identity broadcast in this method (SyncExpedition's
        // WZ-side equivalent in PlayerEnterService.EnterZone follows the same rule) - a first attempt
        // sent this from CSSelectCharacterPacket.cs, before the character exists in any zone/observer
        // context, and had no effect. See aaemu-fixes-applied memory for the full recovery writeup.
        if (Connection.ActiveChar.Expedition != null)
        {
            var activeChar = Connection.ActiveChar;
            // Broadcast with the real ObjId so nearby viewers update this character's nameplate/roster entry.
            activeChar.BroadcastPacket(
                new SCUnitExpeditionChangedPacket(activeChar.ObjId, activeChar.Id, "", activeChar.Name, 0, (uint)activeChar.Expedition.Id, false),
                true);
            // 2026-08-27: Ghidra-confirmed (FUN_396c3aa0, the native handler for this packet) unitId=0 is a
            // sentinel meaning "this update is about YOU, the reader" - it's the only branch that writes the
            // client's own MyExpeditionId cache (cache+0x36c, read by X2Faction:GetMyExpeditionId/
            // IsExpedInfoLoaded). Any nonzero unitId - including the character's own real ObjId, as sent
            // above - takes the "some OTHER unit changed" branch instead, which never touches that cache.
            // Since the broadcast above always used the real ObjId, this character's own client never once
            // learned its own guild id, so IsExpedInfoLoaded() (cache+0x0 == cache+0x36c) permanently failed
            // and blocked the whole guild UI (member list stuck loading, invite/level-up disabled) for any
            // character who already had a guild BEFORE this exact packet fired for them. This second, unicast
            // copy with unitId=0 is what actually primes that cache - see aaemu-guild-systems-gaps memory.
            activeChar.SendPacket(
                new SCUnitExpeditionChangedPacket(0, activeChar.Id, "", activeChar.Name, 0, (uint)activeChar.Expedition.Id, false));
            // 2026-08-27, follow-up (Ghidra-confirmed via FUN_396b5f40, called by the self branch above right
            // before it writes cache+0x36c): that helper ALSO unconditionally resets cache+0x0 to 0
            // (`*param_1 = DAT_3b4f4fa8`). The self branch only re-populates cache+0x36c afterward, never
            // cache+0x0 - so right after the unitId=0 packet above, cache+0x0=0 while cache+0x36c=<real guild
            // id>, which is the exact mismatch IsExpedInfoLoaded() checks for, AND (observed live) breaks the
            // local player's own nameplate guild tag, which reads the same cached state.
            //
            // 2026-08-27, SECOND follow-up: a bare extra SCExpeditionDescPacket here DID fix the nameplate but
            // broke member list/permissions/buttons again - those latch eagerly off the FULL desc+policies+
            // members+end sequence arriving together and in that order (see aaemu-guild-systems-gaps memory),
            // and a lone desc packet with no policies/members right after it isn't that sequence. Moved the
            // whole SendExpeditionInfo call here (was in CSSelectCharacterPacket.cs, which ran too early -
            // before this self-notify's cache+0x0 reset, with nothing after to undo it) so the complete,
            // correctly-ordered sequence is what actually runs last during login.
            ExpeditionManager.SendExpeditionInfo(activeChar);

            // Independent of the fragile guild-info sequence above (SCHouseTaxInfoPacket is the
            // long-established personal-housing tax packet, not part of that sequence) - see
            // HousingManager.SendExpeditionHouseInfo's doc comment for why this has to be pushed here
            // too, not just at residence-placement time.
            HousingManager.Instance.SendExpeditionHouseInfo(activeChar);
        }

        Connection.ActiveChar.UpdateGearBonuses(null, null);

        // Combat resources (combat_resources) after Spawn(): seeding applies each pool's bar buff, and
        // both that buff and the point packet address the local player unit, which only exists once the
        // character is spawned. default_point had never been read, so every pool started each session at
        // 0 and the abilities gated on them could not reach their first tier.
        Connection.ActiveChar.InitializeCombatResources();
        Connection.ActiveChar.SendAllCombatResources();

        // The player-frame event window shows during the post-NotifyInGame load and reads its event counts; the
        // client crashes on show without them. The reference server sends this (all-zero, no active events) at
        // world entry — emit it here so the window has data before it renders.
        Connection.ActiveChar.SendPacket(new SCEventInfoCountPacket());

        // World-level state for the GetWorldLevel HUD provider. Must be sent AFTER Spawn() (above): the client's
        // world-level manager binds this data to the local player unit, so the unit has to exist or its link
        // (*(ClientPlayer+104)+8) stays null and the provider null-derefs when the player-frame event window shows.
        // The reference emits 0x038A ~4s after NotifyInGame, never in the select burst.
        Connection.ActiveChar.SendPacket(new SCWorldLevelInfoPacket());

        // Daily schedule: load persisted contracts for today, then reset-count budget.
        TodayAssignmentManager.Instance.OnCharacterEnterWorld(Connection.ActiveChar);

        // Push current Dominion/Castle ownership so the client's territory UI has data without waiting on a
        // CSRequestDominionDataPacket round-trip. 2026-08-24: guild dominions (Exeloch/Sungold) live in a
        // separate manager since last night's split - this call site (and CSRequestDominionSummaryPacket/
        // CSRequestDominionDataPacket) only ever pushed DominionManager's own set, so the client never received
        // TerritoryData for those two zones after the split at all - explains the missing world-map circle.
        DominionManager.Instance.SendAllDominionsTo(Connection);
        GuildDominionManager.Instance.SendAllDominionsTo(Connection);

        // Hero panel data (phase/candidates/rankings) - this whole opcode family didn't exist in this codebase
        // until 2026-08-14, so the client never had a way to learn any of this before now regardless of
        // whether the election logic itself was running.
        HeroManager.Instance.SendHeroInfo(Connection.ActiveChar);

        // Lobby already sent these during FinishState 0, but the in-world player object
        // is built later and does not keep that map. Listing authority is read here.
        Connection.SendPacket(new SCAccountAttributeConfigPacket());
        AccountAttributePublisher.Send(Connection);

        // Mirror interest armed on NotifyInGameCompleted — not here during load.
        Logger.Info($"NotifyInGame: {Connection.ActiveChar?.Name} ({Connection.ActiveChar?.Id}) zoneAuth={WorldIntegration.ZoneAuthority}");
    }
}

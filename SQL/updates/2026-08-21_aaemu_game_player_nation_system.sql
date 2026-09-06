USE aaemu_game;

-- Phase 1 of the player-founded-nation system. A founded nation is a REAL third-tier faction (peer to Nuia/
-- Haranya/pirates), not just a label - it needs its own unique FactionsEnum id and a way to remember which
-- guild founded it (so a voluntary disband can hand the territory back). See NationManager.DeclareIndependence.

-- The real, unique top-level faction id allocated for this nation at founding time (see
-- NationManager.AllocateNationFactionId - a fresh id per nation, never reused, kept far outside both the
-- system_factions range (max observed 221) and the expeditions/guild id range (max observed ~1001) to avoid
-- ever colliding with either). 0 = not yet founded (should never be read for a row that exists, since a row
-- only exists once DeclareIndependence succeeds).
ALTER TABLE `nations` ADD COLUMN `faction_id` INT UNSIGNED NOT NULL DEFAULT 0 AFTER `sovereign_character_id`;

-- The guild that founded this nation - kept so a voluntary disband (NationManager.Disband, forced=false) knows
-- which guild's ownership to revert the dominion to. Distinct from "guilds currently in the nation"
-- (expeditions.mother) since the founder is a special case that survives even if it later left/rejoined.
ALTER TABLE `nations` ADD COLUMN `founding_expedition_id` INT NOT NULL DEFAULT 0 AFTER `faction_id`;

-- Per-character temp-faction bookkeeping for nation membership (join = temp faction, revert-on-leave to home
-- faction). Unit.cs's existing OriginFaction/IsTempFaction pair is runtime-only (battleground use, reset every
-- login) - nation membership needs to survive a relog, so it's persisted here. 0 = not currently in a temp
-- faction (normal state for the vast majority of characters).
ALTER TABLE `characters` ADD COLUMN `origin_faction_id` INT UNSIGNED NOT NULL DEFAULT 0 AFTER `faction_id`;

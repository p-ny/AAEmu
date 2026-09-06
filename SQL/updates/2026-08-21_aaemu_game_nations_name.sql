USE aaemu_game;

-- Real, player-chosen nation name at founding time. No live client-facing mechanism currently delivers a
-- player-entered string into DeclareIndependence's trigger path (quest-completion only, zero text payload) -
-- this column exists so a name can be recorded/set (e.g. via a GM command) even though nothing in the current
-- wire protocol can DISPLAY it back to the client yet (SCDominionDataPacket/DominionData has no string field at
-- all, confirmed by direct RE this session - see DominionData.cs's own doc comments). NULL until set.
ALTER TABLE `nations` ADD COLUMN `name` VARCHAR(64) NULL DEFAULT NULL AFTER `sovereign_character_id`;

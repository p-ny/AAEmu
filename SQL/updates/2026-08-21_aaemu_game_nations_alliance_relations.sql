USE aaemu_game;

-- Diplomatic relations from a founded player nation toward the two pre-existing alliance factions (Nuia 148 /
-- Haranya 149), per the user's real (wiki-sourced, v2.9 Ascension) design: the Sovereign can independently set
-- Neutral/Allied/Hostile toward each. Stored directly on `nations` (only ever 2 targets, unlike the open-ended
-- nation-vs-nation `nation_relations` table which is correctly zone_id-keyed for that different case). Values
-- match the existing RelationState enum (Hostile=1, Neutral=2, Friendly=3 - "Friendly" here means "Allied").
ALTER TABLE `nations` ADD COLUMN `relation_nuia` TINYINT UNSIGNED NOT NULL DEFAULT '2' AFTER `name`;
ALTER TABLE `nations` ADD COLUMN `relation_haranya` TINYINT UNSIGNED NOT NULL DEFAULT '2' AFTER `relation_nuia`;

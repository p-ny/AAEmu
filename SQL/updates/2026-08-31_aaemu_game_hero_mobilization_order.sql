USE aaemu_game;

-- Hero "Mobilization Order" (X2Faction:RequestIssuanceOfMobilizationOrder /
-- CSFactionIssuanceOfMobilizationOrderPacket + CSFactionMobilizationOrderPacket) - a serving Hero consumes
-- one of the war/choice/peace flag items (46174/46177/46178) to issue a faction-wide order. No confirmed
-- data-driven cap was found (unlike hero_rewards.dominion_point_weekly_count for Dominion Points), so these
-- columns only track counts for display (SCHeroMobilizationOrderUpdatedPacket's todayCount/totalCount
-- fields) - see HeroManager.IssueMobilizationOrder.
ALTER TABLE `characters` ADD COLUMN `mobilization_order_today_count` INT NOT NULL DEFAULT '0' AFTER `last_dominion_point_give_time`;
ALTER TABLE `characters` ADD COLUMN `mobilization_order_total_count` INT NOT NULL DEFAULT '0' AFTER `mobilization_order_today_count`;
ALTER TABLE `characters` ADD COLUMN `last_mobilization_order_time` DATETIME NOT NULL DEFAULT '1970-01-01 00:00:00' AFTER `mobilization_order_total_count`;

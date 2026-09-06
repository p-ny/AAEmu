USE aaemu_game;

-- Hero "Dominion Points" (X2Hero:GiveDominionPoint/DominionPointCount) - a serving Hero may distribute
-- points to a dominion their faction owns, once per day, up to a weekly cap from their hero_rewards
-- ranking's dominion_point_weekly_count (7/10/15). last_dominion_point_give_time drives both the daily
-- gate and the weekly rollover for dominion_point_weekly_given - see HeroManager.GiveDominionPoint.
ALTER TABLE `characters` ADD COLUMN `dominion_point_weekly_given` INT NOT NULL DEFAULT '0' AFTER `last_daily_leadership_point_time`;
ALTER TABLE `characters` ADD COLUMN `last_dominion_point_give_time` DATETIME NOT NULL DEFAULT '1970-01-01 00:00:00' AFTER `dominion_point_weekly_given`;

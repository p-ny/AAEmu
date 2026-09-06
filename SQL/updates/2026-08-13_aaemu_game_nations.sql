USE aaemu_game;

-- Independent player nations: a Dominion whose owning Expedition completed the founding quest chain
-- (quest_context 7983 donate -> 7984/7985 sabotage -> 7986 "국가의 상징"/"A Nation is Born", craft+deliver the
-- Great Prosperity Seal). One row per zone_id that has become a nation; the Dominion itself keeps living in
-- `dominions` (same primary key domain) - this table only adds the "is independent" + Sovereign fact on top.
CREATE TABLE IF NOT EXISTS `nations` (
  `zone_id` smallint unsigned NOT NULL COMMENT 'matches dominions.zone_id',
  `sovereign_character_id` int unsigned NOT NULL COMMENT 'the character who completed the founding quest',
  `declared_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`zone_id`) USING BTREE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Independent player nations founded on top of a claimed Dominion';

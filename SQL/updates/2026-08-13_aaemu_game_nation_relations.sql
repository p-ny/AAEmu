USE aaemu_game;

-- Nation-to-nation diplomacy (friend/hostile), confirmed real via the __MAIL_NATION_RELATION_* mail templates
-- found in x2game-dev.dll strings and the existing (previously unused) MailType.NationRelation* enum values.
-- No client packet exists for this in r575 at all (checked - no CS*Nation* class anywhere), so this is exposed
-- via chat command (/nationrelation, /nationrelationrespond) rather than a guessed opcode.
CREATE TABLE IF NOT EXISTS `nation_relations` (
  `zone_id_a` smallint unsigned NOT NULL COMMENT 'always the smaller of the two zone ids - canonical ordering',
  `zone_id_b` smallint unsigned NOT NULL COMMENT 'always the larger of the two zone ids',
  `status` enum('pending','friend','hostile') NOT NULL DEFAULT 'pending',
  `requested_by_zone_id` smallint unsigned NOT NULL,
  `requested_friend` tinyint(1) NOT NULL DEFAULT '1',
  `updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`zone_id_a`, `zone_id_b`) USING BTREE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='Nation-to-nation friend/hostile relations';

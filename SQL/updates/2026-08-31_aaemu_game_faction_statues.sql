USE aaemu_game;

-- Construction/decay state for the 3 Hero capital-city Statues (Nuia doodad_almighty 10588, Haranya 10650,
-- Pirate 10651). These are world-decoration doodads (spawned from doodad_spawns.json, IsPersistent=false
-- by design - same path as every other ambient object) so they don't round-trip through the generic
-- `doodads` table. This dedicated table is a narrow, additive persistence hook scoped ONLY to these 3
-- template ids - see SpawnManager.SpawnAll (restore) and DoodadFuncFactionStatueDevote.Use (write).
CREATE TABLE IF NOT EXISTS `faction_statues` (
  `template_id` INT unsigned NOT NULL,
  `func_group_id` INT unsigned NOT NULL,
  `data` INT NOT NULL DEFAULT '0',
  `updated_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`template_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

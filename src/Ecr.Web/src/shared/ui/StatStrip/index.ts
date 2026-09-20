/**
 * Смуга показників переліку (`UI-06`, шар 3).
 *
 * ⚠ Тека, а не один файл: сусідні компоненти шару 3 (`DataTable/`) теж
 * теками, і так домовлено в директиві (§2, «нові теки `shared/ui/DataTable/**`
 * тощо»). Точка входу одна — цей файл.
 */
export {
  StatStrip,
  StatStripMaxItems,
  capItems,
  problemTone,
  type StatItem,
  type StatStripItems,
  type StatStripProps,
  type StatTone,
} from './StatStrip';

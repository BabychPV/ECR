import type { ColumnDto, RowDto } from '@/api/types';

/**
 * ✎ B175: значення за замовчуванням незаповненої комірки — дефолт РЯДКА шаблону (`RowDto.defaults`),
 * інакше дефолт колонки (`ColumnDto.defaultValue`, ФВ-3.8). Порожній рядок у `defaults` — «у цьому рядку
 * дефолту колонки немає» (не `??`-порожнеча: `''` перекриває `defaultValue`). Те саме правило, що на сервері
 * (`ColumnDefaultSet.OfRow`): записана комірка > дефолт рядка > дефолт колонки. Лише показ — не пишеться (D-283).
 */
export function effectiveDefault(
  row: Pick<RowDto, 'defaults'>,
  column: Pick<ColumnDto, 'code' | 'defaultValue'>,
): unknown {
  return row.defaults?.[column.code] ?? column.defaultValue;
}

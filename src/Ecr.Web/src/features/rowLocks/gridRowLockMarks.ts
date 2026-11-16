import type { CellProps, CellTemplate, ColumnRegular } from '@revolist/revogrid';
import type { RowLockDto } from '@/features/documents/rowLocks/api';
import { lockHint } from './rowLockModel';
import './rowLocks.css';

/**
 * ✎ B139 (B75): значок «рядок заблоковано» в першій колонці сітки — поверх колонок, які зібрав `gridColumns`
 * (той самий прийом, що `withPermitMarks`: код сітки не чіпається).
 *
 * - значок — окремий елемент на ПОЧАТКУ комірки (не `::before`/`::after`: їх уже займають стани комірки в
 *   `shared/theme/cell-states.css`), з доступною назвою «Заблокував X до DD.MM.YYYY»;
 * - комірка дістає `data-row-lock` і те саме пояснення в `title` (додається до наявного).
 *
 * ⛔ Лише показ. Чи можна правити комірку заблокованого рядка, вирішує сервер (`cellPermissions`, причина
 * `RowLocked`): власник блокування править далі, решта — ні.
 *
 * ⚠ Без блокувань і колонки, якої це не стосується, повертаються ТІ САМІ об'єкти — RevoGrid не бачить змін.
 */
export function withRowLockMarks(
  columns: readonly ColumnRegular[],
  locksByRow: ReadonlyMap<string, RowLockDto>,
  rowKeyOf: (model: unknown) => string,
): ColumnRegular[] {
  if (locksByRow.size === 0 || columns.length === 0) return columns as ColumnRegular[];

  return columns.map((column, index) => {
    if (index !== 0) return column;

    const original = column.cellTemplate;
    const originalProps = column.cellProperties;

    const cellTemplate: CellTemplate = (h, props, extra) => {
      const base =
        original === undefined ? (props.value === null || props.value === undefined ? '' : String(props.value)) : original(h, props, extra);
      const lock = locksByRow.get(rowKeyOf(props.model));
      if (lock === undefined) return base;

      const hint = lockHint(lock);

      return h('span', { class: 'ecr-row-lock' }, [
        h('span', { class: 'ecr-row-lock-glyph', role: 'img', 'aria-label': hint, title: hint, 'data-row-lock-badge': String(lock.id) }),
        h('span', { class: 'ecr-row-lock-value' }, [base as ReturnType<typeof h>]),
      ]);
    };

    const cellProperties = (props: Parameters<NonNullable<ColumnRegular['cellProperties']>>[0]): CellProps => {
      const own = (originalProps?.(props) ?? {}) as CellProps;
      const lock = locksByRow.get(rowKeyOf(props.model));
      if (lock === undefined) return own;

      const hint = lockHint(lock);
      const title = typeof own['title'] === 'string' && own['title'].length > 0 ? `${own['title']}\n${hint}` : hint;

      return { ...own, title, 'data-row-lock': String(lock.id) } as CellProps;
    };

    return { ...column, cellTemplate, cellProperties };
  });
}

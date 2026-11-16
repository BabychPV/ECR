import { describe, expect, it } from 'vitest';
import type { ColumnRegular } from '@revolist/revogrid';
import type { RowLockDto } from '@/features/documents/rowLocks/api';
import { withRowLockMarks } from '../gridRowLockMarks';
import {
  activeLocksByRow,
  belongsTo,
  lockFormError,
  lockRequest,
  MaxNoteLength,
  pickedRowKeys,
  type LockForm,
  type LockTable,
} from '../rowLockModel';

/**
 * ✎ B139 (B75): чиста логіка блокувань рядків і значок у першій колонці сітки.
 */
const Table: LockTable = { sheetCode: 'S1', tableCode: 'T1', sheetCopyId: null };

function lock(over: Partial<RowLockDto> = {}): RowLockDto {
  return {
    canUnlock: true,
    id: 5,
    isActive: true,
    lockFrom: '2026-11-01',
    lockUntil: '2026-11-30',
    note: null,
    ownerDisplayName: 'A. Serikbayev',
    ownerUserId: 3,
    rowKey: 'R2',
    sheetCode: 'S1',
    sheetCopyId: null,
    tableCode: 'T1',
    ...over,
  };
}

const form = (over: Partial<LockForm> = {}): LockForm => ({
  rowKeys: ['R1'],
  fromDate: '2026-11-10',
  toDate: '2026-11-20',
  note: '',
  ...over,
});

describe('rowLockModel', () => {
  it('блокування належить таблиці лише за аркушем, таблицею і копією разом', () => {
    expect(belongsTo(lock(), Table)).toBe(true);
    expect(belongsTo(lock({ tableCode: 'T2' }), Table)).toBe(false);
    // ⛔ Код таблиці унікальний лише в межах аркуша: той самий T1 іншого аркуша — чуже блокування.
    expect(belongsTo(lock({ sheetCode: 'S2' }), Table)).toBe(false);
    expect(belongsTo(lock({ sheetCopyId: 7 }), Table)).toBe(false);
    expect(belongsTo(lock({ sheetCopyId: 7 }), { ...Table, sheetCopyId: 7 })).toBe(true);
  });

  it('значок дістають лише ЧИННІ блокування цієї таблиці (ще не почате — ні)', () => {
    const byRow = activeLocksByRow(
      [lock({ id: 1, rowKey: 'R1' }), lock({ id: 2, rowKey: 'R2', isActive: false }), lock({ id: 3, rowKey: 'R3', tableCode: 'T9' })],
      Table,
    );

    expect([...byRow.keys()]).toEqual(['R1']);
  });

  it('перевірка форми: рядки, обидві дати, кінець не раніше початку, довжина примітки', () => {
    expect(lockFormError(form())).toBeNull();
    expect(lockFormError(form({ fromDate: '2026-11-10', toDate: '2026-11-10' }))).toBeNull();
    expect(lockFormError(form({ rowKeys: [] }))).toBe('noRows');
    expect(lockFormError(form({ toDate: '' }))).toBe('dates');
    expect(lockFormError(form({ fromDate: 'not-a-date' }))).toBe('dates');
    // ⛔ Мутація: прибрати порівняння дат — форма з кінцем раніше початку пішла б на сервер.
    expect(lockFormError(form({ toDate: '2026-11-09' }))).toBe('range');
    expect(lockFormError(form({ note: 'x'.repeat(MaxNoteLength + 1) }))).toBe('note');
    expect(lockFormError(form({ note: 'x'.repeat(MaxNoteLength) }))).toBeNull();
  });

  it('тіло запиту: адреса таблиці, період, рядки, дати; порожня примітка — null', () => {
    expect(lockRequest(form({ rowKeys: ['R1', 'R2'], note: '  ' }), { ...Table, sheetCopyId: 4 }, 202611)).toEqual({
      periodKey: 202611,
      sheetCode: 'S1',
      tableCode: 'T1',
      sheetCopyId: 4,
      rowKeys: ['R1', 'R2'],
      fromDate: '2026-11-10',
      toDate: '2026-11-20',
      note: null,
    });
    expect(lockRequest(form({ note: ' audit ' }), Table, 202611).note).toBe('audit');
  });

  it('рядки дії: виділений діапазон, інакше рядок фокусної комірки; рядок підсумків не потрапляє', () => {
    const rows = [{ rowKey: 'R1' }, { rowKey: 'R2' }, { rowKey: 'R3' }];

    expect(pickedRowKeys(rows, { fromRow: 1, toRow: 2 }, 0)).toEqual(['R2', 'R3']);
    expect(pickedRowKeys(rows, null, 0)).toEqual(['R1']);
    expect(pickedRowKeys(rows, null, null)).toEqual([]);
    // Діапазон, що зачепив рядок підсумків (індекс 3), обрізається до рядків даних.
    expect(pickedRowKeys(rows, { fromRow: 2, toRow: 3 }, null)).toEqual(['R3']);
  });
});

describe('withRowLockMarks', () => {
  type Node = { tag: string; props: Record<string, unknown>; children: unknown };
  const h = ((tag: string, props: Record<string, unknown>, children: unknown) => ({ tag, props, children })) as never;
  const rowKeyOf = (model: unknown): string => (model as { rowKey: string }).rowKey;
  const columns: ColumnRegular[] = [
    { prop: 'A', cellProperties: () => ({ title: 'own hint' }) },
    { prop: 'B' },
  ];

  it('без блокувань колонки повертаються ті самі', () => {
    expect(withRowLockMarks(columns, new Map(), rowKeyOf)).toBe(columns);
  });

  it('перша колонка заблокованого рядка: значок із доступною назвою і data-row-lock; решта колонок і рядків без змін', () => {
    const marked = withRowLockMarks(columns, new Map([['R2', lock()]]), rowKeyOf);

    expect(marked[1]).toBe(columns[1]);

    const first = marked[0] as ColumnRegular;
    const lockedProps = first.cellProperties?.({ model: { rowKey: 'R2' } } as never) as Record<string, unknown>;
    expect(lockedProps['data-row-lock']).toBe('5');
    // Власна підказка комірки лишається, пояснення блокування додається.
    expect(String(lockedProps['title'])).toMatch(/^own hint\n/);
    expect(String(lockedProps['title'])).toContain('rowLock.badgeHint');

    const cell = first.cellTemplate?.(h, { model: { rowKey: 'R2' }, value: 'Boiler' } as never, undefined as never) as Node;
    const [glyph, value] = cell.children as Node[];
    expect(glyph?.props['role']).toBe('img');
    expect(String(glyph?.props['aria-label'])).toContain('rowLock.badgeHint');
    expect(glyph?.props['data-row-lock-badge']).toBe('5');
    expect((value?.children as unknown[])[0]).toBe('Boiler');

    // Незаблокований рядок: ні значка, ні атрибута.
    expect(first.cellTemplate?.(h, { model: { rowKey: 'R1' }, value: 'Pump' } as never, undefined as never)).toBe('Pump');
    expect((first.cellProperties?.({ model: { rowKey: 'R1' } } as never) as Record<string, unknown>)['data-row-lock']).toBeUndefined();
  });
});

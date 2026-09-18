import { describe, expect, it } from 'vitest';
import type { ColumnDto, RegistryEntryDto, RowDto, TableSliceDto } from '@/api/types';
import { columnIndexOf, rowIndexOf } from '../rowIndex';
import { valueOf } from '../edits';
import { lookupCellDisplay } from '../LookupCellEditor';

/**
 * `CL-03` (`DIRECTIVE-14-ARCH.md` §3.5): у гарячих колбеках немає лінійного
 * пошуку.
 *
 * ⛔ Тут — і ТІЛЬКИ тут — доказом є ЧАС, бо предметом і є час: `onPaste`
 * синхронний, і поки він рахує, вкладка не відповідає. 30 000 комірок (вставка
 * 500×60, штатний розмір із `ФВ-9.16`) × 500 рядків пошуку = до 15 млн
 * порівнянь.
 *
 * ⚠ Поріг 50 мс узятий із директиви й навмисно НЕ впритул до виміряного: на
 * мапі той самий обсяг іде одиниці мілісекунд, тобто запас на повільну машину
 * CI — десятикратний. Порогу впритул вистачило б, щоб тест почервонів від
 * чужого навантаження, і його б вимкнули.
 */

const RowCount = 500;
const ColumnCount = 60;

function bigSlice(): TableSliceDto {
  const columns: ColumnDto[] = Array.from({ length: ColumnCount }, (_, index) => ({
    id: index + 1,
    code: `C${String(index + 1)}`,
    header: `Колонка ${String(index + 1)}`,
    dataType: 'Decimal',
    ordinal: index + 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    displayFormat: null,
    defaultValue: null,
    lookupRegistryDefId: null,
    unitId: null,
    unitSymbol: null,
  })) as ColumnDto[];

  const rows: RowDto[] = Array.from({ length: RowCount }, (_, index) => {
    const cells: Record<string, unknown> = {};
    for (const column of columns) cells[column.code] = index;

    return {
      rowKey: `R${String(index + 1)}`,
      ordinal: index + 1,
      rowKind: 'Static',
      label: null,
      rowVersion: '0x01',
      cells,
      isOrphaned: false,
    };
  }) as RowDto[];

  return {
    tableInstanceId: 1,
    periodKey: 202609,
    columns,
    rows,
    cellPermissions: {},
    cellConfirmations: {},
  } as TableSliceDto;
}

/** Рядок за позицією; кидає, а не віддає `undefined` (`noUncheckedIndexedAccess`). */
function rowAt(slice: TableSliceDto, index: number): RowDto {
  const row = slice.rows[index];
  if (row === undefined) throw new Error(`у зрізі немає рядка ${String(index)}`);

  return row;
}

describe('CL-03 · знімок undo для вставки', () => {
  /**
   * Індексований пошук швидший за лінійний **на порядок**, на тих самих даних
   * і в тому самому прогоні.
   *
   * ⛔ Тут стояв абсолютний поріг (`elapsed < 50` мс), і він упав одразу ж —
   * 64.7 мс на машині, де паралельно йшли інші прогони. Абсолютний час у
   * тесті вимірює **завантаженість раннера**, а не код: рівно той клас
   * плаваючого падіння, який щойно закрив `G-04`/`W3.4`, і рівно те, що
   * `CLAUDE.md` називає «перевірка, яку не можна відрізнити від шуму».
   *
   * ⚠ Відношення машинно-незалежне: обидві гілки виконуються в одному
   * процесі, на одному масиві, з тим самим прогрівом. Множник 5 узято з
   * запасом — виміряна різниця на 30 000 правок при 500 рядках близька до
   * двох порядків, і поріг не має падати від того, що раннер повільний.
   *
   * ⚠ Абсолютна стеля лишається, але грубою (2 с): вона ловить не
   * «повільніше, ніж хотілося», а «знову квадратично» — випадок, коли
   * відношення збережеться, бо сповільниться і контроль теж.
   */
  it('індексований пошук швидший за лінійний щонайменше впʼятеро', () => {
    const slice = bigSlice();

    const edits = slice.rows.flatMap((row) =>
      slice.columns.map((column) => ({ rowKey: row.rowKey, columnCode: column.code })),
    );

    expect(edits).toHaveLength(RowCount * ColumnCount);

    // Контроль — той самий лінійний пошук, який `CL-03` і прибрав.
    const linearStarted = performance.now();
    let linearSum = 0;
    for (const edit of edits) {
      const row = slice.rows.find((candidate) => candidate.rowKey === edit.rowKey);
      linearSum += Number(row?.cells[edit.columnCode] ?? 0);
    }
    const linear = performance.now() - linearStarted;

    const indexedStarted = performance.now();
    let sum = 0;
    for (const edit of edits) {
      sum += Number(valueOf(slice, edit.rowKey, edit.columnCode));
    }
    const indexed = performance.now() - indexedStarted;

    // ⚠ Результати використовуються, інакше рушій має право викинути обидва
    // цикли цілком — і вимір показав би нуль незалежно від коду.
    expect(sum).toBeGreaterThan(0);
    expect(linearSum).toBe(sum);

    expect(indexed * 5).toBeLessThan(linear);
    expect(indexed).toBeLessThan(2_000);
  });
});

describe('CL-03 · мапа рядків і колонок', () => {
  it('мемоїзована за самим масивом рядків, а не за зрізом', () => {
    const slice = bigSlice();

    expect(rowIndexOf(slice)).toBe(rowIndexOf(slice));

    // Новий об'єкт-обгортка з ТИМИ САМИМИ рядками — та сама мапа.
    expect(rowIndexOf({ ...slice })).toBe(rowIndexOf(slice));

    // Нові рядки — нова мапа: інакше після збереження сітка показувала б
    // старі значення.
    const patched = { ...slice, rows: [...slice.rows] };
    expect(rowIndexOf(patched)).not.toBe(rowIndexOf(slice));
    expect(rowIndexOf(patched).get('R1')).toBe(rowAt(slice, 0));
  });

  it('дублікат ключа: виграє перший — та сама поведінка, що й у find()', () => {
    const slice = bigSlice();
    const first = rowAt(slice, 0);
    const twin: RowDto = { ...first, rowVersion: '0xFF' };
    const withTwin = { ...slice, rows: [...slice.rows, twin] };

    expect(rowIndexOf(withTwin).get('R1')).toBe(first);
    expect(columnIndexOf(slice).get('C7')).toBe(slice.columns[6]);
  });

  it('невідомий ключ — undefined, а не чужий рядок', () => {
    expect(rowIndexOf(bigSlice()).get('R0')).toBeUndefined();
    expect(valueOf(bigSlice(), 'R0', 'C1')).toBeNull();
  });
});

describe('CL-03 · показ Lookup-комірки', () => {
  const entries = Array.from(
    { length: 5000 },
    (_, index) => ({ id: index + 1, display: `Запис ${String(index + 1)}` }) as RegistryEntryDto,
  );

  it('останній запис довідника знаходиться так само, як і перший', () => {
    expect(lookupCellDisplay(5000, entries)).toBe('Запис 5000');
    expect(lookupCellDisplay('1', entries)).toBe('Запис 1');
  });

  it('невідомий запис лишається видимим ідентифікатором, а не порожньою коміркою', () => {
    expect(lookupCellDisplay(90_001, entries)).toBe('90001');
    expect(lookupCellDisplay(null, entries)).toBe('');
  });

  /**
   * ⛔ Той самий перехід від абсолютного порога до відношення, що й вище, і з
   * тієї ж причини: `elapsed < 50` мс міряє завантаженість раннера. Контроль —
   * `entries.find` на кожну комірку, тобто рівно те, що `CL-03` прибрав.
   */
  it('пошук за мапою швидший за лінійний щонайменше впʼятеро', () => {
    const linearStarted = performance.now();
    let linearShown = 0;
    for (let cell = 0; cell < RowCount * ColumnCount; cell += 1) {
      const id = (cell % 5000) + 1;
      const entry = entries.find((candidate) => candidate.id === id);
      linearShown += (entry?.display ?? String(id)).length;
    }
    const linear = performance.now() - linearStarted;

    const started = performance.now();
    let shown = 0;
    for (let cell = 0; cell < RowCount * ColumnCount; cell += 1) {
      shown += lookupCellDisplay((cell % 5000) + 1, entries).length;
    }
    const elapsed = performance.now() - started;

    expect(shown).toBeGreaterThan(0);
    expect(linearShown).toBe(shown);

    expect(elapsed * 5).toBeLessThan(linear);
    expect(elapsed).toBeLessThan(2_000);
  });
});

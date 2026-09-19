import { describe, expect, it } from 'vitest';
import type { ColumnDto, RegistryEntryDto, RowDto, TableSliceDto } from '@/api/types';
import { columnIndexOf, rowIndexOf } from '../rowIndex';
import { valueOf } from '../edits';
import { lookupCellDisplay } from '../LookupCellEditor';

/**
 * `CL-03` (`DIRECTIVE-14-ARCH.md` §3.5): у гарячих колбеках немає лінійного
 * пошуку.
 *
 * ⛔ **Доказ — ЛІЧИЛЬНИК ЗВЕРНЕНЬ до масиву, а не час.** І це третя редакція
 * цього тесту; дві попередні впали, і обидві — не на дефекті:
 *
 * 1. `elapsed < 50 мс` — абсолютний поріг. Упав одразу (64.7 мс), бо на машині
 *    паралельно йшли інші прогони: він міряв завантаженість раннера.
 * 2. `indexed * 5 < linear` — відношення. Виглядало машинно-незалежним і теж
 *    упало на гейті: 78.7 проти 76.5, тобто **4.86× замість 5×**. Відношення
 *    вільне від швидкості машини, але не від її ПЛАНУВАЛЬНИКА: якщо між двома
 *    замірами процес витіснили, множник пливе.
 *
 * Предмет цього рядка плану — «лінійного пошуку в гарячому колбеку немає», і
 * це твердження **рахується, а не хронометрується**: масив загортається в
 * `Proxy`, який лічить звернення за індексом. Лінійний `find` торкається
 * ~N/2 елементів **на кожен** пошук; індекс проходить масив **один раз** і далі
 * не торкається взагалі. Числа відрізняються на три порядки й не залежать ні
 * від машини, ні від планувальника, ні від JIT.
 *
 * ⚠ Обсяг лишився продуктивним: 30 000 комірок (вставка 500×60, штатний розмір
 * із `ФВ-9.16`) × 500 рядків = до 15 млн порівнянь у синхронному `onPaste`.
 */

/** Масив, який лічить звернення за індексом. */
function counting<T>(items: readonly T[]): { readonly items: readonly T[]; readonly reads: () => number } {
  let reads = 0;

  const proxy = new Proxy(items, {
    get(target, property, receiver): unknown {
      // ⚠ Лічимо лише доступ ЗА ІНДЕКСОМ. `length`, `find`, ітератор і решта
      // властивостей — не звернення до елемента, і рахувати їх означало б
      // порівнювати різні речі в двох гілках.
      if (typeof property === 'string' && /^\d+$/.test(property)) reads += 1;

      return Reflect.get(target, property, receiver);
    },
  }) as readonly T[];

  return { items: proxy, reads: () => reads };
}

const RowCount = 500;
const ColumnCount = 60;

/**
 * Скільки запитів робить КОНТРОЛЬНИЙ (лінійний) цикл.
 *
 * ⚠ Не 30 000: кожне звернення крізь `Proxy` коштує пастки, і лінійний пошук
 * на повному обсязі дав би мільйони пасток — тест не вклався б у таймаут (це
 * сталося буквально, 5 с). Контраст «N/2 на кожен запит» проти «N один раз»
 * видно вже на двох сотнях, а індексована гілка однаково проганяється на
 * повних 30 000.
 */
const ControlSample = 200;

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
   * Індекс проходить масив рядків **один раз**, лінійний пошук — на кожен
   * запит. Рахуємо звернення, не час (чому саме так — у шапці файлу).
   */
  it('індексований пошук торкається масиву один раз, лінійний — на кожен запит', () => {
    const slice = bigSlice();

    const edits = slice.rows.flatMap((row) =>
      slice.columns.map((column) => ({ rowKey: row.rowKey, columnCode: column.code })),
    );

    expect(edits).toHaveLength(RowCount * ColumnCount);

    // ⚠ Контроль іде ВИБІРКОЮ, а не повним обсягом: кожне звернення крізь
    // `Proxy` коштує пастки, і лінійний пошук по всіх 30 000 запитів дав би
    // ~7 млн пасток — тест не вклався б у таймаут. Твердження від цього не
    // слабшає: контраст між «N/2 на кожен запит» і «N один раз» видно вже на
    // двох сотнях.
    // ⛔ Вибірка з КРОКОМ, а не перші N. Перша редакція брала `slice(0, 200)` —
    // а `edits` побудовані рядок за рядком, тож усі двісті припадали на перші
    // чотири рядки, і лінійний `find` знаходив їх на першому ж елементі: 940
    // звернень замість очікуваних десятків тисяч. Тест це й показав, і це
    // рівно те, заради чого лічильник і заведено: він ловить помилку в ЗАМІРІ,
    // якої секундомір не побачив би взагалі.
    const stride = Math.max(1, Math.floor(edits.length / ControlSample));
    const sample = edits.filter((_, index) => index % stride === 0).slice(0, ControlSample);

    const control = counting(slice.rows);
    const controlSlice = { ...slice, rows: control.items } as TableSliceDto;

    let linearSum = 0;
    for (const edit of sample) {
      const row = control.items.find((candidate) => candidate.rowKey === edit.rowKey);
      linearSum += Number(row?.cells[edit.columnCode] ?? 0);
    }

    // ⚠ Результати звіряються на ТІЙ САМІЙ вибірці, інакше «менше звернень»
    // можна отримати, просто нічого не знайшовши.
    let sampleSum = 0;
    for (const edit of sample) {
      sampleSum += Number(valueOf(controlSlice, edit.rowKey, edit.columnCode));
    }

    expect(sampleSum).toBeGreaterThan(0);
    expect(linearSum).toBe(sampleSum);

    // ⛔ Головне твердження — на ПОВНОМУ обсязі: індекс будується один раз,
    // тобто звернень рівно стільки, скільки рядків, скільки б запитів не було.
    // Не «менше, ніж у контролі» — точне число, яке не пливе ні від машини,
    // ні від планувальника, ні від JIT.
    const indexed = counting(slice.rows);
    const indexedSlice = { ...slice, rows: indexed.items } as TableSliceDto;

    let sum = 0;
    for (const edit of edits) {
      sum += Number(valueOf(indexedSlice, edit.rowKey, edit.columnCode));
    }

    expect(sum).toBeGreaterThan(0);
    expect(indexed.reads()).toBe(RowCount);

    // А контроль на двохстах запитах уже торкнувся масиву на два порядки
    // більше, ніж індекс на тридцяти тисячах.
    expect(control.reads()).toBeGreaterThan(RowCount * 20);
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
   * ⛔ Той самий лічильник звернень, що й вище, і з тієї ж причини: час тут
   * міряв би раннер. Контроль — `entries.find` на кожну комірку, тобто рівно
   * те, що `CL-03` прибрав.
   */
  it('мапа записів будується один раз, лінійний пошук — на кожну комірку', () => {
    const cells = RowCount * ColumnCount;

    // Контроль — вибіркою, з тієї ж причини, що й вище: пастки `Proxy` на
    // 30 000 × ~2500 звернень не вклалися б у таймаут.
    // ⚠ Ідентифікатори беруться з КРОКОМ по всьому довіднику, а не підряд: 1…200
    // лежать на початку масиву, і лінійний пошук знаходив би їх одразу — контроль
    // виміряв би везіння з порядком, а не лінійність.
    const step = Math.max(1, Math.floor(entries.length / ControlSample));

    const control = counting(entries);
    let linearShown = 0;
    let sampleShown = 0;
    for (let cell = 0; cell < ControlSample; cell += 1) {
      const id = ((cell * step) % entries.length) + 1;
      const entry = control.items.find((candidate) => candidate.id === id);
      linearShown += (entry?.display ?? String(id)).length;
      sampleShown += lookupCellDisplay(id, control.items).length;
    }

    expect(sampleShown).toBeGreaterThan(0);
    expect(linearShown).toBe(sampleShown);

    const indexed = counting(entries);
    let shown = 0;
    for (let cell = 0; cell < cells; cell += 1) {
      shown += lookupCellDisplay((cell % 5000) + 1, indexed.items).length;
    }

    expect(shown).toBeGreaterThan(0);
    expect(indexed.reads()).toBe(entries.length);
    expect(control.reads()).toBeGreaterThan(entries.length * 20);
  });
});

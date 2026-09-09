import { describe, it, expect } from 'vitest';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { cellStateClass, cellStateOf, type CellStateName } from '../cellState';
import { roundToScale } from '../rounding';

function column(overrides: Partial<ColumnDto> = {}): ColumnDto {
  return {
    id: 1,
    code: 'volume',
    header: 'Volume',
    dataType: 'Decimal',
    ordinal: 1,
    isReadOnly: false,
    isRequired: false,
    displayFormat: null,
    defaultValue: null,
    lookupRegistryDefId: null,
    unitId: null,
    unitSymbol: null,
    precision: 18,
    scale: 2,
    ...overrides,
  };
}

function slice(overrides: Partial<TableSliceDto> = {}): TableSliceDto {
  return {
    tableInstanceId: 1,
    periodKey: 202601,
    columns: [column()],
    rows: [
      {
        rowKey: 'R1',
        ordinal: 1,
        rowKind: 'Fixed',
        label: null,
        rowVersion: '0x01',
        cells: {},
        isOrphaned: false,
      },
    ],
    cellPermissions: {},
    cellConfirmations: {},
    ...overrides,
  };
}

const noFlags = { dirty: new Set<string>(), rounded: new Set<string>() };

describe('Стани комірки', () => {
  it('редагована комірка не має стану', () => {
    expect(cellStateOf(slice(), 'R1', column(), noFlags)).toBeNull();
  });

  it('заборонена колонка дає readOnly', () => {
    const s = slice({ columns: [column({ isReadOnly: true })] });

    expect(cellStateOf(s, 'R1', column({ isReadOnly: true }), noFlags)).toBe('readOnly');
  });

  it('обчислена комірка відрізняється від просто забороненої', () => {
    const s = slice({ cellPermissions: { 'R1:volume': 'CalculatedCell' } });

    // ⛔ Це різні стани, і плутати їх не можна: заборонену комірку не змінить
    // ніхто, а обчислена зміниться сама при наступному перерахунку.
    expect(cellStateOf(s, 'R1', column(), noFlags)).toBe('calculated');
  });

  it('осиротілий рядок перекриває решту станів', () => {
    const s = slice();
    s.rows[0]!.isOrphaned = true;

    const flags = { dirty: new Set(['R1:volume']), rounded: new Set<string>() };

    // ⚠ Пріоритет не косметичний: осиротілий рядок блокує подання документа
    // цілком (ФВ-8.13), і дізнатися про це з відмови ECR-SUB-4221 — надто пізно.
    expect(cellStateOf(s, 'R1', column(), flags)).toBe('orphaned');
  });

  it('незбережена правка перекриває округлення і обчислення', () => {
    const s = slice({ cellPermissions: { 'R1:volume': 'CalculatedCell' } });
    const flags = { dirty: new Set(['R1:volume']), rounded: new Set(['R1:volume']) };

    expect(cellStateOf(s, 'R1', column(), flags)).toBe('dirty');
  });

  it('округлена комірка видно, коли правку вже збережено', () => {
    const flags = { dirty: new Set<string>(), rounded: new Set(['R1:volume']) };

    expect(cellStateOf(slice(), 'R1', column(), flags)).toBe('rounded');
  });
});

describe('Розрізнення станів БЕЗ кольору (ФВ-14.18)', () => {
  const states: CellStateName[] = ['orphaned', 'dirty', 'rounded', 'calculated', 'readOnly'];

  it('кожен стан має власний клас', () => {
    const classes = states.map((state) => cellStateClass(state));

    // ⛔ П'ять різних класів, а не п'ять різних кольорів. Перевірка навмисно
    // не дивиться на фон: саме фон і є той єдиний носій, який заборонений.
    expect(new Set(classes).size).toBe(states.length);
  });

  it('клас читається з CSS у kebab-case', () => {
    expect(cellStateClass('readOnly')).toBe('ecr-cell ecr-cell--read-only');
    expect(cellStateClass('dirty')).toBe('ecr-cell ecr-cell--dirty');
  });

  it('відсутність стану не дає класу', () => {
    expect(cellStateClass(null)).toBe('');
  });
});

describe('Округлення при вставці (ФВ-9.16c, D-116)', () => {
  it('ФВ-9.16c: значення, що вкладається в масштаб, не округлюється і не позначається', () => {
    // ⛔ `null` тут значуще: позначка ставиться ЛИШЕ на змінені комірки,
    // інакше лічильник «округлено N значень» показував би всю таблицю.
    expect(roundToScale(12.34, column({ scale: 2 }))).toBeNull();
  });

  it('ФВ-9.16b: зайві знаки округлюються до масштабу колонки', () => {
    expect(roundToScale(12.3456, column({ scale: 2 }))).toBe(12.35);
  });

  it("від'ємне значення округлюється від нуля — як на сервері", () => {
    // ⛔ `Math.round(-2.5)` дає −2 (до +∞), а .NET з `AwayFromZero` — −3.
    // Розбіжність в один знак означала б, що клієнт надішле число, яке сервер
    // вважатиме неокругленим, і вставка відхилиться там, де мала пройти.
    expect(roundToScale(-2.5, column({ scale: 0 }))).toBe(-3);
    expect(roundToScale(2.5, column({ scale: 0 }))).toBe(3);
  });

  it('половина на межі подвійної точності округлюється вгору', () => {
    // `1.005 * 100` у подвійній точності дає 100.49999999999999.
    expect(roundToScale(1.005, column({ scale: 2 }))).toBe(1.01);
  });

  it('колонка без масштабу не округлюється', () => {
    expect(roundToScale(12.3456, column({ scale: null }))).toBeNull();
  });

  it('нечислова колонка не округлюється', () => {
    expect(roundToScale(12.3456, column({ dataType: 'Text', scale: 2 }))).toBeNull();
  });
});

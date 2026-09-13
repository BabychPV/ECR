import { describe, expect, it } from 'vitest';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { gridColumns } from '../DocumentGrid';
import { NoLocalFlags } from '../cellState';

/**
 * Finding 2 (High, Stage 1): маркер на самій комірці для відмови збереження —
 * той самий взірець, що й `.ecr-cell-required-input-blocked` (Q-306), але
 * тепер для будь-якої відмови, названої сервером у цьому патчі, а не лише
 * для обов'язкових вхідних колонок методології.
 *
 * ⚠ RevoGrid тут НЕ монтується: `gridColumns` — чиста функція, що будує опис
 * колонок для бібліотеки, і саме тому доступна для одиничного тесту без
 * реального веб-компонента (той самий підхід, що й `cellState.test.ts`).
 */

function column(overrides: Partial<ColumnDto> = {}): ColumnDto {
  return {
    id: 1,
    code: 'C1',
    header: 'Колонка 1',
    dataType: 'Decimal',
    ordinal: 1,
    isReadOnly: false,
    isRequired: false,
    displayFormat: null,
    defaultValue: null,
    lookupRegistryDefId: null,
    unitId: null,
    unitSymbol: null,
    ...overrides,
  };
}

function slice(overrides: Partial<TableSliceDto> = {}): TableSliceDto {
  return {
    tableInstanceId: 1,
    periodKey: 202609,
    columns: [column()],
    rows: [
      { rowKey: 'R1', ordinal: 1, rowKind: 'Item', label: null, rowVersion: '0x01', cells: {}, isOrphaned: false },
    ],
    cellPermissions: {},
    cellConfirmations: {},
    ...overrides,
  };
}

function cellPropsFor(
  columns: ReturnType<typeof gridColumns>,
  columnCode: string,
  rowKey: string,
): Record<string, unknown> {
  const found = columns.find((c) => c.prop === columnCode);
  if (found === undefined || typeof found.cellProperties !== 'function') {
    throw new Error(`Немає колонки ${columnCode} або в неї немає cellProperties`);
  }

  return found.cellProperties({ model: { __rowKey: rowKey }, prop: columnCode } as never) as Record<
    string,
    unknown
  >;
}

const noRequiredInput = { blocked: new Map<string, string>(), warning: new Map<string, string>() };

describe('gridColumns — маркер помилки збереження на комірці (Q-30x)', () => {
  it('без saveErrorByCell — жодного нового класу (поведінка не змінилась для решти сітки)', () => {
    const columns = gridColumns(slice(), false, NoLocalFlags, {}, noRequiredInput);
    const props = cellPropsFor(columns, 'C1', 'R1');

    expect(String(props['class'] ?? '')).not.toContain('ecr-cell-save-error');
  });

  it('комірка з saveErrorByCell отримує клас ecr-cell-save-error і текст у title', () => {
    const saveErrorByCell = new Map([['R1:C1', 'Колонка «C1» очікує число.']]);
    const columns = gridColumns(slice(), false, NoLocalFlags, {}, noRequiredInput, saveErrorByCell);
    const props = cellPropsFor(columns, 'C1', 'R1');

    expect(String(props['class'])).toContain('ecr-cell-save-error');
    expect(String(props['title'])).toContain('Колонка «C1» очікує число.');
  });

  it('маркер стосується ЛИШЕ названої сервером комірки, не всього рядка чи всієї колонки', () => {
    const twoRows = slice({
      rows: [
        { rowKey: 'R1', ordinal: 1, rowKind: 'Item', label: null, rowVersion: '0x01', cells: {}, isOrphaned: false },
        { rowKey: 'R2', ordinal: 2, rowKind: 'Item', label: null, rowVersion: '0x01', cells: {}, isOrphaned: false },
      ],
    });
    const saveErrorByCell = new Map([['R1:C1', 'Колонка «C1» очікує число.']]);
    const columns = gridColumns(twoRows, false, NoLocalFlags, {}, noRequiredInput, saveErrorByCell);

    expect(String(cellPropsFor(columns, 'C1', 'R1')['class'])).toContain('ecr-cell-save-error');
    expect(String(cellPropsFor(columns, 'C1', 'R2')['class'] ?? '')).not.toContain('ecr-cell-save-error');
  });

  it('маркер накладається поверх звичайного стану (dirty), а не замінює його', () => {
    const flags = { dirty: new Set(['R1:C1']), rounded: new Set<string>() };
    const saveErrorByCell = new Map([['R1:C1', 'Колонка «C1» очікує число.']]);
    const columns = gridColumns(slice(), false, flags, {}, noRequiredInput, saveErrorByCell);
    const props = cellPropsFor(columns, 'C1', 'R1');

    const className = String(props['class']);
    expect(className).toContain('ecr-cell--dirty');
    expect(className).toContain('ecr-cell-save-error');
  });
});

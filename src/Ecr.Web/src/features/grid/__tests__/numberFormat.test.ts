import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { DefaultLanguage, setLanguage } from '@/shared/i18n';
import { displayPaddingOf, MaxPaddedScale } from '@/shared/format';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { cellDisplay, editorValueOf } from '../cellValue';
import { gridColumns } from '../DocumentGrid';
import { NoLocalFlags } from '../cellState';
import { columnTotals, totalsRow } from '../gridTotals';
import { auditValueText } from '@/features/audit/auditValue';

/**
 * Баг людини 02.11 (живий стенд, документ 8 «Stationary Equipment», 202609):
 * числові комірки показувались `3.0000000000000000` і `1.000000000000…`.
 *
 * Причина: колонки імпорту Land мають `scale = 16` (`decimal(38,16)`, точність
 * СХОВИЩА), виходи методологій — 12; показ доповнював нулями до `scale` як до
 * формату комірки. Тепер доповнення — лише для `scale ≤ MaxPaddedScale`
 * (`displayPaddingOf`), одним правилом для сітки, підсумків, журналу аркуша й
 * аудиту.
 */

const Stored = '3.0000000000000000';

function norm(text: string): string {
  return text.replace(/[   ]/g, ' ');
}

function column(overrides: Partial<ColumnDto> = {}): ColumnDto {
  return {
    id: 1,
    code: 'C1',
    header: 'Running hours',
    dataType: 'Decimal',
    ordinal: 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    displayFormat: null,
    defaultValue: null,
    lookupRegistryDefId: null,
    unitId: null,
    unitSymbol: null,
    ...overrides,
  };
}

function slice(cells: Record<string, unknown>, columns: ColumnDto[]): TableSliceDto {
  return {
    tableInstanceId: 1,
    periodKey: 202609,
    columns,
    rows: [
      { rowKey: 'R1', ordinal: 1, rowKind: 'Item', label: null, rowVersion: '0x2A', cells, isOrphaned: false },
    ],
    cellPermissions: {},
    cellConfirmations: {},
  };
}

const noRequiredInput = { blocked: new Map<string, string>(), warning: new Map<string, string>() };

/** Текст, який намалює шаблон комірки сітки (і рядка підсумків — той самий шаблон). */
function shownByGrid(data: TableSliceDto, value: unknown): string {
  const found = gridColumns(data, false, NoLocalFlags, {}, noRequiredInput).find((c) => c.prop === 'C1');
  if (found === undefined || typeof found.cellTemplate !== 'function') throw new Error('Немає шаблону C1');

  return norm(String(found.cellTemplate((() => null) as never, { value } as never, undefined as never)));
}

beforeEach(() => setLanguage('en'));
afterEach(() => setLanguage(DefaultLanguage));

describe('Number format: масштаб сховища не доповнює нулями', () => {
  it.each([
    ['без масштабу', null],
    ['scale 16 (decimal(38,16), імпорт Land)', 16],
    ['scale 12 (вихід методології)', 12],
  ])('%s: «3.0000000000000000» → «3»', (_name, scale) => {
    expect(cellDisplay(Stored, column({ scale }))).toBe('3');
    expect(cellDisplay('1.000000000000', column({ dataType: 'Calculated', scale }))).toBe('1');
  });

  it('масштаб-формат (≤ MaxPaddedScale) лишається доповненням', () => {
    expect(MaxPaddedScale).toBe(6);
    expect(cellDisplay(Stored, column({ scale: 2 }))).toBe('3.00');
    expect(cellDisplay('1.5', column({ scale: 4 }))).toBe('1.5000');
    expect(cellDisplay('1.5', column({ scale: 6 }))).toBe('1.500000');
    // Довший за масштаб дріб не ріжеться (U-05).
    expect(cellDisplay('1.23456', column({ scale: 2 }))).toBe('1.23456');
  });

  it('від\'ємні, великі й крихітні значення — найкоротший точний запис', () => {
    const wide = column({ scale: 16 });

    expect(cellDisplay('-3.0000000000000000', wide)).toBe('-3');
    expect(norm(cellDisplay('1234567890123.5000000000000000', wide))).toBe('1,234,567,890,123.5');
    expect(cellDisplay('0.0000000000000001', wide)).toBe('0.0000000000000001');
    expect(cellDisplay('-0.0000000000000000', wide)).toBe('0');
  });

  it('displayPaddingOf: null, некоректне, сховище → 0; формат → сам масштаб', () => {
    expect(displayPaddingOf(null)).toBe(0);
    expect(displayPaddingOf(undefined)).toBe(0);
    expect(displayPaddingOf(-1)).toBe(0);
    expect(displayPaddingOf(2.5)).toBe(0);
    expect(displayPaddingOf(16)).toBe(0);
    expect(displayPaddingOf(7)).toBe(0);
    expect(displayPaddingOf(3)).toBe(3);
  });

  it('сітка й рядок підсумків: «3», не «3.0000000000000000»', () => {
    const data = slice({ C1: Stored }, [column({ scale: 16 })]);
    const row = totalsRow(data, columnTotals(data), 'Total', null);

    expect(shownByGrid(data, Stored)).toBe('3');
    expect(shownByGrid(data, row.C1)).toBe('3');
  });

  it('журнал аркуша й аудит: те саме правило', () => {
    expect(auditValueText(Stored, 'Decimal', 16)).toBe('3');
    expect(auditValueText('1.000000000000', 'Calculated', 12)).toBe('1');
    expect(auditValueText('12.5', 'Decimal', 2)).toBe('12.50');
  });

  it('редактор отримує те саме обрізане значення, без втрати знаків', () => {
    const wide = column({ scale: 16 });

    expect(editorValueOf(Stored, wide)).toBe('3');
    expect(editorValueOf('1234.1234567890123456', wide)).toBe('1234.1234567890123456');
  });
});

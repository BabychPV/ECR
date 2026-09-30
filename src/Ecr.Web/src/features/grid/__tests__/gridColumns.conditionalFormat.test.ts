import { describe, expect, it } from 'vitest';
import type { CellStyleDto, ColumnDto, TableSliceDto } from '@/api/types';
import { gridColumns } from '../DocumentGrid';
import { cellKey } from '../permissions';
import { NoLocalFlags, type LocalCellFlags } from '../cellState';
import { conditionalMatchOf, conditionalRulesOf, withConditionalRule } from '../conditionalAppearance';

/**
 * Умовне форматування на живій сітці (`ФВ-2.7`): правила версії з
 * `ColumnDto.conditionalFormats` підсвічують комірку за її значенням.
 *
 * ⛔ Мутаційний доказ (перевірено руками 2026-09-30):
 *  - у `cellProperties` брати `column.style` замість стилю з правилом —
 *    червоніють «заливка правила» і «жирність правила»;
 *  - у `withConditionalRule` порожній колір правила не лишає колір автора
 *    (`argbOfHex('')`) — червоніє «правило задає лише своє»;
 *  - прибрати `data-conditional-rule` — червоніє «перше правило перемагає».
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
    isRequiredByMethodology: false,
    displayFormat: null,
    defaultValue: null,
    lookupRegistryDefId: null,
    unitId: null,
    unitSymbol: null,
    style: null,
    ...overrides,
  };
}

const rules: NonNullable<ColumnDto['conditionalFormats']> = [
  { columnCode: 'C1', operator: 'gt', value: '100', valueTo: null, backgroundHex: '#ff0000', foregroundHex: null, isBold: false },
  { columnCode: 'C1', operator: 'gt', value: '50', valueTo: null, backgroundHex: null, foregroundHex: null, isBold: true },
];

function slice(cells: Record<string, unknown>, overrides: Partial<ColumnDto> = {}): TableSliceDto {
  return {
    tableInstanceId: 1,
    periodKey: 202609,
    columns: [column({ conditionalFormats: rules, ...overrides })],
    rows: [{ rowKey: 'R1', ordinal: 1, rowKind: 'Item', label: null, rowVersion: '0x01', cells, isOrphaned: false }],
    cellPermissions: {},
    cellConfirmations: {},
  };
}

const noRequiredInput = { blocked: new Map<string, string>(), warning: new Map<string, string>() };

function propsFor(value: unknown, flags: LocalCellFlags = NoLocalFlags, overrides: Partial<ColumnDto> = {}) {
  const columns = gridColumns(slice({ C1: value }, overrides), false, flags, {}, noRequiredInput);
  const found = columns.find((c) => c.prop === 'C1');
  if (found === undefined || typeof found.cellProperties !== 'function') throw new Error('Немає колонки C1');

  return found.cellProperties({ model: { __rowKey: 'R1', C1: value } } as never) ?? {};
}

describe('gridColumns — умовне форматування (ФВ-2.7)', () => {
  it('заливка правила: клас ecr-cell-filled і змінна --ecr-cell-fill', () => {
    const props = propsFor('150');

    expect(props['data-conditional-rule']).toBe('1');
    expect(props.class).toContain('ecr-cell-filled');
    expect((props.style as Record<string, string>)['--ecr-cell-fill']).toBe('#ff0000');
  });

  it('перше правило перемагає; друге спрацьовує лише там, де перше — ні', () => {
    expect(propsFor('150')['data-conditional-rule']).toBe('1');
    expect(propsFor('70')['data-conditional-rule']).toBe('2');
  });

  it('жирність правила — font-weight: bold', () => {
    expect((propsFor('70').style as Record<string, string>).fontWeight).toBe('bold');
  });

  it('жодне правило не спрацювало — ні атрибута, ні стилю', () => {
    const props = propsFor('10');

    expect(props).not.toHaveProperty('data-conditional-rule');
    expect(props).not.toHaveProperty('style');
  });

  it('5.0000000000 зі сховища і число 5 (`Int`) — те саме значення', () => {
    const exact = [{ ...rules[0]!, operator: 'eq', value: '5' }];

    expect(propsFor('5.0000000000', NoLocalFlags, { conditionalFormats: exact })['data-conditional-rule']).toBe('1');
    expect(propsFor(5, NoLocalFlags, { conditionalFormats: exact })['data-conditional-rule']).toBe('1');
    expect(propsFor(150)['data-conditional-rule']).toBe('1');
  });

  it('незбережена комірка лишається dirty і з правилом', () => {
    const dirty: LocalCellFlags = { dirty: new Set([cellKey('R1', 'C1')]), rounded: new Set() };
    const props = propsFor('150', dirty);

    expect(props['data-cell-state']).toBe('dirty');
    expect(props['data-conditional-rule']).toBe('1');
  });

  it('колонка без правил — як і була', () => {
    expect(propsFor('150', NoLocalFlags, { conditionalFormats: null })).not.toHaveProperty('data-conditional-rule');
  });
});

describe('conditionalAppearance', () => {
  const authored: CellStyleDto = {
    isBold: false,
    isItalic: true,
    foregroundArgb: (0xff112233 | 0) as number,
    backgroundArgb: null,
    horizontalAlign: 2,
    verticalAlign: null,
    wrapText: false,
  };

  it('правило задає лише своє: порожній колір лишає колір автора, решта стилю не змінюється', () => {
    const [first] = conditionalRulesOf({ conditionalFormats: rules });
    const merged = withConditionalRule(authored, first!);

    expect(merged.backgroundArgb).toBe((0xffff0000 | 0) as number);
    expect(merged.foregroundArgb).toBe(authored.foregroundArgb);
    expect(merged.isItalic).toBe(true);
    expect(merged.horizontalAlign).toBe(2);
  });

  it('порожня комірка: лише «порожньо» спрацьовує', () => {
    const empty = conditionalRulesOf({
      conditionalFormats: [{ ...rules[0]!, operator: 'empty', value: null }],
    });

    expect(conditionalMatchOf(empty, 'C1', null)?.position).toBe(1);
    expect(conditionalMatchOf(empty, 'C1', undefined)?.position).toBe(1);
    expect(conditionalMatchOf(empty, 'C1', '3')).toBeNull();
  });

  it('невідомий оператор із сервера пропускається, а не ламає сітку', () => {
    expect(conditionalRulesOf({ conditionalFormats: [{ ...rules[0]!, operator: 'contains' }] })).toHaveLength(0);
  });
});

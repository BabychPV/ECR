import { describe, expect, it } from 'vitest';
import type { CellStyleDto, ColumnDto, TableSliceDto } from '@/api/types';
import { gridColumns } from '../DocumentGrid';
import { cellKey } from '../permissions';
import { NoLocalFlags, type LocalCellFlags } from '../cellState';
import { withCellFormat } from '../conditionalAppearance';

/**
 * Умовне форматування на живій сітці (`ФВ-2.7`): результат правил, який
 * сервер віддає в `TableSliceDto.cellFormats`, фарбує свою комірку.
 *
 * ⛔ Мутаційний доказ (перевірено руками 2026-09-30):
 *  - у `cellProperties` брати `column.style` замість стилю з форматом —
 *    червоніють «заливка» і «жирність»;
 *  - у `withCellFormat` порожній колір формату затирає колір автора —
 *    червоніє «формат задає лише своє»;
 *  - шукати формат не за ключем комірки (`rowKey:columnCode`) — червоніє
 *    «лише своя комірка»;
 *  - прибрати `data-conditional-format` — червоніє «атрибут».
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

const red = { backgroundHex: '#ff0000', foregroundHex: null, isBold: false };
const bold = { backgroundHex: null, foregroundHex: null, isBold: true };

function slice(cellFormats: TableSliceDto['cellFormats']): TableSliceDto {
  return {
    tableInstanceId: 1,
    periodKey: 202609,
    columns: [column()],
    rows: [
      { rowKey: 'R1', ordinal: 1, rowKind: 'Item', label: null, rowVersion: '0x01', cells: { C1: '150' }, isOrphaned: false },
      { rowKey: 'R2', ordinal: 2, rowKind: 'Item', label: null, rowVersion: '0x01', cells: { C1: '10' }, isOrphaned: false },
    ],
    cellPermissions: {},
    cellConfirmations: {},
    ...(cellFormats === undefined ? {} : { cellFormats }),
  };
}

const noRequiredInput = { blocked: new Map<string, string>(), warning: new Map<string, string>() };

function propsFor(
  rowKey: string,
  cellFormats: TableSliceDto['cellFormats'],
  flags: LocalCellFlags = NoLocalFlags,
) {
  const columns = gridColumns(slice(cellFormats), false, flags, {}, noRequiredInput);
  const found = columns.find((c) => c.prop === 'C1');
  if (found === undefined || typeof found.cellProperties !== 'function') throw new Error('Немає колонки C1');

  return found.cellProperties({ model: { __rowKey: rowKey } } as never) ?? {};
}

describe('gridColumns — умовне форматування (ФВ-2.7)', () => {
  it('заливка формату: атрибут, клас ecr-cell-filled і змінна --ecr-cell-fill', () => {
    const props = propsFor('R1', { 'R1:C1': red });

    expect(props['data-conditional-format']).toBe('true');
    expect(props.class).toContain('ecr-cell-filled');
    expect((props.style as Record<string, string>)['--ecr-cell-fill']).toBe('#ff0000');
  });

  it('жирність формату — font-weight: bold', () => {
    expect((propsFor('R1', { 'R1:C1': bold }).style as Record<string, string>).fontWeight).toBe('bold');
  });

  it('лише своя комірка: формат іншого рядка цю не фарбує', () => {
    const props = propsFor('R2', { 'R1:C1': red });

    expect(props).not.toHaveProperty('data-conditional-format');
    expect(props).not.toHaveProperty('style');
  });

  it('зріз без cellFormats — як і був', () => {
    expect(propsFor('R1', null)).not.toHaveProperty('data-conditional-format');
    expect(propsFor('R1', undefined)).not.toHaveProperty('style');
  });

  it('незбережена комірка лишається dirty і з форматом', () => {
    const dirty: LocalCellFlags = { dirty: new Set([cellKey('R1', 'C1')]), rounded: new Set() };
    const props = propsFor('R1', { 'R1:C1': red }, dirty);

    expect(props['data-cell-state']).toBe('dirty');
    expect(props['data-conditional-format']).toBe('true');
  });
});

describe('withCellFormat', () => {
  const authored: CellStyleDto = {
    isBold: false,
    isItalic: true,
    foregroundArgb: (0xff112233 | 0) as number,
    backgroundArgb: null,
    horizontalAlign: 2,
    verticalAlign: null,
    wrapText: false,
  };

  it('формат задає лише своє: порожній колір лишає колір автора, решта стилю не змінюється', () => {
    const merged = withCellFormat(authored, red);

    expect(merged.backgroundArgb).toBe((0xffff0000 | 0) as number);
    expect(merged.foregroundArgb).toBe(authored.foregroundArgb);
    expect(merged.isItalic).toBe(true);
    expect(merged.horizontalAlign).toBe(2);
  });

  it('без стилю автора — лише те, що задав формат', () => {
    const merged = withCellFormat(null, { backgroundHex: null, foregroundHex: '#00ff00', isBold: true });

    expect(merged.foregroundArgb).toBe((0xff00ff00 | 0) as number);
    expect(merged.backgroundArgb).toBeNull();
    expect(merged.isBold).toBe(true);
  });
});

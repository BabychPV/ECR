import { describe, expect, it } from 'vitest';
import type { TableSliceDto } from '@/api/types';
import { rowTitle } from '@/features/documents/inspector/LockedRows';
import { addressChip } from '@/features/documents/inspector/inspectorModel';
import { calcErrorSourceText } from '../calcError';
import { lockedRowsOf } from '../rowAttention';
import { isOpaqueRowKey, rowKeyText } from '../rowKeyText';

/**
 * integ-row-label-messages (live doc 100, Issues «ROWS THAT NEED ATTENTION»): a row added by a person has a 32-hex
 * key; it must never reach the text. «Row N (name)» / «N» instead; short template codes stay as they are.
 *
 * ⛔ Red without the fix: `rowTitle` returned «adc95dc2bdb84fa0bce2d80e0defc581 (B12 diesel generator DG-1)».
 */

const Guid = 'adc95dc2bdb84fa0bce2d80e0defc581';
const Hex32 = /[0-9a-f]{32}/i;

const Slice = {
  tableInstanceId: 7,
  periodKey: 202601,
  columns: [
    { code: 'Land_DescriptionEquipment', header: 'Description of equipment', dataType: 'String', lookupRegistryDefId: null },
    { code: 'RH_1', header: 'Running hours', dataType: 'Decimal', lookupRegistryDefId: null },
  ],
  rows: [
    { rowKey: 'LTD-A01', label: 'Template', cells: {}, isOrphaned: false, ordinal: 1, rowKind: 'Item', rowVersion: 'AA' },
    {
      rowKey: Guid,
      label: null,
      cells: { Land_DescriptionEquipment: 'B12 diesel generator DG-1' },
      isOrphaned: false,
      ordinal: 2,
      rowKind: 'Dynamic',
      rowVersion: 'AB',
    },
    { rowKey: 'f'.repeat(32), label: null, cells: {}, isOrphaned: false, ordinal: 3, rowKind: 'Dynamic', rowVersion: 'AC' },
  ],
  cellPermissions: { [`${Guid}:RH_1`]: 'PeriodClosed', [`${'f'.repeat(32)}:RH_1`]: 'PeriodClosed' },
  cellConfirmations: {},
} as unknown as TableSliceDto;

describe('row keys in human text', () => {
  it('recognises service keys and leaves template codes alone', () => {
    expect(isOpaqueRowKey(Guid)).toBe(true);
    expect(isOpaqueRowKey('LTD-A01')).toBe(false);
    expect(rowKeyText('LTD-A01', 5)).toBe('LTD-A01');
    expect(rowKeyText(Guid, 3)).not.toMatch(Hex32);
  });

  it('locked rows: «N (name)» for a named dynamic row, «N» for an unnamed one — never the key', () => {
    const items = lockedRowsOf(Slice);
    const titles = items.map((item) => rowTitle(item));

    expect(titles).toEqual(['2 (B12 diesel generator DG-1)', '3']);
    for (const title of titles) expect(title).not.toMatch(Hex32);
  });

  it('a code-keyed row keeps «code (name)»', () => {
    expect(rowTitle({ rowKey: 'LTD-A01', rowName: 'Gasoline blow torch', rowOrdinal: 1 })).toBe('LTD-A01 (Gasoline blow torch)');
  });

  it('attention rows and chips carry no key', () => {
    expect(addressChip(Guid, 'C1')).not.toMatch(Hex32);
    expect(addressChip('LTD-A01', 'C1')).toBe('LTD-A01 · C1');
    // Адреса інспектора: службовий ключ → підпис рядка з сітки, кодовий ключ підписом не підміняється.
    expect(addressChip(Guid, 'C1', 'Row 3')).toBe('Row 3 · C1');
    expect(addressChip(Guid, 'C1', 'row 3')).toBe('Row 3 · C1');
    expect(addressChip('LTD-A01', 'C1', 'Row 3')).toBe('LTD-A01 · C1');
    expect(calcErrorSourceText({ tableCode: 'T', columnCode: 'C', rowKey: Guid } as never)).toBe('T · C');
  });
});

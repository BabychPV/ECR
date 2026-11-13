import { describe, expect, it } from 'vitest';
import type { TableSliceDto } from '@/api/types';
import { lockedRowsOf, tableAttentionOf } from '../rowAttention';

/**
 * B51 (3), F13 RC #849: a locked row of a dynamic table (Land T3_00) has no template label, so Issues showed only the
 * row code. The name now falls back to a descriptive column of the row: «Description of equipment», then «Type of
 * equipment» (a lookup — its record name), then nothing (the sentence keeps the row code).
 *
 * ⛔ Red without the fix: `rowName` was the template label only — `null` for every dynamic row.
 */

const Slice = {
  tableInstanceId: 960,
  periodKey: 202601,
  columns: [
    { code: 'Land_Status', header: 'Status', dataType: 'Lookup', lookupRegistryDefId: 4 },
    { code: 'Land_TypeEquipment', header: 'Type of equipment', dataType: 'Lookup', lookupRegistryDefId: 5 },
    { code: 'Land_DescriptionEquipment', header: 'Description of equipment', dataType: 'String', lookupRegistryDefId: null },
    { code: 'RH_1', header: 'Running hours per month (hour)', dataType: 'Decimal', lookupRegistryDefId: null },
  ],
  rows: [
    {
      rowKey: 'LTD-A01',
      label: null,
      cells: { Land_Status: 1, Land_TypeEquipment: 7, Land_DescriptionEquipment: ' Gasoline blow torch ' },
      isOrphaned: false,
      ordinal: 1,
      rowKind: 'Dynamic',
      rowVersion: 'AA',
    },
    {
      rowKey: 'LTD-A02',
      label: null,
      cells: { Land_Status: 1, Land_TypeEquipment: 7, Land_DescriptionEquipment: '' },
      isOrphaned: false,
      ordinal: 2,
      rowKind: 'Dynamic',
      rowVersion: 'AB',
    },
    {
      rowKey: 'LTD-A03',
      label: null,
      cells: { Land_Status: 1, Land_DescriptionEquipment: null },
      isOrphaned: false,
      ordinal: 3,
      rowKind: 'Dynamic',
      rowVersion: 'AC',
    },
    {
      rowKey: 'LTD-A04',
      label: 'Template label',
      cells: { Land_DescriptionEquipment: 'Ignored' },
      isOrphaned: false,
      ordinal: 4,
      rowKind: 'Item',
      rowVersion: 'AD',
    },
  ],
  cellPermissions: {
    'LTD-A01:RH_1': 'PeriodClosed',
    'LTD-A02:RH_1': 'PeriodClosed',
    'LTD-A03:RH_1': 'PeriodClosed',
    'LTD-A04:RH_1': 'PeriodClosed',
  },
  cellConfirmations: {},
} as unknown as TableSliceDto;

const lookupLabel = (registryId: number, id: number): string | undefined =>
  registryId === 5 && id === 7 ? 'Compressor' : registryId === 4 && id === 1 ? 'Running' : undefined;

describe('B51 (3): locked row name falls back to a descriptive column', () => {
  it('label → Description → Type (lookup name) → none', () => {
    const names = lockedRowsOf(Slice, undefined, lookupLabel).map((row) => [row.rowKey, row.rowName]);

    expect(names).toEqual([
      ['LTD-A01', 'Gasoline blow torch'],
      ['LTD-A02', 'Compressor'],
      ['LTD-A03', null],
      ['LTD-A04', 'Template label'],
    ]);
  });

  it('without a lookup resolver the text column still names the row; the lookup id is never shown', () => {
    const names = lockedRowsOf(Slice).map((row) => row.rowName);

    expect(names).toEqual(['Gasoline blow torch', null, null, 'Template label']);
  });

  it('tableAttentionOf passes the resolver through to the locked rows', () => {
    const state = tableAttentionOf(Slice, undefined, undefined, 300, undefined, lookupLabel);

    expect(state.lockedRows?.[1]?.rowName).toBe('Compressor');
  });
});

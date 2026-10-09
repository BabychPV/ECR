import { afterEach, describe, expect, it } from 'vitest';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { captureEdit, revertsToSaved } from '../edits';
import { sameColumnValue } from '../cellValue';
import { discardPendingRows, pendingSlice, putPendingEdit, resetPending } from '../pendingStore';
import type { PendingEdit } from '../useCellPatch';

/**
 * `G1-06`: у текстовій колонці рядки, «рівні як числа» (`0012`/`12`,
 * `1.10`/`1.1`, `5`/`5.0`), — РІЗНІ значення. Доти `sameCellValue` зводив їх до
 * десяткового канону, і `captureEdit` повертав `null`: правки не було ні у
 * сховищі, ні в PATCH.
 */
function column(dataType: string): ColumnDto {
  return {
    id: 1,
    code: 'C1',
    header: 'Код',
    dataType,
    ordinal: 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    displayFormat: null,
    defaultValue: null,
    lookupRegistryDefId: null,
    unitId: null,
    unitSymbol: null,
  } as ColumnDto;
}

function slice(dataType: string, saved: unknown): TableSliceDto {
  return {
    tableInstanceId: 1,
    periodKey: 202603,
    columns: [column(dataType)],
    rows: [
      {
        rowKey: 'R1',
        ordinal: 1,
        rowKind: 'Static',
        label: null,
        rowVersion: 'v1',
        cells: { C1: saved },
        isOrphaned: false,
      },
    ],
    cellPermissions: {},
    cellConfirmations: {},
  };
}

afterEach(() => {
  resetPending();
});

describe('Текстова колонка: рівність дослівна (G1-06)', () => {
  it.each([
    ['0012', '12'],
    ['12', '0012'],
    ['1.10', '1.1'],
    ['5', '5.0'],
    ['+5', '5'],
    ['01001', '1001'],
  ])('збережено %s, введено %s — це правка', (saved, raw) => {
    const captured = captureEdit(slice('String', saved), { columnCode: 'C1', rowKey: 'R1', raw });

    expect(captured).not.toBeNull();
    expect(captured?.pending.value).toBe(raw);
    expect(revertsToSaved(slice('String', saved), { columnCode: 'C1', rowKey: 'R1', raw })).toBe(false);
  });

  it('той самий текст — не правка', () => {
    expect(captureEdit(slice('String', '0012'), { columnCode: 'C1', rowKey: 'R1', raw: '0012' })).toBeNull();
  });

  it('регресія e470777a: Decimal `5.0000` і `5` — одне число, не правка', () => {
    expect(captureEdit(slice('Decimal', '5.0000'), { columnCode: 'C1', rowKey: 'R1', raw: '5' })).toBeNull();
  });

  it('sameColumnValue: дата — за датою, текст — дослівно, число — за значенням', () => {
    expect(sameColumnValue('Date', '2026-09-15', '2026-09-15T00:00:00')).toBe(true);
    expect(sameColumnValue('String', '1.10', '1.1')).toBe(false);
    expect(sameColumnValue('Decimal', '1.10', '1.1')).toBe(true);
    expect(sameColumnValue(undefined, '1.10', '1.1')).toBe(true);
  });

  it('новіша текстова правка `5.0` поверх надісланого `5` не знімається відповіддю', () => {
    const flying = captureEdit(slice('String', 'x'), { columnCode: 'C1', rowKey: 'R1', raw: '5' });
    const newer = captureEdit(slice('String', 'x'), { columnCode: 'C1', rowKey: 'R1', raw: '5.0' });
    if (flying === null || newer === null) throw new Error('правки мають бути захоплені');

    putPendingEdit(1, 202603, newer.pending);
    const sent = new Map<string, PendingEdit>([['R1:C1', flying.pending]]);
    discardPendingRows(1, 202603, ['R1'], sent);

    expect(pendingSlice(1, 202603).get('R1:C1')?.value).toBe('5.0');
  });
});

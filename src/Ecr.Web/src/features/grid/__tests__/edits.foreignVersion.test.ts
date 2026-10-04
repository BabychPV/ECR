import { afterEach, describe, expect, it } from 'vitest';
import type { TableSliceDto } from '@/api/types';
import { captureEdit, withKnownVersions } from '../edits';
import { discardPendingRows, pendingSlice, putPendingEdit, resetPending } from '../pendingStore';
import type { PendingEdit } from '../useCellPatch';

/**
 * AN-39 / L8-20: утримана (відхилена) правка довго чекає повтору, а
 * `withKnownVersions` підтягувала до неї НОВУ версію рядка з кешу. Якщо за цей час
 * хтось інший змінив ту саму комірку, а екран побачив це перезапитом зрізу,
 * повтор ішов із версією, під якою вже лежить чуже значення: тихий перезапис без 409.
 */

function sliceWith(rowVersion: string, c1: unknown, c2: unknown = 1): TableSliceDto {
  return {
    tableInstanceId: 4,
    periodKey: 202609,
    columns: [
      { id: 1, code: 'C1', header: 'C1', dataType: 'Decimal', ordinal: 1, isReadOnly: false, isRequired: false, isRequiredByMethodology: false, displayFormat: null, defaultValue: null, lookupRegistryDefId: null, unitId: null, unitSymbol: null },
      { id: 2, code: 'C2', header: 'C2', dataType: 'Decimal', ordinal: 2, isReadOnly: false, isRequired: false, isRequiredByMethodology: false, displayFormat: null, defaultValue: null, lookupRegistryDefId: null, unitId: null, unitSymbol: null },
    ],
    rows: [
      { rowKey: 'R1', ordinal: 1, rowKind: 'Static', label: null, rowVersion, cells: { C1: c1, C2: c2 }, isOrphaned: false },
    ],
    cellPermissions: {},
    cellConfirmations: {},
  };
}

/** Правка C1 «abc»-подібна: набрана над значенням 10 при версії v1. */
function heldEdit(): PendingEdit {
  const captured = captureEdit(sliceWith('v1', 10), { columnCode: 'C1', rowKey: 'R1', raw: '12' });
  if (captured === null) throw new Error('правку не захоплено');

  return captured.pending;
}

afterEach(() => {
  resetPending();
});

describe('L8-20: версія рядка для утриманої правки', () => {
  it('captureEdit запам`ятовує значення, яке людина бачила', () => {
    expect(heldEdit().before).toBe(10);
  });

  it('чужа зміна ТІЄЇ САМОЇ комірки в кеші: версія лишається старою (сервер дасть 409)', () => {
    const [edit] = withKnownVersions([heldEdit()], sliceWith('v2', 99));

    expect(edit?.baseVersion).toBe('v1');
  });

  it('у рядку змінилась інша комірка, ця - та сама: версія з кешу (B-09 не повертається)', () => {
    const [edit] = withKnownVersions([heldEdit()], sliceWith('v2', 10, 7));

    expect(edit?.baseVersion).toBe('v2');
  });

  it('явний вибір «Keep mine» (overrides) сильніший за захист', () => {
    const [edit] = withKnownVersions([heldEdit()], sliceWith('v2', 99), new Map([['R1', 'v3']]));

    expect(edit?.baseVersion).toBe('v3');
  });

  it('усі правки рядка тримають стару версію, якщо хоч одна бачила інше значення', () => {
    const other = captureEdit(sliceWith('v1', 10), { columnCode: 'C2', rowKey: 'R1', raw: '5' })?.pending;
    if (other === undefined) throw new Error('правку не захоплено');

    const edits = withKnownVersions([other, heldEdit()], sliceWith('v2', 99));

    expect(edits.map((edit) => edit.baseVersion)).toEqual(['v1', 'v1']);
  });

  it('власне щойно збережене значення - не чуже: новіша правка тієї ж комірки перебазовується', () => {
    // Набрали 12 (летить), потім 13 поверх - обидві бачили 10.
    const first = heldEdit();
    const second = { ...first, value: '13' };
    putPendingEdit(4, 202609, second);

    // Сервер прийняв 12, кеш тепер v2 / 12; 13 лишається незбереженою.
    discardPendingRows(4, 202609, ['R1'], new Map([['R1:C1', first]]));
    const kept = pendingSlice(4, 202609).get('R1:C1');

    expect(kept?.before).toBe('12');
    expect(withKnownVersions(kept === undefined ? [] : [kept], sliceWith('v2', 12))[0]?.baseVersion).toBe('v2');
  });

  // Рев'ю AN-39b P3-1: дата після перезапиту приходить у формі сервера - це не чужа зміна.
  it('Date: власне збережене в формі сервера (T00:00:00) не вважається чужим', () => {
    const dated: TableSliceDto = {
      ...sliceWith('v3', null),
      columns: [{ ...sliceWith('v1', null).columns[0]!, dataType: 'Date' }],
      rows: [{ rowKey: 'R1', ordinal: 1, rowKind: 'Static', label: null, rowVersion: 'v3', cells: { C1: '2026-09-15T00:00:00' }, isOrphaned: false }],
    };
    const edit: PendingEdit = { rowKey: 'R1', columnCode: 'C1', value: '2026-09-20', isEmpty: false, baseVersion: 'v1', before: '2026-09-15' };

    expect(withKnownVersions([edit], dated)[0]?.baseVersion).toBe('v3');
    // Контроль: інша дата - чужа зміна, версія стара.
    expect(withKnownVersions([{ ...edit, before: '2026-09-14' }], dated)[0]?.baseVersion).toBe('v1');
  });
});

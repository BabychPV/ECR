import { describe, expect, it } from 'vitest';
import type { RowWindowMap } from '../rowWindowApi';
import {
  emptyForm,
  formFromMap,
  newSourceRow,
  toCreateRequest,
  toggleActiveRequest,
  toUpdateRequest,
  validateForm,
  type RowWindowFormState,
} from '../rowWindowForm';
import type { MapColumn } from '../sourceEventMapForm';

/**
 * Модель форми прив'язки вікна рядка (HSE301 A1): перевірки до надсилання збігаються з відмовами сервера
 * (`windowColumnsNotDate`, `targetNotDecimal`, `rowWindowSelectorWithoutColumn`, `rowWindowSelectorTaken`,
 * `rowWindowPolicyOutOfRange`), а тіло `POST`/`PUT` несе рівно те, що в формі.
 *
 * Мутація: прибрати в `validateForm` перевірку `duplicateSelector` — червоніє «два джерела з тим самим селектором».
 */

const columns: MapColumn[] = [
  { id: 1, code: 'Start', header: 'Start', dataType: 'Date', lookupRegistryDefId: null, unitId: null },
  { id: 2, code: 'End', header: 'End', dataType: 'Date', lookupRegistryDefId: null, unitId: null },
  { id: 3, code: 'Volume', header: 'Volume', dataType: 'Decimal', lookupRegistryDefId: null, unitId: null },
  { id: 4, code: 'Key', header: 'Key', dataType: 'String', lookupRegistryDefId: null, unitId: null },
];

function valid(patch: Partial<RowWindowFormState> = {}): RowWindowFormState {
  return {
    ...emptyForm(),
    documentId: 100,
    tableDefId: 200,
    targetColumnDefId: 3,
    startColumnDefId: 1,
    endColumnDefId: 2,
    targetUnitId: 9,
    ...patch,
  };
}

const Stored: RowWindowMap = {
  id: 5,
  tableDefId: 200,
  targetColumnDefId: 3,
  targetColumnCode: 'Volume',
  startColumnDefId: 1,
  startColumnCode: 'Start',
  endColumnDefId: 2,
  endColumnCode: 'End',
  selectorColumnDefId: 4,
  selectorColumnCode: 'Key',
  summary: 'Total',
  isStep: false,
  maxGapSeconds: null,
  targetUnitId: 9,
  minPercentGood: '95.00',
  refetchWithinDays: 7,
  isActive: true,
  rowVersion: '00000000000007D1',
  sources: [{ id: 1, selectorValue: 'A', sourceEntityId: 42, sourceField: 'Flare.Total', sourceUnitId: 8 }],
};

describe('features/sources/rowWindowForm', () => {
  it('порожня форма названа всіма відомими наперед проблемами', () => {
    expect(validateForm(emptyForm(), columns)).toEqual(['documentRequired', 'tableRequired', 'columnsRequired', 'unitRequired']);
    expect(validateForm(valid(), columns)).toEqual([]);
  });

  it('вікно не з Date, ціль не Decimal, початок = кінець', () => {
    expect(validateForm(valid({ startColumnDefId: 3 }), columns)).toEqual(['windowNotDate']);
    expect(validateForm(valid({ targetColumnDefId: 4 }), columns)).toEqual(['targetNotDecimal']);
    expect(validateForm(valid({ endColumnDefId: 1 }), columns)).toEqual(['windowSame']);
  });

  it('пороги: покриття 0–100 з двома знаками, доби 0–366, прогалина — додатне ціле', () => {
    expect(validateForm(valid({ minPercentGood: '80.5', refetchWithinDays: '0', maxGapSeconds: '600' }), columns)).toEqual([]);

    for (const bad of [{ minPercentGood: '101' }, { minPercentGood: '1.234' }, { refetchWithinDays: '367' }, { maxGapSeconds: '0' }, { maxGapSeconds: '-5' }]) {
      expect(validateForm(valid(bad), columns)).toEqual(['policyInvalid']);
    }
  });

  it('джерела: неповне, значення селектора без колонки, два джерела з тим самим селектором (без урахування регістру)', () => {
    const full = { sourceEntityId: 42, sourceField: 'Flare.Total', sourceUnitId: 8 };

    expect(validateForm(valid({ sources: [newSourceRow({ sourceEntityId: 42 })] }), columns)).toEqual(['sourceIncomplete']);
    expect(validateForm(valid({ sources: [newSourceRow({ ...full, selectorValue: 'A' })] }), columns)).toEqual(['selectorWithoutColumn']);

    const twins = [newSourceRow({ ...full, selectorValue: 'A' }), newSourceRow({ ...full, selectorValue: ' a ' })];
    expect(validateForm(valid({ selectorColumnDefId: 4, sources: twins }), columns)).toEqual(['duplicateSelector']);

    // Два джерела «для всіх рядків» — теж дубль (NULL у базі рівні).
    expect(validateForm(valid({ sources: [newSourceRow(full), newSourceRow(full)] }), columns)).toEqual(['duplicateSelector']);
  });

  it('POST: порожні пороги — null (типові значення сервера), порожній селектор джерела — null', () => {
    const request = toCreateRequest(
      valid({ selectorColumnDefId: 4, sources: [newSourceRow({ selectorValue: '  ', sourceEntityId: 42, sourceField: ' Flare.Total ', sourceUnitId: 8 })] }),
    );

    expect(request).toEqual({
      tableDefId: 200,
      targetColumnDefId: 3,
      startColumnDefId: 1,
      endColumnDefId: 2,
      selectorColumnDefId: 4,
      summary: 'Total',
      isStep: false,
      targetUnitId: 9,
      minPercentGood: null,
      refetchWithinDays: null,
      maxGapSeconds: null,
      sources: [{ selectorValue: null, sourceEntityId: 42, sourceField: 'Flare.Total', sourceUnitId: 8 }],
    });
  });

  it('PUT: форма наявної прив\'язки повертається без змін разом із rowVersion, ключ (таблиця, ціль) не йде', () => {
    const state = { ...formFromMap(Stored), documentId: 100 };

    const request = toUpdateRequest(state, Stored);

    expect(request).toEqual({
      startColumnDefId: 1,
      endColumnDefId: 2,
      selectorColumnDefId: 4,
      summary: 'Total',
      isStep: false,
      targetUnitId: 9,
      minPercentGood: '95.00',
      refetchWithinDays: 7,
      maxGapSeconds: null,
      isActive: true,
      rowVersion: '00000000000007D1',
      sources: [{ selectorValue: 'A', sourceEntityId: 42, sourceField: 'Flare.Total', sourceUnitId: 8 }],
    });
    expect(request).not.toHaveProperty('tableDefId');
    expect(request).not.toHaveProperty('targetColumnDefId');
  });

  it('пауза: лише isActive навпаки, усе інше й rowVersion — як у прив\'язці', () => {
    const paused = toggleActiveRequest(Stored);

    expect(paused).toMatchObject({ isActive: false, rowVersion: '00000000000007D1', summary: 'Total' });
    expect(paused.sources).toEqual([{ selectorValue: 'A', sourceEntityId: 42, sourceField: 'Flare.Total', sourceUnitId: 8 }]);
    expect(toggleActiveRequest({ ...Stored, isActive: false }).isActive).toBe(true);
  });
});

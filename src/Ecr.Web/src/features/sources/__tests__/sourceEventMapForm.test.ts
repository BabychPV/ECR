import { describe, expect, it } from 'vitest';
import {
  emptyForm,
  EndAttribute,
  formFromMap,
  newFieldRow,
  StartAttribute,
  toCreateRequest,
  toggleActiveRequest,
  toUpdateRequest,
  validateForm,
  valueKindsFor,
  type MapColumn,
  type MapFormState,
} from '../sourceEventMapForm';
import type { SourceEventMap } from '../sourceEventsApi';

/**
 * Модель форми мапінгу подій (HSE301 A6-UI): перевірки до надсилання дзеркалять правила обробника (§4.7.3), а
 * тіла `POST`/`PUT` — рівно ті поля, які читає сервер.
 */

const Columns: MapColumn[] = [
  { id: 1, code: 'Start', header: 'Start', dataType: 'Date', lookupRegistryDefId: null, unitId: null },
  { id: 2, code: 'End', header: 'End', dataType: 'Date', lookupRegistryDefId: null, unitId: null },
  { id: 3, code: 'Category', header: 'Category', dataType: 'Lookup', lookupRegistryDefId: 70, unitId: null },
  { id: 4, code: 'Volume_Sm3', header: 'Volume', dataType: 'Decimal', lookupRegistryDefId: null, unitId: 9 },
  { id: 5, code: 'Comment', header: 'Comment', dataType: 'String', lookupRegistryDefId: null, unitId: null },
];

/** Мінімальна правильна форма: документ, таблиця, `$start`/`$end` на Date-колонки. */
function valid(patch: Partial<MapFormState> = {}): MapFormState {
  return {
    ...emptyForm(),
    documentId: 100,
    tableDefId: 200,
    fields: [
      newFieldRow({ sourceAttribute: StartAttribute, targetColumnDefId: 1 }),
      newFieldRow({ sourceAttribute: EndAttribute, targetColumnDefId: 2 }),
    ],
    ...patch,
  };
}

const Stored: SourceEventMap = {
  id: 12,
  sourceEntityId: 42,
  documentId: 100,
  tableDefId: 200,
  volumeMode: 'EventAttribute',
  filterAttribute: 'Area',
  filterScope: 'PrimaryElement',
  filterValue: 'Island A',
  isActive: true,
  rowVersion: '00000000000007D1',
  fields: [
    {
      id: 1,
      targetColumnDefId: 1,
      sourceAttribute: '$start',
      attributeScope: 'Event',
      valueKind: 'Direct',
      sourceUnitId: null,
      targetUnitId: null,
      values: [],
    },
    {
      id: 2,
      targetColumnDefId: 3,
      sourceAttribute: 'TUGF_Category',
      attributeScope: 'Event',
      valueKind: 'ValueMap',
      sourceUnitId: null,
      targetUnitId: null,
      values: [{ id: 5, sourceValue: 'V6', registryEntryId: 700 }],
    },
  ],
};

describe('validateForm', () => {
  it('нова форма: без документа, таблиці й колонок для $start/$end — зберегти не можна', () => {
    expect(validateForm(emptyForm(), Columns)).toEqual(['documentRequired', 'tableRequired', 'startEndRequired', 'fieldIncomplete']);
  });

  it('документ, таблиця і $start/$end на Date-колонках — проблем немає', () => {
    expect(validateForm(valid(), Columns)).toEqual([]);
  });

  it('$start на не-Date колонці — startEndNotDate', () => {
    const state = valid({
      fields: [
        newFieldRow({ sourceAttribute: StartAttribute, targetColumnDefId: 5 }),
        newFieldRow({ sourceAttribute: EndAttribute, targetColumnDefId: 2 }),
      ],
    });

    expect(validateForm(state, Columns)).toEqual(['startEndNotDate']);
  });

  it('без $end — startEndRequired', () => {
    const state = valid({ fields: [newFieldRow({ sourceAttribute: StartAttribute, targetColumnDefId: 1 })] });

    expect(validateForm(state, Columns)).toContain('startEndRequired');
  });

  it('одна колонка двічі — duplicateColumn', () => {
    const state = valid({
      fields: [...valid().fields, newFieldRow({ sourceAttribute: 'Comment', targetColumnDefId: 1 })],
    });

    expect(validateForm(state, Columns)).toContain('duplicateColumn');
  });

  it('пошук за кодом на не-Lookup колонці — valueKindMismatch', () => {
    const state = valid({
      fields: [...valid().fields, newFieldRow({ sourceAttribute: 'Note', targetColumnDefId: 5, valueKind: 'LookupByCode' })],
    });

    expect(validateForm(state, Columns)).toEqual(['valueKindMismatch']);
  });

  it('таблиця відповідностей без пар чи з незаповненою парою — valueMapIncomplete', () => {
    const empty = valid({
      fields: [...valid().fields, newFieldRow({ sourceAttribute: 'Cat', targetColumnDefId: 3, valueKind: 'ValueMap' })],
    });
    const half = valid({
      fields: [
        ...valid().fields,
        newFieldRow({
          sourceAttribute: 'Cat',
          targetColumnDefId: 3,
          valueKind: 'ValueMap',
          values: [{ sourceValue: 'V6', registryEntryId: null }],
        }),
      ],
    });

    expect(validateForm(empty, Columns)).toEqual(['valueMapIncomplete']);
    expect(validateForm(half, Columns)).toEqual(['valueMapIncomplete']);
  });

  it('звуження — три значення разом або жодного', () => {
    expect(validateForm(valid({ filterAttribute: 'Area', filterScope: 'Event' }), Columns)).toEqual(['filterIncomplete']);
    expect(validateForm(valid({ filterValue: 'Island A' }), Columns)).toEqual(['filterIncomplete']);
    expect(
      validateForm(valid({ filterAttribute: 'Area', filterScope: 'Event', filterValue: 'Island A' }), Columns),
    ).toEqual([]);
  });
});

describe('valueKindsFor', () => {
  it('Lookup-колонка — усі чотири способи; решта — лише «як є»', () => {
    expect(valueKindsFor(Columns[2])).toEqual(['Direct', 'LookupByCode', 'LookupByName', 'ValueMap']);
    expect(valueKindsFor(Columns[3])).toEqual(['Direct']);
    expect(valueKindsFor(undefined)).toEqual(['Direct']);
  });
});

describe('тіла запитів', () => {
  it('POST: сутність, документ, таблиця, поля; звуження без значення — три null; values лише для ValueMap', () => {
    const state = valid({
      volumeMode: 'RowWindow',
      filterAttribute: 'Area',
      filterScope: 'Event',
      filterValue: '   ',
      fields: [
        ...valid().fields,
        newFieldRow({ sourceAttribute: 'Volume', targetColumnDefId: 4, sourceUnitId: 8, targetUnitId: 9 }),
      ],
    });

    expect(toCreateRequest(42, state)).toEqual({
      sourceEntityId: 42,
      documentId: 100,
      tableDefId: 200,
      volumeMode: 'RowWindow',
      filterAttribute: null,
      filterScope: null,
      filterValue: null,
      fields: [
        { targetColumnDefId: 1, sourceAttribute: '$start', attributeScope: 'Event', valueKind: 'Direct', sourceUnitId: null, targetUnitId: null, values: null },
        { targetColumnDefId: 2, sourceAttribute: '$end', attributeScope: 'Event', valueKind: 'Direct', sourceUnitId: null, targetUnitId: null, values: null },
        { targetColumnDefId: 4, sourceAttribute: 'Volume', attributeScope: 'Event', valueKind: 'Direct', sourceUnitId: 8, targetUnitId: 9, values: null },
      ],
    });
  });

  it('зарезервований атрибут завжди йде з областю Event, навіть якщо в рядку стоїть інша', () => {
    const state = valid({
      fields: [
        newFieldRow({ sourceAttribute: StartAttribute, targetColumnDefId: 1, attributeScope: 'PrimaryElement' }),
        newFieldRow({ sourceAttribute: EndAttribute, targetColumnDefId: 2 }),
      ],
    });

    expect(toCreateRequest(42, state).fields[0]?.attributeScope).toBe('Event');
  });

  it('PUT з мапінгу, що є, повертає його ж поля, звуження й відповідності без змін', () => {
    expect(toUpdateRequest(formFromMap(Stored))).toEqual({
      volumeMode: 'EventAttribute',
      isActive: true,
      rowVersion: '00000000000007D1',
      filterAttribute: 'Area',
      filterScope: 'PrimaryElement',
      filterValue: 'Island A',
      fields: [
        { targetColumnDefId: 1, sourceAttribute: '$start', attributeScope: 'Event', valueKind: 'Direct', sourceUnitId: null, targetUnitId: null, values: null },
        {
          targetColumnDefId: 3,
          sourceAttribute: 'TUGF_Category',
          attributeScope: 'Event',
          valueKind: 'ValueMap',
          sourceUnitId: null,
          targetUnitId: null,
          values: [{ sourceValue: 'V6', registryEntryId: 700 }],
        },
      ],
    });
  });

  it('пауза змінює лише isActive — решта тіла та сама', () => {
    const paused = toggleActiveRequest(Stored);

    expect(paused.isActive).toBe(false);
    expect({ ...paused, isActive: true }).toEqual(toUpdateRequest(formFromMap(Stored)));
    expect(toggleActiveRequest({ ...Stored, isActive: false }).isActive).toBe(true);
  });

  // AN-40 / L9-06: пауза з рядка переліку несе версію цього рядка — застарілий кеш дає 409, а не мовчки відкочує
  // чужу правку полів. Новий мапінг версії не має.
  it('пауза й PUT несуть rowVersion мапінгу, новий мапінг — null', () => {
    expect(toggleActiveRequest(Stored).rowVersion).toBe('00000000000007D1');
    expect(toUpdateRequest(formFromMap({ ...Stored, rowVersion: 'ABCD' })).rowVersion).toBe('ABCD');
    expect(emptyForm().rowVersion).toBeNull();
  });
});

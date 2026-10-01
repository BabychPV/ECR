import type { components } from '@/api/schema';
import type {
  CreateSourceEventMapRequest,
  SourceEventFieldInput,
  SourceEventMap,
  UpdateSourceEventMapRequest,
} from './sourceEventsApi';

/**
 * Модель форми мапінгу подій (HSE301 A6, FEATURE-HSE301-VIEW §4.7.3, §10.6) — чиста логіка без інтерфейсу:
 * стан форми, перевірки до надсилання і збирання тіла `POST`/`PUT`.
 *
 * ⛔ Перевірки тут — підказка ДО надсилання, а не заміна серверних: сервер лишається остаточним
 * (`err.ECR-INT-0422.eventMapStartEndRequired`, `.eventMapValueKindMismatch` …), форма лише не дає натиснути
 * «Зберегти», коли відмова відома наперед.
 */

export type AttributeScope = components['schemas']['SourceEventAttributeScope'];
export type ValueKind = components['schemas']['SourceEventValueKind'];
export type VolumeMode = components['schemas']['SourceEventVolumeMode'];

/** Зарезервовані атрибути: час і назва самої події (§4.7.3). */
export const StartAttribute = '$start';
export const EndAttribute = '$end';
export const NameAttribute = '$name';
export const ReservedAttributes = [StartAttribute, EndAttribute, NameAttribute] as const;

export const VolumeModes = ['None', 'EventAttribute', 'RowWindow'] as const satisfies readonly VolumeMode[];
export const ValueKinds = ['Direct', 'LookupByCode', 'LookupByName', 'ValueMap'] as const satisfies readonly ValueKind[];

/** Колонка динамічної таблиці — лише те, що форма читає з `ColumnDto`. */
export interface MapColumn {
  readonly id: number;
  readonly code: string;
  readonly header: string;
  readonly dataType: string;
  readonly lookupRegistryDefId: number | null;
  readonly unitId: number | null;
}

/** Пара «значення PI → запис довідника»; `registryEntryId = null` — ще не обрано. */
export interface ValuePair {
  readonly sourceValue: string;
  readonly registryEntryId: number | null;
}

/** Рядок сітки «колонка ↔ атрибут». `key` — лише для React, на сервер не йде. */
export interface FieldRow {
  readonly key: string;
  readonly targetColumnDefId: number | null;
  readonly sourceAttribute: string;
  readonly attributeScope: AttributeScope;
  readonly valueKind: ValueKind;
  readonly sourceUnitId: number | null;
  readonly targetUnitId: number | null;
  readonly values: readonly ValuePair[];
}

export interface MapFormState {
  readonly documentId: number | null;
  readonly tableDefId: number | null;
  readonly volumeMode: VolumeMode;
  readonly filterAttribute: string;
  readonly filterScope: AttributeScope | null;
  readonly filterValue: string;
  readonly isActive: boolean;
  readonly fields: readonly FieldRow[];
}

let rowSeed = 0;

/** Новий порожній рядок сітки. */
export function newFieldRow(patch: Partial<Omit<FieldRow, 'key'>> = {}): FieldRow {
  rowSeed += 1;

  return {
    key: `f${String(rowSeed)}`,
    targetColumnDefId: null,
    sourceAttribute: '',
    attributeScope: 'Event',
    valueKind: 'Direct',
    sourceUnitId: null,
    targetUnitId: null,
    values: [],
    ...patch,
  };
}

/**
 * Стан нового мапінгу: `$start` і `$end` уже стоять рядками (без колонки) — вони обов'язкові, і людина лише
 * обирає, куди вони лягають.
 */
export function emptyForm(): MapFormState {
  return {
    documentId: null,
    tableDefId: null,
    volumeMode: 'None',
    filterAttribute: '',
    filterScope: null,
    filterValue: '',
    isActive: true,
    fields: [newFieldRow({ sourceAttribute: StartAttribute }), newFieldRow({ sourceAttribute: EndAttribute })],
  };
}

/** Стан форми з мапінгу, що вже є на сервері. */
export function formFromMap(map: SourceEventMap): MapFormState {
  return {
    documentId: map.documentId,
    tableDefId: map.tableDefId,
    volumeMode: map.volumeMode,
    filterAttribute: map.filterAttribute ?? '',
    filterScope: map.filterScope,
    filterValue: map.filterValue ?? '',
    isActive: map.isActive,
    fields: map.fields.map((field) =>
      newFieldRow({
        targetColumnDefId: field.targetColumnDefId,
        sourceAttribute: field.sourceAttribute,
        attributeScope: field.attributeScope,
        valueKind: field.valueKind,
        sourceUnitId: field.sourceUnitId,
        targetUnitId: field.targetUnitId,
        values: field.values.map((value) => ({
          sourceValue: value.sourceValue,
          registryEntryId: value.registryEntryId,
        })),
      }),
    ),
  };
}

/** Чи атрибут зарезервований (`$start`/`$end`/`$name`): його область завжди `Event`. */
export function isReserved(attribute: string): boolean {
  return (ReservedAttributes as readonly string[]).includes(attribute);
}

/** Чи колонка — довідник: лише для неї мають сенс пошук за кодом, назвою і таблиця відповідностей. */
export function isLookupColumn(column: MapColumn | undefined): boolean {
  return column?.dataType === 'Lookup';
}

/** Чи колонка — дата (ціль `$start`/`$end`). */
export function isDateColumn(column: MapColumn | undefined): boolean {
  return column?.dataType === 'Date';
}

/** Чи колонка числова: лише для неї показуються одиниці. */
export function isNumericColumn(column: MapColumn | undefined): boolean {
  return column?.dataType === 'Decimal' || column?.dataType === 'Int';
}

/** Які способи запису значення має сенс пропонувати для колонки. */
export function valueKindsFor(column: MapColumn | undefined): readonly ValueKind[] {
  return isLookupColumn(column) ? ValueKinds : ['Direct'];
}

/** Чому форму не можна зберегти; порожньо — можна. */
export type MapFormProblem =
  | 'documentRequired'
  | 'tableRequired'
  | 'startEndRequired'
  | 'startEndNotDate'
  | 'fieldIncomplete'
  | 'duplicateColumn'
  | 'valueKindMismatch'
  | 'valueMapIncomplete'
  | 'filterIncomplete';

/**
 * Перевірка форми до надсилання — ті самі правила, що в обробнику (§4.7.3):
 * обов'язкові `$start`/`$end` на Date-колонки, звуження — трьома значеннями разом або жодним, способи
 * Lookup — лише на Lookup-колонці, таблиця відповідностей — без порожніх пар.
 */
export function validateForm(state: MapFormState, columns: readonly MapColumn[]): MapFormProblem[] {
  const problems: MapFormProblem[] = [];
  const byId = new Map(columns.map((column) => [column.id, column]));

  if (state.documentId === null) problems.push('documentRequired');
  if (state.tableDefId === null) problems.push('tableRequired');

  const start = state.fields.find((field) => field.sourceAttribute === StartAttribute);
  const end = state.fields.find((field) => field.sourceAttribute === EndAttribute);

  if (start?.targetColumnDefId == null || end?.targetColumnDefId == null) {
    problems.push('startEndRequired');
  } else if (
    !isDateColumn(byId.get(start.targetColumnDefId)) ||
    !isDateColumn(byId.get(end.targetColumnDefId))
  ) {
    problems.push('startEndNotDate');
  }

  if (state.fields.some((field) => field.targetColumnDefId === null || field.sourceAttribute.trim().length === 0)) {
    problems.push('fieldIncomplete');
  }

  const targets = state.fields.flatMap((field) => (field.targetColumnDefId === null ? [] : [field.targetColumnDefId]));
  if (new Set(targets).size !== targets.length) problems.push('duplicateColumn');

  if (
    state.fields.some(
      (field) =>
        field.valueKind !== 'Direct' &&
        field.targetColumnDefId !== null &&
        columns.length > 0 &&
        !isLookupColumn(byId.get(field.targetColumnDefId)),
    )
  ) {
    problems.push('valueKindMismatch');
  }

  if (
    state.fields.some(
      (field) =>
        field.valueKind === 'ValueMap' &&
        (field.values.length === 0 ||
          field.values.some((pair) => pair.sourceValue.trim().length === 0 || pair.registryEntryId === null)),
    )
  ) {
    problems.push('valueMapIncomplete');
  }

  const filterParts = [state.filterAttribute.trim().length > 0, state.filterScope !== null, state.filterValue.trim().length > 0];
  if (filterParts.some(Boolean) && !filterParts.every(Boolean)) problems.push('filterIncomplete');

  return problems;
}

function fieldInput(field: FieldRow): SourceEventFieldInput {
  return {
    targetColumnDefId: field.targetColumnDefId ?? 0,
    sourceAttribute: field.sourceAttribute.trim(),
    attributeScope: isReserved(field.sourceAttribute) ? 'Event' : field.attributeScope,
    valueKind: field.valueKind,
    sourceUnitId: field.sourceUnitId,
    targetUnitId: field.targetUnitId,
    values:
      field.valueKind === 'ValueMap'
        ? field.values.map((pair) => ({ sourceValue: pair.sourceValue.trim(), registryEntryId: pair.registryEntryId ?? 0 }))
        : null,
  };
}

/** Звуження: три значення разом або три `null` — половинчастого сервер не приймає. */
function filterOf(state: MapFormState): Pick<CreateSourceEventMapRequest, 'filterAttribute' | 'filterScope' | 'filterValue'> {
  const attribute = state.filterAttribute.trim();
  const value = state.filterValue.trim();

  return attribute.length > 0 && value.length > 0 && state.filterScope !== null
    ? { filterAttribute: attribute, filterScope: state.filterScope, filterValue: value }
    : { filterAttribute: null, filterScope: null, filterValue: null };
}

/** Тіло `POST /source-event-maps`. Кличеться лише для форми без проблем (`validateForm`). */
export function toCreateRequest(sourceEntityId: number, state: MapFormState): CreateSourceEventMapRequest {
  return {
    sourceEntityId,
    documentId: state.documentId ?? 0,
    tableDefId: state.tableDefId ?? 0,
    volumeMode: state.volumeMode,
    ...filterOf(state),
    fields: state.fields.map(fieldInput),
  };
}

/** Тіло `PUT /source-event-maps/{id}` — повна заміна, `isActive: false` — пауза. */
export function toUpdateRequest(state: MapFormState): UpdateSourceEventMapRequest {
  return {
    volumeMode: state.volumeMode,
    isActive: state.isActive,
    ...filterOf(state),
    fields: state.fields.map(fieldInput),
  };
}

/** Тіло паузи/відновлення: той самий мапінг без жодної зміни, крім `isActive`. */
export function toggleActiveRequest(map: SourceEventMap): UpdateSourceEventMapRequest {
  return toUpdateRequest({ ...formFromMap(map), isActive: !map.isActive });
}

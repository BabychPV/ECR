import type { PermitTypeColumn, PermitTypeTable } from '@/features/permits/api/types';

/*
 * Чисті операції діалогів конструктора типу дозволу (Permits, PM-08c): коди, чернетки
 * стовпців, збирання таблиці й серії «12 місяців». Тут немає React і мережі — це те, що
 * тести доводять мутаціями (ключ, унікальність коду, склад серії).
 *
 * ⛔ Правила кодів дзеркалять сервер (`EcrCode.Pattern`, `PermitHeaderFields`): латиниця,
 * цифри, підкреслення, перший символ — літера, до 64; порівняння без урахування регістру
 * (`StringComparer.OrdinalIgnoreCase` у `PermitTypeDefinitionValidator`). Клієнт не
 * виправляє серверну перевірку, а лише не дає людині набрати те, що сервер відхилить.
 */

/** Довжина коду сутності конфігурації (`EcrCode.Pattern`). */
export const MaxCodeLength = 64;

/** Код, зарезервований за посиланням на дозвіл у кожній таблиці (`PermitHeaderFields.PermitFieldCode`). */
export const ReservedColumnCode = 'PERMIT';

/** Типи стовпців, які можна завести з діалогів; `Lookup` налаштовується в інспекторі стовпця. */
export const SimpleColumnKinds = ['String', 'Decimal', 'Int', 'Date', 'Bool'] as const;

/** Тип стовпця, який діалог уміє створити. */
export type SimpleColumnKind = (typeof SimpleColumnKinds)[number];

/** Чи несе тип одиницю виміру. */
export function isNumericKind(dataType: string): boolean {
  return dataType === 'Decimal' || dataType === 'Int';
}

/** Стиль складання коду з назви. */
export type CodeStyle = 'snake' | 'pascal';

/** Слова назви: лише латиниця й цифри, решта — роздільник. */
function wordsOf(name: string): string[] {
  return name.split(/[^A-Za-z0-9]+/).filter((word) => word.length > 0);
}

/**
 * Код із назви: «Water body» → `Water_body` (стовпець) або `WaterBody` (таблиця).
 *
 * ⚠ Назва без жодної латинської літери чи цифри (лише кирилиця) дає `fallback`: код мусить
 * існувати, а вигадувати транслітерацію тут означало б зашити мовну таблицю в діалог.
 * ⚠ Перший символ — літера: код із цифри отримує префікс `fallback_`.
 */
export function codeFromName(
  name: string,
  style: CodeStyle,
  fallback: string,
  maxLength: number = MaxCodeLength,
): string {
  const words = wordsOf(name);
  const joined =
    style === 'pascal'
      ? words.map((word) => word.charAt(0).toUpperCase() + word.slice(1)).join('')
      : words.join('_');

  let code = joined.length === 0 ? fallback : joined;
  if (!/^[A-Za-z]/.test(code)) code = `${fallback}_${code}`;

  return code.slice(0, Math.max(1, maxLength)).replace(/_+$/, '') || fallback;
}

/**
 * Код, якого ще немає серед `existing` (без урахування регістру); колізія додає `_2`, `_3` …
 *
 * ⛔ Порівняння без регістру — як на сервері: `January` і `JANUARY` для нього один стовпець.
 */
export function uniqueCode(
  base: string,
  existing: Iterable<string>,
  maxLength: number = MaxCodeLength,
): string {
  const taken = new Set<string>();
  for (const code of existing) taken.add(code.toLowerCase());

  const first = base.slice(0, maxLength);
  if (!taken.has(first.toLowerCase())) return first;

  for (let n = 2; ; n += 1) {
    const suffix = `_${String(n)}`;
    const candidate = base.slice(0, Math.max(1, maxLength - suffix.length)) + suffix;
    if (!taken.has(candidate.toLowerCase())) return candidate;
  }
}

/** Код реєстру таблиці: `PRM_<тип>_<таблиця>` (`PermitHeaderFields.TableRegistryCode`). */
export function tableRegistryCode(typeCode: string, tableCode: string): string {
  return `PRM_${typeCode}_${tableCode}`;
}

/** Скільки знаків лишається на код таблиці, щоб код її реєстру вмістився в 64. */
export function maxTableCodeLength(typeCode: string): number {
  return Math.max(1, MaxCodeLength - tableRegistryCode(typeCode, '').length);
}

/** Чернетка стовпця у формі діалогу. */
export interface DraftColumn {
  /** Стабільний ключ рядка форми (не код: код ще не відомий). */
  readonly id: string;

  /** Назва, яку бачить і править людина (англійська або перша наявна). */
  readonly name: string;

  /** Вихідні назви мовами; `null` — стовпець новий. */
  readonly nameL10n: Readonly<Record<string, string>> | null;
  readonly dataType: string;
  readonly unitId: number | null;
  readonly required: boolean;
  readonly key: boolean;

  /** Стовпець, з якого скопійовано чернетку: тримає код, групу, точність і налаштування списку. */
  readonly base: PermitTypeColumn | null;
}

let draftSequence = 0;

/** Новий ключ рядка форми. */
export function newDraftId(): string {
  draftSequence += 1;

  return `draft-${String(draftSequence)}`;
}

/** Порожній текстовий стовпець. */
export function emptyDraftColumn(overrides: Partial<DraftColumn> = {}): DraftColumn {
  return {
    id: newDraftId(),
    name: '',
    nameL10n: null,
    dataType: 'String',
    unitId: null,
    required: false,
    key: false,
    base: null,
    ...overrides,
  };
}

/** Назва стовпця для показу: англійська, інакше перша непорожня. */
export function displayName(names: Readonly<Record<string, string>>): string {
  const en = names['en'];
  if (en !== undefined && en.length > 0) return en;

  return Object.values(names).find((value) => value.length > 0) ?? '';
}

/** Чернетка зі стовпця наявної таблиці («копія іншої таблиці»). */
export function draftFromColumn(column: PermitTypeColumn): DraftColumn {
  return {
    id: newDraftId(),
    name: displayName(column.nameL10n),
    nameL10n: column.nameL10n,
    dataType: column.dataType,
    unitId: column.unitId,
    required: column.required,
    key: column.key,
    base: column,
  };
}

/** Результат перевірки стовпців майстра. */
export type ColumnsProblem =
  | { readonly kind: 'unnamed'; readonly index: number }
  | { readonly kind: 'noKey' };

/**
 * Перевірка кроку «Columns»: кожен стовпець має назву, хоча б один — ключ.
 *
 * ⛔ Без ключа рядок не мав би адреси: сервер відмовляє `tableWithoutKey`, а майстер не
 * чекає відмови (мокап: «Mark at least one column as Key»).
 */
export function validateColumns(columns: readonly DraftColumn[]): ColumnsProblem | null {
  const unnamed = columns.findIndex((column) => column.name.trim().length === 0);
  if (unnamed >= 0) return { kind: 'unnamed', index: unnamed };
  if (!columns.some((column) => column.key)) return { kind: 'noKey' };

  return null;
}

/** Назви ключових стовпців для рядка «Unique row: Permit + …». */
export function keyColumnNames(columns: readonly DraftColumn[]): string[] {
  return columns.filter((column) => column.key).map((column) => column.name.trim());
}

/** Підписи нового стовпця: старі мови лишаються, лише якщо назву не міняли. */
function namesOf(draft: DraftColumn): Record<string, string> {
  const name = draft.name.trim();
  if (draft.nameL10n !== null && displayName(draft.nameL10n) === name) return { ...draft.nameL10n };

  return { en: name };
}

/**
 * Стовпець запиту зі чернетки.
 *
 * ⚠ Ключовий стовпець завжди обов'язковий (контракт `PermitTypeColumnDto.Required`).
 * ⚠ Точність і масштаб лишаються лише в `Decimal` і лише разом; налаштування списку
 * переходять лише у `Lookup`, а прихований реєстр `TypeOnly` сервер виводить сам.
 */
export function buildColumn(
  draft: DraftColumn,
  order: number,
  usedCodes: Set<string>,
): PermitTypeColumn {
  const base = draft.base;
  const code =
    base !== null
      ? base.code
      : uniqueCode(codeFromName(draft.name, 'snake', 'Column'), usedCodes);
  usedCodes.add(code);

  const decimalBase = draft.dataType === 'Decimal' && base?.dataType === 'Decimal' ? base : null;
  const precision = decimalBase?.precision ?? null;
  const lookupBase = draft.dataType === 'Lookup' && base?.dataType === 'Lookup' ? base : null;

  return {
    code,
    nameL10n: namesOf(draft),
    dataType: draft.dataType,
    unitId: isNumericKind(draft.dataType) ? draft.unitId : null,
    required: draft.required || draft.key,
    key: draft.key,
    group: base?.group ?? null,
    precision,
    scale: precision === null ? null : (decimalBase?.scale ?? null),
    listScope: lookupBase?.listScope ?? null,
    listRegistryCode: lookupBase?.listScope === 'TypeOnly' ? null : (lookupBase?.listRegistryCode ?? null),
    siblingTableCode: lookupBase?.siblingTableCode ?? null,
    choices: lookupBase?.choices ?? null,
    order,
  };
}

/** Що збирається у таблицю. */
export interface NewTableInput {
  readonly typeCode: string;
  readonly nameEn: string;
  readonly nameRu: string;
  readonly columns: readonly DraftColumn[];

  /** Коди таблиць, які тип уже має. */
  readonly existingTableCodes: readonly string[];
}

/** Код нової таблиці: PascalCase з назви, унікальний, із запасом під код реєстру. */
export function newTableCode(
  typeCode: string,
  nameEn: string,
  existingTableCodes: readonly string[],
): string {
  const max = maxTableCodeLength(typeCode);

  return uniqueCode(codeFromName(nameEn, 'pascal', 'Table', max), existingTableCodes, max);
}

/**
 * Нова таблиця типу зі стовпців майстра.
 *
 * ⚠ Коди стовпців, що не скопійовані, видаються послідовно й унікально в межах таблиці (разом
 * із зарезервованим `PERMIT`); скопійовані зберігають свої.
 */
export function buildNewTable(input: NewTableInput): PermitTypeTable {
  const used = new Set<string>([ReservedColumnCode.toLowerCase()]);
  for (const draft of input.columns) {
    if (draft.base !== null) used.add(draft.base.code.toLowerCase());
  }

  const names: Record<string, string> = { en: input.nameEn.trim() };
  if (input.nameRu.trim().length > 0) names['ru'] = input.nameRu.trim();

  return {
    code: newTableCode(input.typeCode, input.nameEn, input.existingTableCodes),
    nameL10n: names,
    order: input.existingTableCodes.length + 1,
    columns: input.columns.map((draft, index) => buildColumn(draft, index + 1, used)),
    rules: [],
  };
}

/** Група серії місяців (код групи; показується як є). */
export const MonthGroupCode = 'Estimated_volume_by_month';

const MonthNames: readonly (readonly [string, string, string])[] = [
  ['January', 'Январь', 'Қаңтар'],
  ['February', 'Февраль', 'Ақпан'],
  ['March', 'Март', 'Наурыз'],
  ['April', 'Апрель', 'Сәуір'],
  ['May', 'Май', 'Мамыр'],
  ['June', 'Июнь', 'Маусым'],
  ['July', 'Июль', 'Шілде'],
  ['August', 'Август', 'Тамыз'],
  ['September', 'Сентябрь', 'Қыркүйек'],
  ['October', 'Октябрь', 'Қазан'],
  ['November', 'Ноябрь', 'Қараша'],
  ['December', 'Декабрь', 'Желтоқсан'],
];

/**
 * Дванадцять стовпців-місяців однієї групи.
 *
 * ⛔ Рівно 12, у календарному порядку, `Decimal`, необов'язкові, не ключові, з однією одиницею
 * й групою — на цьому тримається широка таблиця (31 стовпець) і перевірка «місяці = рік».
 * ⚠ Коди унікалізуються проти наявних у таблиці: другий виклик дає `January_2`.
 */
export function buildMonthColumns(input: {
  readonly unitId: number | null;
  readonly group: string;
  readonly existingCodes: readonly string[];
  readonly startOrder: number;
}): PermitTypeColumn[] {
  const used = new Set<string>([
    ReservedColumnCode.toLowerCase(),
    ...input.existingCodes.map((code) => code.toLowerCase()),
  ]);

  return MonthNames.map(([en, ru, kz], index) => {
    const code = uniqueCode(en, used);
    used.add(code.toLowerCase());

    return {
      code,
      nameL10n: { en, ru, kz },
      dataType: 'Decimal',
      unitId: input.unitId,
      required: false,
      key: false,
      group: input.group,
      precision: null,
      scale: null,
      listScope: null,
      listRegistryCode: null,
      siblingTableCode: null,
      choices: null,
      order: input.startOrder + index,
    };
  });
}

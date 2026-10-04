import type { components } from '@/api/schema';
import type { RegistryBatchItem, RegistryBatchResult, RegistryRow } from '@/features/registries/rows/api';
import { t } from '@/shared/i18n';
import { normalizeUserDecimal } from '@/shared/format/userDecimal';
import { canonicalKey, type KeyPart, type KeyPartType } from '@/features/registries/keys/normalizeKey';

/**
 * Модель табличного редактора даних довідника (`ФВ-8.12`, FEATURE-REGISTRY-TABLES §8.4).
 *
 * ⚠ Лише чиста логіка: що змінено, що з цього піде в пакет (`POST …/entries/batch`), де дубль
 * ключа і до якої комірки належить помилка звіту. Компоненти цього не рахують самі — так кожне
 * правило має тест без рендера.
 */

export type RegistryField = components['schemas']['RegistryFieldDto'];
export type RegistryKey = components['schemas']['RegistryKeyDto'];

/** Незбережений стан рядка. Ключ рядка — `e:<id>` для наявного, `n:<seq>` для нового. */
export interface RowDraft {
  readonly rowKey: string;
  /** Наявний запис; `null` — новий рядок. */
  readonly id: number | null;
  /** `version` рядка з `GET …/rows` — `baseVersion` пакета (`D-166`). */
  readonly baseVersion: string | null;
  /** Код нового запису для довідника з ручним кодом. */
  readonly code: string | null;
  /** Змінені значення: код поля → рядок у поданні `value`; `null` — очистити. */
  readonly values: Readonly<Record<string, string | null>>;
  /** Підписи вибраних цілей `Lookup`/`Unit` — щоб комірка показувала назву, а не id. */
  readonly displays: Readonly<Record<string, string>>;
  /** Позначено до видалення (оборотно до збереження). */
  readonly deleted: boolean;
}

export const existingRowKey = (id: number): string => `e:${String(id)}`;

/** Порожній стан наявного рядка — з нього починається перша правка. */
export function draftOf(row: RegistryRow): RowDraft {
  return {
    rowKey: existingRowKey(row.id),
    id: row.id,
    baseVersion: row.version,
    code: null,
    values: {},
    displays: {},
    deleted: false,
  };
}

/** Новий рядок. */
export function newDraft(seq: number): RowDraft {
  return { rowKey: `n:${String(seq)}`, id: null, baseVersion: null, code: '', values: {}, displays: {}, deleted: false };
}

/** Правка комірки. Значення, що повернулося до збереженого, знімається з чернетки. */
export function setCell(
  draft: RowDraft,
  row: RegistryRow | undefined,
  field: string,
  value: string | null,
  display?: string,
): RowDraft {
  const normalized = value === null || value.trim() === '' ? null : value.trim();
  const stored = row?.values[field]?.value ?? null;
  const values = { ...draft.values };
  const displays = { ...draft.displays };

  if (row !== undefined && normalized === stored) {
    delete values[field];
    delete displays[field];
  } else {
    values[field] = normalized;
    if (display !== undefined) displays[field] = display;
    else delete displays[field];
  }

  return { ...draft, values, displays };
}

/**
 * Введене людиною число → інваріантний запис (`12,5` → `12.5`, `1 000` → `1000`) для полів
 * `Int`/`Decimal`; решта типів і неоднозначне (`1,234`) — як є, їх назве `validateCell`.
 *
 * ⚠ Саме інваріантний рядок іде в пакет: сервер читає його однаково в будь-якій мові, і та сама
 * вставка з ru/kz Excel не стає червоною лише через кому.
 */
export function normalizeCellInput(field: Pick<RegistryField, 'dataType'>, value: string | null): string | null {
  if (value === null || (field.dataType !== 'Int' && field.dataType !== 'Decimal')) return value;
  const read = normalizeUserDecimal(value);
  return read.kind === 'number' ? read.text : value;
}

/** Чинне значення комірки: правка, інакше збережене. */
export function cellValue(row: RegistryRow | undefined, draft: RowDraft | undefined, field: string): string | null {
  if (draft !== undefined && field in draft.values) return draft.values[field] ?? null;
  return row?.values[field]?.value ?? null;
}

/** Що показати в комірці: для `Lookup`/`Unit` — назва цілі, а не id (`ФВ-8.8`). */
export function cellDisplay(row: RegistryRow | undefined, draft: RowDraft | undefined, field: string): string {
  if (draft !== undefined && field in draft.values) {
    return draft.displays[field] ?? draft.values[field] ?? '';
  }
  const stored = row?.values[field];
  return stored?.display ?? stored?.value ?? '';
}

/** Чи є в чернетці що зберігати. */
export function isDirty(draft: RowDraft): boolean {
  if (draft.id === null) return !draft.deleted;
  return draft.deleted || Object.keys(draft.values).length > 0;
}

/**
 * Перевірка значення до сервера. Повертає вид відмови або `null` (текст — `checkText`).
 *
 * ⚠ Число читається за правилами сервера (`CultureNumberReader`, `normalizeUserDecimal`):
 * `12,5` — 12.5 у будь-якій мові, і відмовою тут не є. Неоднозначне лише `1,234` (кома й рівно
 * три цифри — розряди чи дріб?) — `ambiguous`, і людина дізнається про це до збереження.
 */
export type CellCheck = 'required' | 'notInteger' | 'ambiguous' | 'notNumber' | 'notDate' | 'notBool';

export function validateCell(field: RegistryField, value: string | null): CellCheck | null {
  if (value === null) return field.isRequired ? 'required' : null;

  switch (field.dataType) {
    case 'Int': {
      const read = normalizeUserDecimal(value);
      return read.kind === 'number' && /^-?\d+$/.test(read.text) ? null : 'notInteger';
    }
    case 'Decimal': {
      const read = normalizeUserDecimal(value);
      return read.kind === 'number' ? null : read.kind;
    }
    case 'Date':
      return isIsoDate(value) ? null : 'notDate';
    case 'Bool':
      return value === 'true' || value === 'false' ? null : 'notBool';
    default:
      return null;
  }
}

/** Текст відмови перевірки комірки — літералами, щоб сторож каталогу бачив кожен ключ. */
export function checkText(check: CellCheck): string {
  switch (check) {
    case 'required':
      return t('registries.data.required');
    case 'notInteger':
      return t('registries.data.notInteger');
    case 'ambiguous':
      return t('registries.data.decimalDot');
    case 'notNumber':
      return t('registries.data.notNumber');
    case 'notDate':
      return t('registries.data.notDate');
    case 'notBool':
      return t('registries.data.notBool');
  }
}

function isIsoDate(value: string): boolean {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  if (match === null) return false;
  const [year, month, day] = [Number(match[1]), Number(match[2]), Number(match[3])];
  const date = new Date(Date.UTC(year, month - 1, day));
  return date.getUTCFullYear() === year && date.getUTCMonth() === month - 1 && date.getUTCDate() === day;
}

/**
 * Пакет із чернеток. Наявний рядок несе лише змінені поля і свій `baseVersion`; новий — усі
 * заповнені. Новий рядок, позначений до видалення, до сервера не йде зовсім.
 */
export function toBatch(drafts: Iterable<RowDraft>): RegistryBatchItem[] {
  const items: RegistryBatchItem[] = [];

  for (const draft of drafts) {
    if (!isDirty(draft)) continue;

    if (draft.deleted) {
      items.push({ clientRowId: draft.rowKey, op: 'delete', id: draft.id, code: null, baseVersion: draft.baseVersion, values: null });
      continue;
    }

    const values: Record<string, string | null> = {};
    for (const [field, value] of Object.entries(draft.values)) {
      if (draft.id === null && value === null) continue;
      values[field] = value;
    }

    items.push({
      clientRowId: draft.rowKey,
      op: 'upsert',
      id: draft.id,
      code: draft.id === null && draft.code !== null && draft.code.trim() !== '' ? draft.code.trim() : null,
      baseVersion: draft.id === null ? null : draft.baseVersion,
      values,
    });
  }

  return items;
}

/** Рядок сітки для пошуку дублів: ключ рядка й чинні значення. */
export interface KeyedRow {
  readonly rowKey: string;
  readonly values: Readonly<Record<string, string | null>>;
}

/** Дубль ключа серед рядків сітки: який ключ і з яким рядком (номер з 1). */
export interface DuplicateMark {
  readonly keyCode: string;
  readonly otherRow: number;
}

/**
 * Дублі активних ключів серед рядків сітки — видно миттєво, до `dryRun` (§8.4 «Живі перевірки»).
 *
 * ⚠ Той самий канонічний рядок, що й на сервері (`normalizeKey.ts`, спільна фікстура): інакше
 * сітка підсвічувала б дубль, якого сервер не бачить. Рядок із порожньою частиною ключа не
 * рахується (`D-153`).
 */
export function duplicateKeys(
  rows: readonly KeyedRow[],
  keys: readonly RegistryKey[],
  fields: readonly RegistryField[],
): Map<string, DuplicateMark> {
  const marks = new Map<string, DuplicateMark>();
  const byCode = new Map(fields.map((f) => [f.code, f]));

  for (const key of keys) {
    if (!key.isActive) continue;
    const seen = new Map<string, number>();

    rows.forEach((row, index) => {
      const parts: KeyPart[] = [];
      for (const code of key.fieldCodes) {
        const field = byCode.get(code);
        if (field === undefined) return;
        parts.push(keyPart(field.dataType as KeyPartType, row.values[code] ?? null));
      }

      let canonical: string | null;
      try {
        canonical = canonicalKey(parts, key.ignoreCase);
      } catch {
        // Значення ще не того типу — його покаже перевірка комірки, а не ключ.
        return;
      }
      if (canonical === null) return;

      const first = seen.get(canonical);
      if (first === undefined) {
        seen.set(canonical, index);
        return;
      }
      if (!marks.has(row.rowKey)) marks.set(row.rowKey, { keyCode: key.code, otherRow: first + 1 });
      const firstKey = rows[first]?.rowKey;
      if (firstKey !== undefined && !marks.has(firstKey)) marks.set(firstKey, { keyCode: key.code, otherRow: index + 1 });
    });
  }

  return marks;
}

function keyPart(type: KeyPartType, value: string | null): KeyPart {
  if (type === 'Bool') return { type, value: value === null ? null : value === 'true' };
  return { type, value };
}

/** Помилка звіту, прив'язана до рядка й поля (`null` — рядок цілком). */
export interface CellProblem {
  readonly field: string | null;
  readonly messageKey: string;
  readonly params: Readonly<Record<string, string>>;
}

/** Помилки звіту пакета за ключем рядка. */
export function problemsByRow(result: RegistryBatchResult): Map<string, CellProblem[]> {
  const map = new Map<string, CellProblem[]>();
  for (const row of result.rows) {
    if (row.errors.length === 0) continue;
    map.set(
      row.clientRowId,
      row.errors.map((error) => ({ field: error.field, messageKey: error.messageKey, params: error.params })),
    );
  }
  return map;
}

/**
 * Буфер обміну Excel → матриця: табуляція між стовпцями, перенос між рядками, хвостовий порожній
 * рядок (Excel завершує буфер переносом) відкидається.
 *
 * ⚠ Та сама поведінка, що `features/grid/clipboard.ts` `parseClipboard`, але НЕ імпорт звідти:
 * спільний модуль із сіткою документа Rollup виносить в окремий чанк, і маршрут `DocumentPage`
 * важчав на 0.5 КБ gzip при запасі 1 КБ (бюджет `D-132`).
 */
export function parseBlock(text: string): string[][] {
  const lines = text.replace(/\r\n?/g, '\n').split('\n');
  while (lines.length > 0 && lines[lines.length - 1] === '') lines.pop();
  return lines.map((line) => line.split('\t'));
}

/** Правки однієї вставки: рядок сітки (індекс, може бути за межею — тоді новий рядок) і поле. */
export interface PastedCell {
  readonly rowIndex: number;
  readonly field: string;
  readonly text: string;
}

/**
 * Вставка блоку з Excel (`Ctrl+V`, §8.4): прямокутник значень від активної комірки праворуч і
 * вниз. Стовпці за останнім полем відкидаються, рядки за останнім — стають новими рядками.
 */
export function planBlockPaste(
  matrix: readonly (readonly string[])[],
  startRow: number,
  startColumn: number,
  columns: readonly string[],
): PastedCell[] {
  const cells: PastedCell[] = [];
  matrix.forEach((line, r) => {
    line.forEach((text, c) => {
      const field = columns[startColumn + c];
      if (field === undefined) return;
      cells.push({ rowIndex: startRow + r, field, text: text.trim() });
    });
  });
  return cells;
}

/** Текст вставки для `Bool`: так/ні в кількох звичних формах. */
export function pastedBool(text: string): string | null {
  const value = text.trim().toLowerCase();
  if (['true', '1', 'yes', 'y', 'да', 'так', 'иә'].includes(value)) return 'true';
  if (['false', '0', 'no', 'n', 'нет', 'ні', 'жоқ'].includes(value)) return 'false';
  return null;
}

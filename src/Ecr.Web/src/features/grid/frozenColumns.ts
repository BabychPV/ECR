import type { ColumnRegular } from '@revolist/revogrid';
import type { ColumnDto } from '@/api/types';
import { isNumericColumn } from './cellValue';

/**
 * Закріплені (frozen) ключові колонки сітки документа.
 *
 * Широкі аркуші (HSE401 - 63 колонки) при прокрутці вправо втрачають колонки, що
 * ідентифікують рядок. RevoGrid закріплює колонки штатно: `pin: 'colPinStart'` на
 * `ColumnRegular`; закріплені колонки живуть у ВЛАСНОМУ в'юпорті, окремому від `rgCol`.
 *
 * ⚠ Скрізь тут `count` - кількість закріплених колонок СІТКИ, тобто разом з колонкою підпису
 * рядка (`__rowLabel`), якщо вона є. Закріплюються завжди ПЕРШІ `count` колонок, тож глобальний
 * порядок колонок (aria-colindex, індекси виділення) не змінюється.
 *
 * ⛔ Чистий модуль: правила за замовчуванням, обмеження й накладання `pin` перевіряються без
 * сітки (`frozenColumns.test.ts`). Подія RevoGrid -> глобальний індекс колонки - у `selection.ts`.
 */

/** Скільки колонок ДАНИХ закріплюється за замовчуванням щонайбільше. */
export const MaxDefaultFrozenDataColumns = 4;

/**
 * Скільки пікселів закріплена частина (з колонкою підпису) може займати за замовчуванням.
 *
 * ⚠ Закріплена частина не прокручується: якщо вона займе пів екрана, прокручуваній решті
 * нічого лишиться. Це стеля ЛИШЕ для типового вибору; явний вибір користувача її не обмежує.
 */
export const MaxDefaultFrozenWidthPx = 600;

/**
 * Типове закріплення діє лише на ШИРОКИХ таблицях: менше колонок даних - не закріплюється нічого.
 * На таблиці з 3-5 колонок воно безглузде (прокручувати нічого). Явний вибір користувача діє на будь-якій.
 */
export const MinDataColumnsForDefaultFreeze = 12;

/** Скільки перших колонок пропонує випадний список «Freeze columns». */
export const MaxFrozenChoices = 8;

/** Значення випадного списку для «Default». */
export const FrozenDefaultChoice = 'default';

/** Значення випадного списку для «None». */
export const FrozenNoneChoice = '0';

/** Що про колонку даних треба знати, щоб вирішити, чи закріплювати її. */
export interface FrozenColumnProbe {
  readonly numeric: boolean;
  readonly width: number;
  /** Група шапки ВЕРХНЬОГО рівня; `null` - колонка без групи. */
  readonly groupId: number | null;
}

interface GroupLike {
  readonly id: number;
  readonly parentId?: number | null;
}

/** Id групи верхнього рівня для найглибшої групи колонки; `null` - групи немає. */
export function topGroupIdOf(
  groupId: number | null | undefined,
  groups: readonly GroupLike[] | null | undefined,
): number | null {
  if (groupId === null || groupId === undefined || groups === null || groups === undefined) return null;

  const byId = new Map(groups.map((group) => [group.id, group] as const));
  const seen = new Set<number>();
  let current = byId.get(groupId);
  if (current === undefined) return null;

  while (current.parentId !== null && current.parentId !== undefined && !seen.has(current.id)) {
    seen.add(current.id);
    const parent = byId.get(current.parentId);
    if (parent === undefined) break;
    current = parent;
  }

  return current.id;
}

/** Колонки зрізу -> дані для рішення про закріплення. */
export function frozenProbesOf(
  columns: readonly ColumnDto[],
  groups: readonly GroupLike[] | null | undefined,
  widthOf: (column: ColumnDto) => number,
): FrozenColumnProbe[] {
  return columns.map((column) => ({
    numeric: isNumericColumn(column),
    width: widthOf(column),
    groupId: topGroupIdOf(column.headerGroupId, groups),
  }));
}

/** Кількість закріплюваних колонок у межах `0..total-1`: хоч одна колонка лишається прокручуваною. */
export function clampFrozenCount(count: number, totalColumns: number): number {
  if (!Number.isFinite(count) || totalColumns <= 1) return 0;

  return Math.max(0, Math.min(Math.trunc(count), totalColumns - 1));
}

/**
 * Типова кількість закріплених колонок (разом з колонкою підпису).
 *
 * Правило: колонка підпису (якщо є) + ведучий пробіг НЕчислових колонок даних (Lookup/Text/...),
 * зупинка на першій числовій; не більше `MaxDefaultFrozenDataColumns` колонок даних і не ширше
 * `MaxDefaultFrozenWidthPx`. Якщо ведучий пробіг покриває ВСІ колонки (числових немає), закріплюється
 * лише колонка підпису (або нічого) - закріплювати «все» безглуздо.
 *
 * ⚠ Межа не розрізає групу шапки верхнього рівня: якщо остання закріплена колонка й наступна
 * належать одній групі, закріплення відступає до початку цієї групи (група лишається цілою в одному
 * в'юпорті, її підпис не ділиться на два).
 */
export function defaultFrozenCount(
  hasRowLabel: boolean,
  probes: readonly FrozenColumnProbe[],
  labelWidth = 0,
): number {
  const total = (hasRowLabel ? 1 : 0) + probes.length;
  const labelCount = hasRowLabel ? 1 : 0;

  if (probes.length < MinDataColumnsForDefaultFreeze) return 0;

  let run = 0;
  while (run < probes.length && probes[run]?.numeric === false) run += 1;
  if (run === probes.length) return clampFrozenCount(labelCount, total);

  let lead = 0;
  let width = hasRowLabel ? labelWidth : 0;
  while (lead < run && lead < MaxDefaultFrozenDataColumns) {
    const next = width + (probes[lead]?.width ?? 0);
    if (next > MaxDefaultFrozenWidthPx) break;
    width = next;
    lead += 1;
  }

  while (lead > 0) {
    const last = probes[lead - 1]?.groupId ?? null;
    const following = probes[lead]?.groupId ?? null;
    if (last === null || last !== following) break;
    lead -= 1;
  }

  return clampFrozenCount(labelCount + lead, total);
}

/** Підсумкова кількість: вибір користувача (якщо є) або типова, у межах сітки. */
export function resolveFrozenCount(override: number | undefined, defaultCount: number, totalColumns: number): number {
  return clampFrozenCount(override ?? defaultCount, totalColumns);
}

/** Перші `count` колонок отримують `pin: 'colPinStart'`; решта не чіпається. */
export function applyFrozenColumns(columns: readonly ColumnRegular[], count: number): ColumnRegular[] {
  if (count <= 0) return columns as ColumnRegular[];

  return columns.map((column, index) => (index < count ? { ...column, pin: 'colPinStart' as const } : column));
}

/** Пункт випадного списку. */
export interface FrozenChoice {
  /** `default`, `0` (None) або кількість колонок (рядок) з колонкою підпису включно. */
  readonly value: string;
  /** Для пунктів «До колонки X»: заголовок колонки; `null` - службові пункти. */
  readonly columnName: string | null;
}

/**
 * Пункти вибору: None, «до колонки X» для перших `MaxFrozenChoices` колонок, Default.
 * Остання колонка не пропонується (хоч одна лишається прокручуваною).
 */
export function frozenChoices(columnNames: readonly string[]): FrozenChoice[] {
  const upTo = Math.min(MaxFrozenChoices, Math.max(0, columnNames.length - 1));
  const choices: FrozenChoice[] = [{ value: FrozenDefaultChoice, columnName: null }, { value: FrozenNoneChoice, columnName: null }];

  for (let index = 1; index <= upTo; index += 1) {
    choices.push({ value: String(index), columnName: columnNames[index - 1] ?? '' });
  }

  return choices;
}

/** Значення випадного списку для поточного вибору користувача. */
export function frozenChoiceValue(override: number | undefined, totalColumns: number): string {
  if (override === undefined) return FrozenDefaultChoice;

  return String(clampFrozenCount(override, totalColumns));
}

/** Значення випадного списку -> вибір користувача; `undefined` - «Default». */
export function frozenOverrideOfChoice(value: string | null): number | undefined {
  if (value === null || value === FrozenDefaultChoice) return undefined;

  const parsed = Number.parseInt(value, 10);

  return Number.isInteger(parsed) && parsed >= 0 ? parsed : undefined;
}

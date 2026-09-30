import type { RegistryEntryDto } from '@/api/types';
import { registryUsage, type UsageResponse } from '@/features/registries/api';
import {
  getRegistryRows,
  type RegistryRow,
  type RegistryRowsQuery,
  type RegistryRowsPage,
} from '@/features/registries/rows/api';

/**
 * «Де використовується» ОДИН запис довідника (ФВ-8.14) — зібране з наявних читань.
 *
 * ⛔ Окремого серверного звіту на запис немає: `GET /registries/{code}/usage` відповідає про
 * ДОВІДНИК (які поля, колонки й речовини можуть на нього посилатися), а відмова видалення
 * `ECR-REG-0409` — лише лічильник за видами. Тут з цих двох джерел і `GET …/rows` зібрано те,
 * що можна назвати ПОІМЕННО, а решту видів названо прямо як «сервер не перелічує» — не «нуль».
 * Сказати «не використано» там, де ми просто не питали, — найдорожча з помилок (`L10`).
 */

/** Скільки записів-посилань читати з одного поля (сторінка `…/rows`). */
export const ReferencingRowsLimit = 20;

/** Поле іншого (або цього ж) довідника, що є `Lookup` на цей довідник. */
export interface ReferencingField {
  /** Код довідника-власника поля. */
  readonly registryCode: string;
  /** Код поля. */
  readonly fieldCode: string;
  /** Маршрут до опису довідника-власника (з відповіді сервера). */
  readonly route: string | null;
}

/** Результат читання записів одного поля: або рядки, або відмова саме цього читання. */
export type FieldReferences =
  | { readonly field: ReferencingField; readonly status: 'ok'; readonly rows: readonly RegistryRow[]; readonly total: number }
  | { readonly field: ReferencingField; readonly status: 'error'; readonly error: unknown };

/** Методологія, що оголошує цей запис речовиною. */
export interface SubstanceUse {
  readonly id: string;
  readonly route: string | null;
}

/** Шаблонна колонка, що бере значення з цього довідника (контекст рівня довідника). */
export interface ColumnUse {
  readonly id: string;
  readonly label: string;
  readonly route: string | null;
}

/** Зібраний звіт на запис. */
export interface EntryUsageReport {
  /** Записи довідників, що посилаються на цей запис полем `Lookup`, по полях. */
  readonly fields: readonly FieldReferences[];
  /** Дочірні записи ієрархії (`parentEntryId`). */
  readonly children: readonly RegistryEntryDto[];
  /** Речовини методологій, що вказують саме на цей запис. */
  readonly substances: readonly SubstanceUse[];
  /** Колонки шаблонів, що беруть значення з довідника. */
  readonly columns: readonly ColumnUse[];
  /** Чи значення довідника вже лежать у документах (рівень довідника, не запису). */
  readonly dataInDocuments: boolean;
  /**
   * Перелік рівня довідника обрізаний сервером (`total > items.length`): частина полів, колонок
   * чи речовин могла не потрапити у звіт. Показується, а не мовчить.
   */
  readonly truncated: boolean;
}

/** Залежності завантаження — для тестів без мережі. */
export interface EntryUsageDeps {
  readonly usage: (code: string) => Promise<UsageResponse>;
  readonly rows: (code: string, query: RegistryRowsQuery) => Promise<RegistryRowsPage>;
}

const NetworkDeps: EntryUsageDeps = { usage: registryUsage, rows: getRegistryRows };

/**
 * Розбирає підпис `registryField` — `ВЛАСНИК.ПОЛЕ` (`RegistryStore.FindUsageAsync`).
 *
 * ⚠ Ділиться за ОСТАННЬОЮ крапкою: код довідника теоретично може містити крапку, код поля — ні
 * (ідентифікатор виразу). Підпис без крапки або з порожньою частиною — `null`: вигадувати
 * довідник, у якого питати, не можна.
 */
export function parseFieldLabel(label: string): { registryCode: string; fieldCode: string } | null {
  const dot = label.lastIndexOf('.');
  if (dot <= 0 || dot === label.length - 1) return null;

  return { registryCode: label.slice(0, dot), fieldCode: label.slice(dot + 1) };
}

/** Сьогоднішня дата клієнта `yyyy-MM-dd` (не `toISOString`: той зсуває день у від'ємному поясі). */
export function todayIso(now: Date = new Date()): string {
  const year = String(now.getFullYear()).padStart(4, '0');
  const month = String(now.getMonth() + 1).padStart(2, '0');
  const day = String(now.getDate()).padStart(2, '0');

  return `${year}-${month}-${day}`;
}

/**
 * Збирає звіт для запису.
 *
 * ⛔ Відмова читання ОДНОГО поля (довідник-власник заборонений користувачеві — `404` S18, або
 * будь-що інше) не валить звіт і не перетворюється на «немає посилань»: група показує свою
 * помилку, решта — свої рядки. Відмова ж звіту рівня довідника валить усе — без нього не відомо,
 * ДЕ шукати, і будь-яка відповідь була б неповною мовчки.
 *
 * ⚠ Записи-посилання читаються на `asOf` (сьогодні): `…/rows` показує рядки, які бачить пікер, —
 * чинні, активні, невидалені. Посилання із записів, виведених з обігу, тут не видно; це сказано
 * на екрані.
 */
export async function loadEntryUsage(
  registryCode: string,
  entry: Pick<RegistryEntryDto, 'id' | 'code'>,
  siblings: readonly RegistryEntryDto[],
  asOf: string,
  deps: EntryUsageDeps = NetworkDeps,
): Promise<EntryUsageReport> {
  const usage = await deps.usage(registryCode);

  const referencing: ReferencingField[] = [];
  for (const item of usage.items) {
    if (item.kind !== 'registryField') continue;
    const parsed = parseFieldLabel(item.label);
    if (parsed !== null) referencing.push({ ...parsed, route: item.route });
  }

  const fields = await Promise.all(
    referencing.map(async (field): Promise<FieldReferences> => {
      try {
        const page = await deps.rows(field.registryCode, {
          asOf,
          fields: { [field.fieldCode]: String(entry.id) },
          limit: ReferencingRowsLimit,
        });

        // ⛔ Поле, що посилається на власний довідник (ієрархія полем), не рахує сам запис:
        // «запис посилається сам на себе» — не залежність, яку треба прибрати перед зміною.
        const rows = page.items.filter(
          (row) => !(field.registryCode === registryCode && row.id === entry.id),
        );
        const self = page.items.length - rows.length;

        return { field, status: 'ok', rows, total: Math.max(rows.length, (page.totalCount ?? page.items.length) - self) };
      } catch (error: unknown) {
        return { field, status: 'error', error };
      }
    }),
  );

  // ⚠ Підпис `methodologySubstance` — КОД запису (`RegistryStore`): так речовина прив'язана саме
  // до цього запису, а не до довідника загалом.
  const substances = usage.items
    .filter((item) => item.kind === 'methodologySubstance' && item.label === entry.code)
    .map((item) => ({ id: item.id, route: item.route }));

  const columns = usage.items
    .filter((item) => item.kind === 'templateColumn')
    .map((item) => ({ id: item.id, label: item.label, route: item.route }));

  return {
    fields,
    children: siblings.filter((candidate) => candidate.parentEntryId === entry.id),
    substances,
    columns,
    dataInDocuments: usage.items.some((item) => item.kind === 'data'),
    truncated: usage.total > usage.items.length,
  };
}

/** Чи є хоч одне ПОІМЕННО знайдене посилання на запис (колонки довідника — не посилання на запис). */
export function hasNamedReferences(report: EntryUsageReport): boolean {
  return (
    report.children.length > 0 ||
    report.substances.length > 0 ||
    report.fields.some((group) => group.status === 'ok' && group.total > 0)
  );
}

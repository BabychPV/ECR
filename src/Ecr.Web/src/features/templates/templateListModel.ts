import type { TemplateSummary, TemplateVersionSummary } from '@/api/types';
import { localized, type LocalizedText } from '@/shared/i18n/localized';

/** Стан версії шаблону (`schema.d.ts` → `TemplateVersionStatus`). */
type TemplateVersionStatus = TemplateVersionSummary['status'];

/**
 * Шаблон переліку з полями, що їх додає контракт «Аналізу»
 * (`lane/analiz/ui-template-summary-ext`).
 *
 * ⚠ Поля НЕОБОВ'ЯЗКОВІ навмисно: гілка мусить збиратися й працювати і до, і
 * після того контракту. Без поля (старий сервер) колонка/показник не
 * малюються (`D15-06`), а не показують «0». Коли контракт у `schema.d.ts`,
 * перетин просто збігається з `TemplateSummary`.
 */
export type TemplateListSummary = TemplateSummary & {
  readonly nameL10n?: LocalizedText | null;
  readonly isArchived?: boolean;
  readonly documentCount?: number;
  readonly updatedAt?: string | null;
  readonly draftAuthorDisplayName?: string | null;
  readonly draftCreatedAt?: string | null;
};

/** Стан шаблону в переліку: стан версій або архів самого шаблону. */
export type TemplateListState = TemplateVersionStatus | 'Archived';

/**
 * Рядок переліку шаблонів (`UI-34`, макет `screens-templates.js` → «1.
 * /admin/templates — перелік»): поточна опублікована версія, чернетка і стан
 * шаблону — з `TemplateSummary` (назва, архів, документи, дата, автор
 * чернетки) і пакета версій.
 *
 * ⛔ Того, чого сервер не віддає (момент останньої правки чернетки, дата
 * архівування), тут НЕМАЄ навмисно (`D15-06`): вигадана клітинка збрехала б.
 */
export interface TemplateListRow {
  readonly template: TemplateListSummary;

  /** Назва мовою інтерфейсу; порожньо — сервер назви не дав. */
  readonly name: string;

  /** Версії шаблону в порядку відповіді сервера (за зростанням Id). */
  readonly versions: readonly TemplateVersionSummary[];

  /** Остання опублікована версія — за нею створюються нові документи. */
  readonly current: TemplateVersionSummary | null;

  /** Відкрита чернетка — остання версія у стані `Draft`. */
  readonly draft: TemplateVersionSummary | null;

  /**
   * Остання застаріла версія — показується лише тоді, коли поточної немає:
   * «1.0.0 — no current version» каже більше, ніж порожня клітинка.
   */
  readonly lastDeprecated: TemplateVersionSummary | null;

  /**
   * Стан шаблону для колонки й фільтра «State».
   *
   * ⚠ Архів шаблону (`isArchived`) перемагає версії: архівований шаблон не
   * пропонується для нових документів, хоч би яка версія була опублікована.
   * Інакше — похідний від версій: є опублікована → `Published`; інакше є
   * чернетка → `Draft`; інакше лише застарілі → `Deprecated`; версій немає →
   * `null`.
   */
  readonly state: TemplateListState | null;
}

function lastOf(
  versions: readonly TemplateVersionSummary[],
  status: TemplateVersionStatus,
): TemplateVersionSummary | null {
  for (let index = versions.length - 1; index >= 0; index -= 1) {
    const version = versions[index];
    if (version?.status === status) return version;
  }

  return null;
}

/** Будує рядок переліку з шаблону і його версій. */
export function toTemplateListRow(
  template: TemplateListSummary,
  versions: readonly TemplateVersionSummary[],
): TemplateListRow {
  const current = lastOf(versions, 'Published');
  const draft = lastOf(versions, 'Draft');
  const lastDeprecated = current === null ? lastOf(versions, 'Deprecated') : null;

  const state: TemplateListState | null = template.isArchived === true
    ? 'Archived'
    : current !== null
      ? 'Published'
      : draft !== null
        ? 'Draft'
        : lastDeprecated !== null
          ? 'Deprecated'
          : null;

  return { template, name: localized(template.nameL10n), versions, current, draft, lastDeprecated, state };
}

/** Чи проходить рядок обраний показник смуги. */
export function matchesStat(row: TemplateListRow, stat: string | null): boolean {
  if (stat === 'published') return row.current !== null;
  if (stat === 'drafts') return row.draft !== null;
  if (stat === 'documents') return (row.template.documentCount ?? 0) > 0;

  return true;
}

/** Скільки опублікованих версій у всіх шаблонах (макет: «published versions»). */
export function publishedVersionCount(rows: readonly TemplateListRow[]): number {
  return rows.reduce(
    (sum, row) => sum + row.versions.filter((version) => version.status === 'Published').length,
    0,
  );
}

/**
 * Скільки документів спирається на шаблони (макет: «documents using them»).
 *
 * ⚠ Сервер рахує лише документи проєктів, видимих цьому читачеві: сума — «ваші
 * документи», не всі в системі.
 */
export function documentCount(rows: readonly TemplateListRow[]): number | null {
  // ⚠ `null` — сервер лічильника не віддає (до контракту): показника немає.
  if (!rows.some((row) => row.template.documentCount !== undefined)) return null;

  return rows.reduce((sum, row) => sum + (row.template.documentCount ?? 0), 0);
}

/** Скільки шаблонів мають відкриту чернетку (макет: «drafts in progress»). */
export function draftCount(rows: readonly TemplateListRow[]): number {
  return rows.filter((row) => row.draft !== null).length;
}

/**
 * Фільтр переліку: пошук за кодом (без регістру), стан і показник смуги.
 *
 * ⚠ Пошук — за кодом і назвою мовою інтерфейсу: рівно тим, що видно в
 * рядку. Шукати за тим, чого на екрані немає (назва іншою мовою), означало б
 * знаходити рядки «ні за що».
 */
export function filterTemplateRows(
  rows: readonly TemplateListRow[],
  filter: { readonly query: string | null; readonly state: string | null; readonly stat: string | null },
): readonly TemplateListRow[] {
  const needle = (filter.query ?? '').trim().toLowerCase();

  return rows.filter(
    (row) =>
      (needle.length === 0 ||
        row.template.code.toLowerCase().includes(needle) ||
        row.name.toLowerCase().includes(needle)) &&
      (filter.state === null || row.state === filter.state) &&
      matchesStat(row, filter.stat),
  );
}

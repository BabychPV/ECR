import type { TemplateSummary, TemplateVersionSummary } from '@/api/types';

/** Стан версії шаблону (`schema.d.ts` → `TemplateVersionStatus`). */
type TemplateVersionStatus = TemplateVersionSummary['status'];

/**
 * Рядок переліку шаблонів (`UI-34`, макет `screens-templates.js` → «1.
 * /admin/templates — перелік»): поточна опублікована версія, чернетка і стан
 * шаблону — з ТИХ даних, що вже є (`TemplateSummary` + пакет версій).
 *
 * ⛔ Колонок «Documents», «Updated» і автора чернетки («… is editing») тут НЕМАЄ
 * навмисно: у `GET /templates` і `GET /templates/versions?ids=` цих полів немає
 * (лічильник документів — лише на картці, `BE-26`). Нуль чи порожня клітинка
 * замість невідомого числа збрехали б (`D15-06`, критерій 3 картки `UI-34`).
 */
export interface TemplateListRow {
  readonly template: TemplateSummary;

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
   * ⚠ Похідний від версій: є опублікована → `Published`; інакше є чернетка →
   * `Draft`; інакше лише застарілі → `Deprecated`; версій немає → `null`.
   * Власного стану «архівовано» в переліку сервер не віддає (TODO-контракт у
   * листі готовності), тож вигадувати його з версій не можна.
   */
  readonly state: TemplateVersionStatus | null;
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
  template: TemplateSummary,
  versions: readonly TemplateVersionSummary[],
): TemplateListRow {
  const current = lastOf(versions, 'Published');
  const draft = lastOf(versions, 'Draft');
  const lastDeprecated = current === null ? lastOf(versions, 'Deprecated') : null;

  const state: TemplateVersionStatus | null =
    current !== null ? 'Published' : draft !== null ? 'Draft' : lastDeprecated !== null ? 'Deprecated' : null;

  return { template, versions, current, draft, lastDeprecated, state };
}

/** Чи проходить рядок обраний показник смуги. */
export function matchesStat(row: TemplateListRow, stat: string | null): boolean {
  if (stat === 'published') return row.current !== null;
  if (stat === 'drafts') return row.draft !== null;

  return true;
}

/** Скільки опублікованих версій у всіх шаблонах (макет: «published versions»). */
export function publishedVersionCount(rows: readonly TemplateListRow[]): number {
  return rows.reduce(
    (sum, row) => sum + row.versions.filter((version) => version.status === 'Published').length,
    0,
  );
}

/** Скільки шаблонів мають відкриту чернетку (макет: «drafts in progress»). */
export function draftCount(rows: readonly TemplateListRow[]): number {
  return rows.filter((row) => row.draft !== null).length;
}

/**
 * Фільтр переліку: пошук за кодом (без регістру), стан і показник смуги.
 *
 * ⚠ Пошук — лише за кодом: назви шаблону в `TemplateSummary` немає
 * (TODO-контракт), а шукати за тим, чого не видно в рядку, — означало б
 * знаходити рядки «ні за що».
 */
export function filterTemplateRows(
  rows: readonly TemplateListRow[],
  filter: { readonly query: string | null; readonly state: string | null; readonly stat: string | null },
): readonly TemplateListRow[] {
  const needle = (filter.query ?? '').trim().toLowerCase();

  return rows.filter(
    (row) =>
      (needle.length === 0 || row.template.code.toLowerCase().includes(needle)) &&
      (filter.state === null || row.state === filter.state) &&
      matchesStat(row, filter.stat),
  );
}

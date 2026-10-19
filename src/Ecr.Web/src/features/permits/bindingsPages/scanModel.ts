import type { TemplateSummary, TemplateVersionSummary } from '@/api/types';
import { localized } from '@/shared/i18n/localized';
import type { PermitBinding, PermitBindings } from '../api/bindingsTypes';
import { PermitsPaths } from '../shell/permitPaths';

/**
 * Модель переліку прив'язок аркушів до пермітів (`/admin/permits/bindings`).
 *
 * ⚠ Сервер не має переліку «усі прив'язки»: прив'язка належить ВЕРСІЇ шаблону
 * (`GET /api/v1/template-versions/{id}/permit-bindings`). Тому перелік збирається на клієнті: беруться шаблони,
 * для кожного — остання опублікована версія й відкрита чернетка, і прив'язки кожної читаються окремим запитом
 * (з обмеженою паралельністю, порціями). Усе тут — чиста логіка без мережі й React.
 */

/** Скільки шаблонів перевіряється за одну порцію («Check more» додає ще стільки ж). */
export const ScanPageSize = 20;

/** Скільки версій читається одночасно. */
export const ScanConcurrency = 4;

/** Яку версію шаблону читаємо: за якою створюються документи або яку правлять зараз. */
export type ScanVersionKind = 'Published' | 'Draft';

/** Версія шаблону, чиї прив'язки треба прочитати. */
export interface ScanSource {
  /** Стабільний ключ: версія. */
  readonly key: string;
  readonly templateId: number;
  readonly templateCode: string;
  /** Назва мовою інтерфейсу; порожньо — сервер назви не дав. */
  readonly templateName: string;
  readonly versionId: number;
  readonly version: string;
  readonly kind: ScanVersionKind;
}

/** План перегляду: що читати зараз і скільки шаблонів охоплено. */
export interface ScanPlan {
  readonly sources: readonly ScanSource[];
  /** Усього шаблонів у відповіді сервера. */
  readonly templatesTotal: number;
  /** Скільки шаблонів увійшло в цю порцію (перші `limit` за кодом). */
  readonly templatesChecked: number;
  /** Скільки з них не мають ні опублікованої версії, ні чернетки (читати нічого). */
  readonly templatesWithoutVersion: number;
}

function lastWithStatus(
  versions: readonly TemplateVersionSummary[],
  status: ScanVersionKind,
): TemplateVersionSummary | null {
  for (let index = versions.length - 1; index >= 0; index -= 1) {
    const version = versions[index];
    if (version?.status === status) return version;
  }

  return null;
}

/**
 * Будує план: перші `limit` шаблонів за кодом; для кожного — остання опублікована версія і остання чернетка.
 *
 * ⚠ Застарілі (`Deprecated`) версії не читаються: за ними вже не створюються документи, а перелік мусить
 * відповідати на «що діє і що в роботі». Шаблон без обох версій лише рахується в `templatesWithoutVersion`.
 */
export function buildScanPlan(
  templates: readonly TemplateSummary[],
  versionsByTemplate: ReadonlyMap<number, readonly TemplateVersionSummary[]>,
  limit: number,
): ScanPlan {
  const ordered = [...templates].sort((a, b) => a.code.localeCompare(b.code));
  const chosen = ordered.slice(0, Math.max(0, limit));
  const sources: ScanSource[] = [];
  let withoutVersion = 0;

  for (const template of chosen) {
    const versions = versionsByTemplate.get(template.id) ?? [];
    const found = [lastWithStatus(versions, 'Published'), lastWithStatus(versions, 'Draft')];
    const kinds: readonly ScanVersionKind[] = ['Published', 'Draft'];
    let any = false;

    found.forEach((version, index) => {
      const kind = kinds[index];
      if (version === null || kind === undefined) return;

      any = true;
      sources.push({
        key: String(version.id),
        templateId: template.id,
        templateCode: template.code,
        templateName: localized(template.nameL10n),
        versionId: version.id,
        version: version.version,
        kind,
      });
    });

    if (!any) withoutVersion += 1;
  }

  return {
    sources,
    templatesTotal: templates.length,
    templatesChecked: chosen.length,
    templatesWithoutVersion: withoutVersion,
  };
}

/** Результат читання прив'язок однієї версії. */
export type ScanResult =
  | { readonly status: 'success'; readonly data: PermitBindings }
  | { readonly status: 'error'; readonly error: unknown };

/** Рядок переліку: прив'язка разом з версією, якій вона належить. */
export interface BindingRow {
  readonly key: string;
  readonly source: ScanSource;
  readonly binding: PermitBinding;
}

/** Підсумок читання по всьому плану. */
export interface ScanOutcome {
  readonly rows: readonly BindingRow[];
  /** Версії, прив'язки яких прочитати не вдалося (помилка рядка не валить перелік). */
  readonly failed: readonly { readonly source: ScanSource; readonly error: unknown }[];
  /** Скільки версій уже дали відповідь (успіх або помилка). */
  readonly settled: number;
  readonly total: number;
  readonly done: boolean;
}

/** Збирає рядки й помилки з поточних результатів; ще не прочитані версії просто не входять. */
export function collectOutcome(
  sources: readonly ScanSource[],
  results: ReadonlyMap<string, ScanResult>,
): ScanOutcome {
  const rows: BindingRow[] = [];
  const failed: { source: ScanSource; error: unknown }[] = [];
  let settled = 0;

  for (const source of sources) {
    const result = results.get(source.key);
    if (result === undefined) continue;

    settled += 1;
    if (result.status === 'error') {
      failed.push({ source, error: result.error });
      continue;
    }

    for (const binding of result.data.items) {
      rows.push({ key: `${source.key}:${binding.code}`, source, binding });
    }
  }

  return { rows, failed, settled, total: sources.length, done: settled >= sources.length };
}

/** Фільтр переліку: пошук і режим; показник смуги (`rows`, `draft`). */
export interface BindingFilter {
  readonly needle: string;
  readonly mode: string;
  readonly stat: string | null;
}

/** Чи проходить рядок показник смуги. */
export function matchesStat(row: BindingRow, stat: string | null): boolean {
  if (stat === 'rows') return row.binding.mode === 'Rows';
  if (stat === 'draft') return row.source.kind === 'Draft';

  return true;
}

/**
 * Пошук без регістру по тому, що видно в рядку: коди прив'язки, таблиці аркуша й таблиці дозволу, тип дозволу,
 * код і назва шаблону. Шукати за тим, чого на екрані немає, означало б знаходити рядки «ні за що».
 */
export function filterRows(rows: readonly BindingRow[], filter: BindingFilter): BindingRow[] {
  const needle = filter.needle.trim().toLocaleLowerCase();

  return rows.filter((row) => {
    if (filter.mode.length > 0 && row.binding.mode !== filter.mode) return false;
    if (!matchesStat(row, filter.stat)) return false;
    if (needle.length === 0) return true;

    const haystack = [
      row.binding.code,
      row.binding.sheetTableCode,
      row.binding.permitTypeCode,
      row.binding.permitTableCode,
      row.source.templateCode,
      row.source.templateName,
    ]
      .join(' ')
      .toLocaleLowerCase();

    return haystack.includes(needle);
  });
}

/** Показники смуги над таблицею (по всьому прочитаному, не по відфільтрованому). */
export function rowStats(rows: readonly BindingRow[]): { all: number; rows: number; draft: number } {
  return {
    all: rows.length,
    rows: rows.filter((row) => matchesStat(row, 'rows')).length,
    draft: rows.filter((row) => matchesStat(row, 'draft')).length,
  };
}

/** Режими, які сервер віддає (`PermitBindingDto.mode`). */
export const BindingModes: readonly string[] = ['Rows', 'Formulas'];

/** Адреса екрана прив'язки: код — сегмент, версія й шаблон — параметри (прив'язку однозначно задає пара «версія, код»). */
export function bindingDetailPath(row: Pick<BindingRow, 'binding' | 'source'>): string {
  return `${PermitsPaths.bindings}/${encodeURIComponent(row.binding.code)}?template=${String(row.source.templateId)}&version=${String(row.source.versionId)}`;
}

/** Де прив'язка редагується: вкладка Permit версії шаблону. */
export function templateEditorPath(templateId: number, versionId: number): string {
  return `/admin/templates/${String(templateId)}/versions/${String(versionId)}?tab=permit`;
}

function positiveInt(raw: string | null): number | null {
  if (raw === null || !/^\d+$/.test(raw)) return null;

  const value = Number(raw);

  return Number.isSafeInteger(value) && value > 0 ? value : null;
}

/** Параметри екрана прив'язки з адреси; `null` — посилання неповне чи зіпсоване. */
export function parseBindingLocation(
  params: URLSearchParams,
): { readonly templateId: number; readonly versionId: number } | null {
  const templateId = positiveInt(params.get('template'));
  const versionId = positiveInt(params.get('version'));

  return templateId === null || versionId === null ? null : { templateId, versionId };
}

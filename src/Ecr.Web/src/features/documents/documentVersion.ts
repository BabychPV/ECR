import type { DocumentSummary } from '@/api/types';

/** Рядок сортування колонки «Version»: зростання, спадання, без сортування (порядок сервера). */
export type VersionSort = 'asc' | 'desc';

/** Поля рядка переліку, з яких виводиться версія шаблону (контракт RPT-STABLE). */
export type VersionSource = Pick<DocumentSummary, 'templateVersion' | 'templateVersionByPeriod'>;

/** Версія шаблону рядка за ОДИН період і звідки вона взялась (для підказки «за період»). */
export interface ResolvedVersion {
  /** `null` — версії немає (старий документ без шаблону чи доступ звужено): «—», а не вигадане «v0». */
  readonly version: string | null;
  /** `true` — значення з розрідженої карти `templateVersionByPeriod` (версія періоду відрізняється від типової). */
  readonly fromPeriod: boolean;
}

/**
 * Версія періоду = `templateVersionByPeriod[період] ?? templateVersion`.
 *
 * ⚠ Ключ карти — рядок `YYYYMM`; період у адресі — число. `periodKey === null` (період не обрано) — типова версія,
 * бо відрізнятись нема від чого. Порожній рядок — не версія (`null`), інакше колонка малювала б порожній бейдж.
 *
 * ⛔ Порядок `??` не міняти: карта розріджена, і лише в ній стоять періоди, де версія ВІДРІЗНЯЄТЬСЯ від типової.
 */
export function resolveTemplateVersion(source: VersionSource, periodKey: number | null): ResolvedVersion {
  const byPeriod = periodKey === null ? undefined : source.templateVersionByPeriod?.[String(periodKey)];
  if (typeof byPeriod === 'string' && byPeriod.trim() !== '') return { version: byPeriod, fromPeriod: true };

  const base = source.templateVersion;
  return { version: typeof base === 'string' && base.trim() !== '' ? base : null, fromPeriod: false };
}

/** Сегменти версії для порівняння: «2.10.0» → [2, 10, 0]; нечислові частини порівнюються як текст. */
function segments(version: string): readonly (number | string)[] {
  return version
    .trim()
    .replace(/^v/i, '')
    .split(/[.\-+]/)
    .map((part) => (/^\d+$/.test(part) ? Number(part) : part.toLowerCase()));
}

/**
 * Порівняння версій «по-людськи»: числа - числами (2.10 > 2.9), текст - текстом; число раніше за текст.
 * Коротша версія, що є початком довшої, менша (2.3 < 2.3.1).
 */
export function compareVersions(a: string, b: string): number {
  const left = segments(a);
  const right = segments(b);
  const length = Math.max(left.length, right.length);

  for (let index = 0; index < length; index += 1) {
    const x = left[index];
    const y = right[index];
    if (x === undefined) return -1;
    if (y === undefined) return 1;
    if (x === y) continue;
    if (typeof x === 'number' && typeof y === 'number') return x < y ? -1 : 1;
    if (typeof x === 'number') return -1;
    if (typeof y === 'number') return 1;
    return x < y ? -1 : 1;
  }

  return 0;
}

/**
 * Параметр `sort` переліку документів для колонки «Version» (`sort=templateVersion` / `sort=-templateVersion`);
 * `null` — без сортування (порядок сервера). ✎ Сортування і фільтр версії - СЕРВЕРНІ: сторінка приходить уже впорядкованою.
 */
export function versionSortParam(sort: VersionSort | null): string | null {
  return sort === null ? null : sort === 'asc' ? 'templateVersion' : '-templateVersion';
}

/** Варіанти фільтра: версії сторінки плюс уже обрані (під серверним фільтром на сторінці лишаються лише вони). */
export function mergeVersionOptions(onPage: readonly string[], selected: readonly string[]): readonly string[] {
  return [...new Set([...onPage, ...selected])].sort(compareVersions);
}

/** Різні версії, що є на сторінці, від меншої до більшої. */
export function distinctVersions(items: readonly VersionSource[], periodKey: number | null): readonly string[] {
  const found = new Set<string>();
  for (const item of items) {
    const { version } = resolveTemplateVersion(item, periodKey);
    if (version !== null) found.add(version);
  }
  return [...found].sort(compareVersions);
}

/** Значення з адреси (`versions=1.0,2.3`) → перелік; порожнє й дублі відкидаються. */
export function parseVersionsParam(raw: string | null): readonly string[] {
  if (raw === null) return [];
  return [...new Set(raw.split(',').map((part) => part.trim()).filter((part) => part !== ''))];
}

/** Перелік → значення для адреси; порожній — `null` (параметр прибирається). */
export function versionsToParam(versions: readonly string[]): string | null {
  return versions.length === 0 ? null : versions.join(',');
}

/** Значення з адреси → напрямок; невідоме - без сортування, а не помилка. */
export function parseVersionSort(raw: string | null): VersionSort | null {
  return raw === 'asc' || raw === 'desc' ? raw : null;
}

/** Цикл кліків по заголовку: без сортування → зростання → спадання → без сортування. */
export function nextVersionSort(current: VersionSort | null): VersionSort | null {
  return current === null ? 'asc' : current === 'asc' ? 'desc' : null;
}

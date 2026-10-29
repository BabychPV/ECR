import type { TemplateHeaderGroupDto } from '@/api/types';
import type { LocalizedValue } from '@/shared/ui/LocalizedInput';

/**
 * Дерево груп заголовків колонок таблиці шаблону (до 3 рівнів) — чиста модель редактора.
 * Сервер приймає ВСЕ дерево одним PUT (плоский перелік за кодами), тож редактор тримає чернетку
 * цілком і змінює її чистими функціями.
 */
export const MaxGroupDepth = 3;

export interface GroupDraft {
  readonly code: string;
  readonly parentCode: string | null;
  readonly ordinal: number;
  readonly label: LocalizedValue;
}

export interface GroupRequestItem {
  readonly code: string;
  readonly parentCode: string | null;
  readonly ordinal: number;
  readonly labelL10n: LocalizedValue;
}

export function draftsOf(groups: readonly TemplateHeaderGroupDto[] | undefined): GroupDraft[] {
  const list = groups ?? [];
  const codeById = new Map(list.map((group) => [group.id, group.code] as const));

  return list.map((group) => ({
    code: group.code,
    parentCode: group.parentId === null ? null : (codeById.get(group.parentId) ?? null),
    ordinal: group.ordinal,
    label: { ...(group.labelL10n.values ?? {}) },
  }));
}

export function siblingsOf(drafts: readonly GroupDraft[], parentCode: string | null): GroupDraft[] {
  return drafts.filter((group) => group.parentCode === parentCode).sort((a, b) => a.ordinal - b.ordinal);
}

/** Глибина групи: корінь — 1. */
export function depthOf(drafts: readonly GroupDraft[], code: string): number {
  const byCode = new Map(drafts.map((group) => [group.code, group] as const));
  let depth = 0;
  let current: string | null = code;

  while (current !== null && depth <= MaxGroupDepth) {
    const group = byCode.get(current);

    if (group === undefined) break;
    depth += 1;
    current = group.parentCode;
  }

  return depth;
}

/** Порядок показу: обхід у глибину, сусіди за `ordinal`. */
export function flatten(drafts: readonly GroupDraft[]): { group: GroupDraft; depth: number }[] {
  const result: { group: GroupDraft; depth: number }[] = [];
  const walk = (parent: string | null, depth: number): void => {
    for (const group of siblingsOf(drafts, parent)) {
      result.push({ group, depth });
      walk(group.code, depth + 1);
    }
  };

  walk(null, 1);

  return result;
}

/** Новий код `G<n>`, якого ще немає. */
export function nextCode(drafts: readonly GroupDraft[]): string {
  const taken = new Set(drafts.map((group) => group.code));
  let n = drafts.length + 1;

  while (taken.has(`G${String(n)}`)) n += 1;

  return `G${String(n)}`;
}

export function addGroup(drafts: readonly GroupDraft[], parentCode: string | null): GroupDraft[] {
  if (parentCode !== null && depthOf(drafts, parentCode) >= MaxGroupDepth) return [...drafts];

  const ordinal = siblingsOf(drafts, parentCode).length;

  return [...drafts, { code: nextCode(drafts), parentCode, ordinal, label: {} }];
}

/** Прибирає групу разом із підгрупами й перенумеровує сусідів. */
export function removeGroup(drafts: readonly GroupDraft[], code: string): GroupDraft[] {
  const doomed = new Set<string>([code]);
  let grew = true;

  while (grew) {
    grew = false;
    for (const group of drafts) {
      if (group.parentCode !== null && doomed.has(group.parentCode) && !doomed.has(group.code)) {
        doomed.add(group.code);
        grew = true;
      }
    }
  }

  return renumber(drafts.filter((group) => !doomed.has(group.code)));
}

export function moveGroup(drafts: readonly GroupDraft[], code: string, delta: -1 | 1): GroupDraft[] {
  const target = drafts.find((group) => group.code === code);

  if (target === undefined) return [...drafts];

  const siblings = siblingsOf(drafts, target.parentCode);
  const from = siblings.findIndex((group) => group.code === code);
  const to = from + delta;

  if (to < 0 || to >= siblings.length) return [...drafts];

  const order = siblings.map((group) => group.code);
  [order[from], order[to]] = [order[to] as string, order[from] as string];

  return drafts.map((group) => (group.parentCode === target.parentCode ? { ...group, ordinal: order.indexOf(group.code) } : group));
}

export function relabel(drafts: readonly GroupDraft[], code: string, label: LocalizedValue): GroupDraft[] {
  return drafts.map((group) => (group.code === code ? { ...group, label } : group));
}

function renumber(drafts: readonly GroupDraft[]): GroupDraft[] {
  const parents = new Set(drafts.map((group) => group.parentCode));
  const ordinalOf = new Map<string, number>();

  for (const parent of parents) {
    siblingsOf(drafts, parent).forEach((group, index) => ordinalOf.set(group.code, index));
  }

  return drafts.map((group) => ({ ...group, ordinal: ordinalOf.get(group.code) ?? group.ordinal }));
}

export function hasText(label: LocalizedValue): boolean {
  return Object.values(label).some((value) => value.trim().length > 0);
}

/** Кожна група має назву хоч однією мовою (вимога сервера). */
export function isValid(drafts: readonly GroupDraft[]): boolean {
  return drafts.every((group) => hasText(group.label));
}

export function toRequest(drafts: readonly GroupDraft[]): GroupRequestItem[] {
  return flatten(drafts).map(({ group }) => ({
    code: group.code,
    parentCode: group.parentCode,
    ordinal: group.ordinal,
    labelL10n: Object.fromEntries(Object.entries(group.label).filter(([, value]) => value.trim().length > 0)),
  }));
}

export interface PreviewHeaderCell {
  /** Підпис групи; `null` — порожня клітинка (колонка без групи на цьому рівні). */
  readonly label: string | null;
  readonly span: number;
}

/**
 * Рядки групових заголовків для перегляду таблиці: за кожну колонку (у порядку показу) — id її
 * групи; рівень 0 — верхній. Сусідні колонки однієї групи зливаються в одну клітинку (`span`).
 */
export function previewHeaderRows(
  groups: readonly TemplateHeaderGroupDto[] | undefined,
  columnGroupIds: readonly (number | null | undefined)[],
  labelOf: (group: TemplateHeaderGroupDto) => string,
): PreviewHeaderCell[][] {
  const byId = new Map((groups ?? []).map((group) => [group.id, group] as const));
  const pathOf = (id: number | null | undefined): TemplateHeaderGroupDto[] => {
    const path: TemplateHeaderGroupDto[] = [];
    let current = id ?? null;

    while (current !== null && path.length < MaxGroupDepth) {
      const group = byId.get(current);

      if (group === undefined) break;
      path.unshift(group);
      current = group.parentId;
    }

    return path;
  };
  const paths = columnGroupIds.map(pathOf);
  const depth = paths.reduce((max, path) => Math.max(max, path.length), 0);
  const rows: PreviewHeaderCell[][] = [];

  for (let level = 0; level < depth; level += 1) {
    const cells: { id: number | null; label: string | null; span: number }[] = [];

    for (const path of paths) {
      const group = path[level];
      const id = group?.id ?? null;
      const last = cells[cells.length - 1];

      // Однаковий id = та сама група (id унікальний), тож і предки ті самі.
      if (last !== undefined && last.id === id) last.span += 1;
      else cells.push({ id, label: group === undefined ? null : labelOf(group), span: 1 });
    }

    rows.push(cells.map(({ label, span }) => ({ label, span })));
  }

  return rows;
}
import type { TemplateHeaderGroupDto } from '@/api/types';
import { localized } from '@/shared/i18n/localized';

/**
 * Шлях групи «Батько › Група» (до 3 рівнів) — для колонки «Group» таблиці колонок і підписів діалогу
 * «Edit selected…».
 *
 * ⚠ Окремий модуль, а не експорт `BulkGroupDialog`: діалог вантажиться ліниво (бюджет маршруту D-132), а
 * статичний імпорт помічника з нього затягнув би весь діалог у чанк сторінки.
 */
export function groupPath(groups: readonly TemplateHeaderGroupDto[] | undefined, id: number | null | undefined): string {
  const byId = new Map((groups ?? []).map((group) => [group.id, group] as const));
  const parts: string[] = [];
  let current = id ?? null;

  while (current !== null && parts.length < 3) {
    const group = byId.get(current);
    if (group === undefined) break;
    parts.unshift(localized(group.labelL10n) || group.code);
    current = group.parentId;
  }

  return parts.join(' › ');
}

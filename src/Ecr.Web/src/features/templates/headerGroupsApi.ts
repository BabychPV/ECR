import { apiFetch } from '@/api/client';
import type { GroupRequestItem } from './headerGroupTree';

/** Відповідь заміни дерева груп (`ReplaceHeaderGroupsResponse`). */
export interface ReplaceHeaderGroupsResult {
  readonly presentationRevision: number;
}

/**
 * Замінює ВСЕ дерево груп заголовків колонок таблиці. Презентаційна операція: працює й на
 * опублікованій версії (право `Template.Edit`), піднімає `PresentationRevision`.
 */
export function replaceHeaderGroups(
  templateVersionId: number,
  tableId: number,
  groups: readonly GroupRequestItem[],
): Promise<ReplaceHeaderGroupsResult> {
  return apiFetch<ReplaceHeaderGroupsResult>(
    `/api/v1/template-versions/${String(templateVersionId)}/tables/${String(tableId)}/header-groups`,
    {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ groups }),
    },
  );
}

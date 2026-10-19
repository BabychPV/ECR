import type { PermitType } from '../api/types';
import { findTable, type TypeDraft } from './draftModel';
import { EditorParams, HeadNode } from './editorActions';

/**
 * Вибраний вузол дерева конструктора: `?t=` (код таблиці або `__head`); немає чи невідомий — перша
 * таблиця, а коли таблиць ще немає — шапка. Один розбір на «Structure» і на діалоги (Add column…).
 */
export function selectedNode(params: URLSearchParams, draft: TypeDraft): string {
  const requested = params.get(EditorParams.table);
  return requested === HeadNode || (requested !== null && findTable(draft, requested) !== undefined)
    ? requested
    : (draft.tables[0]?.code ?? HeadNode);
}

/** Код вибраної таблиці для діалогів; `null` — вибрано шапку. */
export function selectedTableCode(params: URLSearchParams, draft: TypeDraft): string | null {
  const node = selectedNode(params, draft);
  return node === HeadNode ? null : node;
}

/** Тип сервера з накладеною робочою копією: те, що бачить людина, а не лише збережене. */
export function typeWithDraft(type: PermitType, draft: TypeDraft): PermitType {
  return { ...type, nameL10n: draft.nameL10n, category: draft.category, rules: [...draft.rules], tables: [...draft.tables] };
}

import { apiFetch } from '@/api/client';
import type { PresentationRevisionResponse } from '@/api/types';
import { ordinalChanges, orderPatch, type Ordered, type OrderedEntity } from './reorder';

async function reorderEntities(
  entityType: OrderedEntity,
  templateVersionId: number,
  items: readonly Ordered[],
  from: number,
  to: number,
): Promise<number | null> {
  const patch = orderPatch(entityType, ordinalChanges(items, from, to));
  if (patch.length === 0) return null;

  const result = await apiFetch<PresentationRevisionResponse>(
    `/api/v1/template-versions/${String(templateVersionId)}/presentation`,
    { method: 'PATCH', body: JSON.stringify(patch) },
  );

  return result.presentationRevision;
}

/**
 * Переставляє колонку таблиці (`ФВ-2.6`) — одним `PATCH …/presentation`
 * (див. `reorder.ts`, чому саме він).
 *
 * @returns Нова ревізія презентаційного шару; `null` — змінювати нічого
 * (порожній патч сервер відхиляє `ECR-TMPL-0422.emptyPatch`, тож його не шлемо).
 */
export function reorderColumns(
  templateVersionId: number,
  columns: readonly Ordered[],
  from: number,
  to: number,
): Promise<number | null> {
  return reorderEntities('ColumnDef', templateVersionId, columns, from, to);
}

/** Переставляє рядок фіксованої таблиці (AN-15) — тим самим `PATCH …/presentation`, `RowDef.Ordinal`. */
export function reorderRows(
  templateVersionId: number,
  rows: readonly Ordered[],
  from: number,
  to: number,
): Promise<number | null> {
  return reorderEntities('RowDef', templateVersionId, rows, from, to);
}

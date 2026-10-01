import { apiFetch } from '@/api/client';
import type { PresentationRevisionResponse } from '@/api/types';
import { columnOrderPatch, ordinalChanges, type Ordered } from './reorder';

/**
 * Переставляє колонку таблиці (`ФВ-2.6`) — одним `PATCH …/presentation`
 * (див. `reorder.ts`, чому саме він).
 *
 * @returns Нова ревізія презентаційного шару; `null` — змінювати нічого
 * (порожній патч сервер відхиляє `ECR-TMPL-0422.emptyPatch`, тож його не шлемо).
 */
export async function reorderColumns(
  templateVersionId: number,
  columns: readonly Ordered[],
  from: number,
  to: number,
): Promise<number | null> {
  const patch = columnOrderPatch(ordinalChanges(columns, from, to));
  if (patch.length === 0) return null;

  const result = await apiFetch<PresentationRevisionResponse>(
    `/api/v1/template-versions/${String(templateVersionId)}/presentation`,
    { method: 'PATCH', body: JSON.stringify(patch) },
  );

  return result.presentationRevision;
}

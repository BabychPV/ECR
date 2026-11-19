import { apiFetch, EcrApiError } from '@/api/client';
import type { components } from '@/api/schema';

/** B80 N5 (D-554): вплив затвердження / повернення аркуша-джерела на залежні документи; тип — зі згенерованої схеми. */
export type SourceImpact = components['schemas']['SourceImpactDto'];

/** Підтвердження впливу, що йде разом із дією (`confirmedImpact`). */
export type ConfirmedImpact = components['schemas']['ConfirmedImpactRequest'];

export type SourceImpactAction = 'approve' | 'reopen';

/**
 * Вплив дії над аркушем-джерелом. Копія аркуша залежних не годує — запиту немає (`null`).
 *
 * ⚠ Збій запиту НЕ блокує дію: сервер однаково вимагає підтвердження (`409`), і клієнт тоді показує діалог за відмовою
 * (`impactRefusal`). Тому помилка тут — `null`, а не виняток.
 */
export async function fetchSourceImpact(
  documentId: number,
  at: { readonly sheetDefId: number; readonly periodKey: number; readonly sheetCopyId: number | null },
  action: SourceImpactAction,
): Promise<SourceImpact | null> {
  if (at.sheetCopyId !== null) return null;

  try {
    const impact = await apiFetch<SourceImpact | undefined>(
      `/api/v1/documents/${String(documentId)}/source-impact?sheetDefId=${String(at.sheetDefId)}&periodKey=${String(at.periodKey)}&action=${action}`,
    );

    return impact !== undefined && typeof impact === 'object' && 'required' in impact ? impact : null;
  } catch {
    return null;
  }
}

/** Відмова сервера через вплив на залежні (`ECR-SRC-0409` / `ECR-SRC-0422`)? Тоді діалог впливу відкривається за відмовою. */
export function impactRefusal(error: unknown): string | null {
  if (!(error instanceof EcrApiError)) return null;
  const code = error.problem.errorCode;

  return code === 'ECR-SRC-0409' || code === 'ECR-SRC-0422' ? code : null;
}

import { apiFetch, EcrApiError } from '@/api/client';
import type { components } from '@/api/schema';

/** B80 N5 (D-554): вплив затвердження / повернення аркуша-джерела на залежні документи; тип — зі згенерованої схеми. */
export type SourceImpact = components['schemas']['SourceImpactDto'];

/** Підтвердження впливу, що йде разом із дією (`confirmedImpact`). */
export type ConfirmedImpact = Omit<components['schemas']['ConfirmedImpactRequest'], 'recalculateApproved'> & {
  /** ✎ Рішення 19.11: «й затверджені» (право на сервері); немає — як без галочки. */
  readonly recalculateApproved?: boolean;
};

export type SourceImpactAction = 'approve' | 'reopen' | 'edit';

/** Аркуші-джерела, для яких людина вже відмовилась від нагадування про правку в цій сесії (`документ:аркуш`). */
const suppressedEditNotices = new Set<string>();

/** ✎ Рішення 19.11: «не питати до кінця сесії для цього аркуша» — сесія = життя сторінки. */
export function suppressEditNotice(documentId: number, sheetDefId: number): void {
  suppressedEditNotices.add(`${String(documentId)}:${String(sheetDefId)}`);
}

/** Чи людина вже відмовилась від нагадування про правку цього аркуша в цій сесії. */
export function isEditNoticeSuppressed(documentId: number, sheetDefId: number): boolean {
  return suppressedEditNotices.has(`${String(documentId)}:${String(sheetDefId)}`);
}

/** Лише для тестів: скидає відмови від нагадувань. */
export function resetEditNoticeSuppression(): void {
  suppressedEditNotices.clear();
}

/** Відповідь `POST …/source-impact/recalculate-approved`; тип — зі згенерованої схеми. */
export type RecalculateApprovedResult = components['schemas']['RecalculateApprovedDependentsResult'];

/**
 * ✎ Рішення 19.11: адміністратор явно просить перерахувати й ЗАТВЕРДЖЕНІ залежні (після збереження правок). Право, причину й
 * відбиток перевіряє сервер; відмова — виняток `EcrApiError` (403 / 422 / 409).
 */
export function recalculateApprovedDependents(
  documentId: number,
  at: { readonly sheetDefId: number; readonly periodKey: number },
  hash: string,
  reason: string,
): Promise<RecalculateApprovedResult> {
  return apiFetch<RecalculateApprovedResult>(
    `/api/v1/documents/${String(documentId)}/source-impact/recalculate-approved`,
    { method: 'POST', body: JSON.stringify({ sheetDefId: at.sheetDefId, periodKey: at.periodKey, hash, reason }) },
  );
}

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

/**
 * ✎ D-5: місяці (періоди-власники), яких стосуються незбережені правки. Річна сторінка пише правки в зріз ПЕРІОДУ СТОРІНКИ
 * (якір, 202601), а місяць кодується КОЛОНКОЮ (`M05`): сервер маршрутизує комірку місячної колонки в період цього місяця
 * (`PatchCellsHandler.MonthRouting`), решту — у якір. Тут те саме правило, що й у чипа стану місяця в сітці
 * (`monthColumnsOf`): колонка, яку `submission-target.monthColumns` знає, дає свій `periodKey`; інша — період зрізу.
 *
 * ⚠ Зріз правок не знає `tableDefId` (лише `tableInstanceId`), тож колонка шукається за кодом серед місячних колонок аркуша.
 */
export function editedPeriods(
  slices: readonly { readonly periodKey: number; readonly edits: readonly { readonly columnCode: string }[] }[],
  monthColumns: readonly { readonly columnCode: string; readonly periodKey: number }[] | undefined,
): number[] {
  const owner = new Map<string, number>();
  for (const month of monthColumns ?? []) owner.set(month.columnCode, month.periodKey);

  const periods = new Set<number>();
  for (const slice of slices) {
    for (const edit of slice.edits) periods.add(owner.get(edit.columnCode) ?? slice.periodKey);
  }

  return [...periods];
}

/** Відмова сервера через вплив на залежні (`ECR-SRC-0409` / `ECR-SRC-0422`)? Тоді діалог впливу відкривається за відмовою. */
export function impactRefusal(error: unknown): string | null {
  if (!(error instanceof EcrApiError)) return null;
  const code = error.problem.errorCode;

  return code === 'ECR-SRC-0409' || code === 'ECR-SRC-0422' ? code : null;
}

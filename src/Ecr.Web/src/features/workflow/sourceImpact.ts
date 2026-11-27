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
  at: { readonly sheetDefId: number; readonly periodKey: number; readonly months?: readonly number[] | undefined },
  hash: string,
  reason: string,
): Promise<RecalculateApprovedResult> {
  return apiFetch<RecalculateApprovedResult>(
    `/api/v1/documents/${String(documentId)}/source-impact/recalculate-approved`,
    {
      method: 'POST',
      // ✎ Місяці правки — ті самі, з якими людина бачила вплив (інакше відбиток не збігся б).
      body: JSON.stringify({ sheetDefId: at.sheetDefId, periodKey: at.periodKey, hash, reason, ...(at.months === undefined ? {} : { months: at.months }) }),
    },
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
  at: {
    readonly sheetDefId: number;
    readonly periodKey: number;
    readonly sheetCopyId: number | null;
    /** ✎ Місяці (1–12) місячних колонок правки — лише для `edit`; немає — сервер вважає увесь рік. */
    readonly months?: readonly number[] | undefined;
  },
  action: SourceImpactAction,
): Promise<SourceImpact | null> {
  if (at.sheetCopyId !== null) return null;

  const months = action === 'edit' && at.months !== undefined ? at.months.map((m) => `&months=${String(m)}`).join('') : '';
  try {
    const impact = await apiFetch<SourceImpact | undefined>(
      `/api/v1/documents/${String(documentId)}/source-impact?sheetDefId=${String(at.sheetDefId)}&periodKey=${String(at.periodKey)}&action=${action}${months}`,
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

/**
 * ✎ Місяці (1–12) незбережених правок за МІСЯЧНИМИ колонками аркуша (`submission-target.monthColumnCodes` — факт шаблону, є й у
 * річному проєкті HSE400, де `monthColumns` порожні). Хоч одна правка не в місячній колонці (річне значення, код, рядок) —
 * `undefined`: місяці правки невідомі, сервер покаже увесь рік — так само, як його порахує каскад (D-572).
 */
export function editedMonths(
  slices: readonly { readonly edits: readonly { readonly columnCode: string }[] }[],
  monthColumnCodes: readonly { readonly columnCode: string; readonly monthNumber: number }[] | null | undefined,
): number[] | undefined {
  const monthOf = new Map<string, number>();
  for (const column of monthColumnCodes ?? []) monthOf.set(column.columnCode, column.monthNumber);

  const months = new Set<number>();
  for (const slice of slices) {
    for (const edit of slice.edits) {
      const month = monthOf.get(edit.columnCode);
      if (month === undefined) return undefined;
      months.add(month);
    }
  }

  return months.size > 0 ? [...months].sort((a, b) => a - b) : undefined;
}

/** Відмова сервера через вплив на залежні (`ECR-SRC-0409` / `ECR-SRC-0422`)? Тоді діалог впливу відкривається за відмовою. */
export function impactRefusal(error: unknown): string | null {
  if (!(error instanceof EcrApiError)) return null;
  const code = error.problem.errorCode;

  return code === 'ECR-SRC-0409' || code === 'ECR-SRC-0422' ? code : null;
}

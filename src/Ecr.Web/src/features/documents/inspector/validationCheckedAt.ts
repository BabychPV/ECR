import type { ValidationResultResponse } from '@/api/types';

/**
 * ✎ UAT 08.11 (d4, F13-p209): КОЛИ була показана перевірка — для порожнього переліку Issues.
 *
 * Свіжий прогін у цій вкладці — момент із відповіді сервера (`validatedAt`), інакше момент завершення запиту.
 * Збережений результат — `validatedAt` з `GET …/validation` (раніше сервер його не віддавав, і порожній перелік
 * казав «нічого не знайдено», не кажучи коли). `validated: false` — перевірки не було, моменту немає.
 */
export function validationCheckedAt(
  fresh: { readonly result: ValidationResultResponse; readonly at: Date } | null,
  stored: ValidationResultResponse | undefined,
): Date | null {
  if (fresh !== null) return dateOf(fresh.result.validatedAt) ?? fresh.at;
  if (stored === undefined || !stored.validated) return null;

  return dateOf(stored.validatedAt);
}

function dateOf(value: string | null | undefined): Date | null {
  if (value === null || value === undefined || value === '') return null;
  const date = new Date(value);

  return Number.isNaN(date.getTime()) ? null : date;
}

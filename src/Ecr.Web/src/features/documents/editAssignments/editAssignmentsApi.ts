import { useMutation, useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { invalidateSlices } from '@/features/grid/sliceCache';
import { formatMonthName, formatMonthYear } from '@/shared/format';
import { t } from '@/shared/i18n';

/**
 * Передача місяця виконавцю: клієнтський бік виконавця (EA-5b, `DESIGN-OWNER-EDIT-GRANTS-2026-10-11.md` §6, §8).
 *
 * ⚠ Типи описані ТУТ, а не взяті з `schema.d.ts`: контракт сервера (EA-3/EA-4) ще не в `dev/integration`.
 * Форма — рівно з §6 проєкту; коли контракт з'явиться, ці інтерфейси замінюються на `components['schemas']`
 * (`D-137`), а решта файлу не змінюється.
 */

/** Стан виконавця у передачі (`EditAssignmentRecipient.Status`). */
export type RecipientStatus = 'Assigned' | 'Filled' | 'Returned';

/**
 * Зріз передачі: усі селектори необов'язкові, жодного = «весь документ» (§2.2).
 * `monthNumber` - колонка-місяць річної таблиці; `periodKey` - період (Рік*100+№).
 */
export interface EditAssignmentSlice {
  readonly monthNumber?: number | null;
  readonly periodKey?: number | null;
  readonly sheetCode?: string | null;
  readonly tableCode?: string | null;
}

/** Рядок `GET /api/v1/me/edit-assignments?role=assignee`: лише рядки викликача, без імен інших виконавців. */
export interface MyEditAssignment {
  readonly id: number;
  readonly documentId: number;
  readonly slice: EditAssignmentSlice;
  readonly status: RecipientStatus;
  /** Похідний стан зрізу: `Validated` - власник валідував, виконавець уже нічого не змінить. */
  readonly validationStatus?: 'NotValidated' | 'Validated' | null;
  /** Інструкція власника - звичайний текст (≤ 500). */
  readonly note?: string | null;
  /** Коментар повернення (≤ 500) - лише для `Returned`. */
  readonly returnReason?: string | null;
}

/**
 * ⚠ ТИМЧАСОВО рукописна форма (ESLint D-137 ловить суфікси `Response`/`Dto`, тому назва інша): контракт EA-3/EA-4
 * ще не в `schema.d.ts`. Коли `MyEditAssignmentDto` з'явиться в схемі - замінити цей і сусідні інтерфейси
 * псевдонімами `components['schemas']`, інакше розбіжність з сервером ловитиме лише рецензент.
 */
export interface MyEditAssignmentList {
  readonly items: readonly MyEditAssignment[];
}

/** Адреса власних передач виконавця в документі. */
export function myEditAssignmentsUrl(documentId: number): string {
  return `/api/v1/me/edit-assignments?role=assignee&documentId=${String(documentId)}`;
}

/** Адреса галочки «Заповнено». */
export function fillEditAssignmentUrl(documentId: number, assignmentId: number): string {
  return `/api/v1/documents/${String(documentId)}/edit-assignments/${String(assignmentId)}/fill`;
}

/** Ключі кешу домену (рядкові літерали в компонентах заборонені - `queryKeys.ts`). */
export const editAssignmentKeys = {
  mine: (documentId: number) => ['edit-assignments', 'mine', documentId] as const,
};

/**
 * Власні передачі виконавця в документі.
 *
 * ⚠ Помилка запиту НЕ показується смугою: смуга - підказка, а не шлях запису. Права на комірки
 * однаково рішає сервер (`cellPermissions`), тож збій цього запиту лише ховає пояснення.
 */
export function useMyEditAssignments(documentId: number): UseQueryResult<MyEditAssignmentList> {
  return useQuery({
    queryKey: editAssignmentKeys.mine(documentId),
    queryFn: () => apiFetch<MyEditAssignmentList>(myEditAssignmentsUrl(documentId)),
    // Стан змінює інша людина (власник повертає чи валідує): без перезапиту на фокус смуга брехала б до F5.
    refetchOnWindowFocus: true,
    staleTime: 30_000,
  });
}

/**
 * Галочка «Заповнено» (`POST …/{id}/fill`, лише сам виконавець, ідемпотентна).
 * Після успіху перечитуються власні передачі й зрізи документа: `cellPermissions` стали `AssignmentFilled`.
 */
export function useFillEditAssignment(documentId: number, periodKey: number) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (assignmentId: number) =>
      apiFetch<unknown>(fillEditAssignmentUrl(documentId, assignmentId), { method: 'POST', body: JSON.stringify({}) }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: editAssignmentKeys.mine(documentId) });
      await invalidateSlices(queryClient, { documentId, periodKey });
    },
  });
}

/**
 * Людська назва зрізу для смуги: «January», «January 2026», «Period 202601», «Sheet F1», «Whole document».
 *
 * ⚠ `periodKey` розкладається на рік і місяць лише для місячного проєкту: для квартального «№» - це квартал,
 * і «Квартал 1» як «Січень» був би брехнею.
 */
export function sliceLabel(slice: EditAssignmentSlice, periodKind: string | undefined): string {
  const month = slice.monthNumber ?? null;
  const period = slice.periodKey ?? null;
  const sheet = slice.sheetCode ?? null;
  const table = slice.tableCode ?? null;

  const parts: string[] = [];

  if (month !== null) {
    parts.push(formatMonthName(month));
  } else if (period !== null) {
    const monthly = periodKind === 'Monthly' ? formatMonthYear(Math.floor(period / 100), period % 100) : '';
    parts.push(monthly !== '' ? monthly : t('editAssign.slice.period', { period }));
  }

  if (sheet !== null) parts.push(table !== null ? `${sheet} / ${table}` : sheet);

  return parts.length === 0 ? t('editAssign.slice.all') : parts.join(', ');
}

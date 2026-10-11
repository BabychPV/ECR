import { formatMonthName } from '@/shared/format';
import { formatPeriodKey } from '@/shared/format/periodKey';
import { t } from '@/shared/i18n';
import type { AssignmentSlice, EditAssignment, MyEditAssignmentsView } from './types';

/** Зріз із усіма чотирма селекторами (схема вимагає всі ключі; `null` = не обмежено). */
export function makeSlice(parts: Partial<AssignmentSlice> = {}): AssignmentSlice {
  return { sheetCode: null, tableCode: null, monthNumber: null, periodKey: null, ...parts };
}

/** Однакові зрізи: порівняння по чотирьох селекторах, `undefined` ≡ `null`. */
export function sameSlice(a: AssignmentSlice, b: AssignmentSlice): boolean {
  return (
    (a.sheetCode ?? null) === (b.sheetCode ?? null)
    && (a.tableCode ?? null) === (b.tableCode ?? null)
    && (a.monthNumber ?? null) === (b.monthNumber ?? null)
    && (a.periodKey ?? null) === (b.periodKey ?? null)
  );
}

/** Підпис зрізу: «January», «January · sheet AIR», «Whole document». */
export function sliceLabel(slice: AssignmentSlice): string {
  const parts: string[] = [];

  if (slice.monthNumber != null) parts.push(formatMonthName(slice.monthNumber));
  if (slice.periodKey != null) parts.push(formatPeriodKey(slice.periodKey) || String(slice.periodKey));
  if (slice.sheetCode != null) parts.push(t('editAssign.sliceSheet', { sheet: slice.sheetCode }));
  if (slice.tableCode != null) parts.push(t('editAssign.sliceTable', { table: slice.tableCode }));

  return parts.length === 0 ? t('editAssign.sliceAll') : parts.join(' · ');
}

/** Порядок рядків: за місяцем/періодом, далі за кодами. */
export function compareAssignments(a: EditAssignment, b: EditAssignment): number {
  const key = (x: EditAssignment): number => x.slice.periodKey ?? (x.slice.monthNumber ?? 0);

  return key(a) - key(b) || sliceLabel(a.slice).localeCompare(sliceLabel(b.slice));
}

/** Наступний місяць того ж зрізу; `null` — зріз не місячний або це грудень (рік змінює інша дія). */
export function nextMonthSlice(slice: AssignmentSlice): AssignmentSlice | null {
  if (slice.monthNumber == null || slice.monthNumber >= 12 || slice.periodKey != null) return null;

  return { ...slice, monthNumber: slice.monthNumber + 1 };
}

/** Кількість передач, що чекають валідації власника (чіп «Awaiting validation»). */
export function awaitingValidationCount(assignments: readonly EditAssignment[]): number {
  return assignments.filter((a) => a.status === 'Filled').length;
}

/**
 * Адреса документа передачі (для «My tasks»).
 *
 * ⛔ Літерал, а не `generatePath` (`features` не імпортують `app`, як `myTaskDocumentHref`).
 * Для «Validate…» - шторка передач документа (`?panel=edit-assignments`, DESIGN §8).
 */
export function editAssignmentHref(documentId: number, validate: boolean): string {
  const base = `/documents/${String(documentId)}`;

  return validate ? `${base}?panel=edit-assignments` : base;
}

/** Скільки рядків у «My tasks» вимагають дії (виконати + провалідувати): окремий від «активних задач» індикатор. */
export function pendingEditAssignmentCount(data: MyEditAssignmentsView | undefined): number {
  return (data?.assignedToMe.length ?? 0) + (data?.awaitingMyValidation.length ?? 0);
}

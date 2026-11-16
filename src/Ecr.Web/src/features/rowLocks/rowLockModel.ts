import type { CreateRowLocksRequest, RowLockDto } from '@/features/documents/rowLocks/api';
import { formatDate } from '@/shared/format/datetime';
import { parseDateOnly } from '@/shared/format/dateOnly';
import { t } from '@/shared/i18n';

/**
 * ✎ B139 (B75, клієнт): чиста логіка блокувань рядків — без рендера, щоб правила перевірялись дешево.
 *
 * ⛔ Клієнт НЕ вирішує, чи можна правити заблокований рядок: це робить сервер (`EditRules`, причина `RowLocked`
 * у `cellPermissions`). Тут лише показ: який рядок заблоковано, ким і до коли, і перевірка форми до запиту.
 */

/** Таблиця, до якої належать рядки (код таблиці унікальний лише в межах аркуша, F13 D2). */
export interface LockTable {
  readonly sheetCode: string;
  readonly tableCode: string;
  /** Копія аркуша; `null` — оригінал. */
  readonly sheetCopyId: number | null;
}

/** Чи належить блокування цій таблиці. ⚠ Старіша відповідь без `sheetCode` — збіг лише за таблицею й копією. */
export function belongsTo(lock: RowLockDto, table: LockTable): boolean {
  return (
    lock.tableCode === table.tableCode &&
    (lock.sheetCode === undefined || lock.sheetCode === table.sheetCode) &&
    (lock.sheetCopyId ?? null) === table.sheetCopyId
  );
}

/**
 * Чинні блокування таблиці за ключем рядка. Сервер віддає лише незняті й не прострочені, але `isActive = false`
 * буває для ще не початого (дата початку в майбутньому) — такий рядок поки правиться, значка «заблоковано» немає.
 */
export function activeLocksByRow(locks: readonly RowLockDto[] | undefined, table: LockTable): ReadonlyMap<string, RowLockDto> {
  const byRow = new Map<string, RowLockDto>();
  for (const lock of locks ?? []) {
    if (lock.isActive && belongsTo(lock, table)) byRow.set(lock.rowKey, lock);
  }

  return byRow;
}

/** Дата `yyyy-MM-dd` людині — мовою інтерфейсу; нерозібрана — як прийшла. */
export function lockDate(value: string): string {
  const parsed = parseDateOnly(value);

  return parsed === null ? value : formatDate(parsed);
}

/** Хто заблокував: ім'я з відповіді, інакше нейтральне «інший користувач» (ідентифікатор людині не показуємо). */
export function lockOwner(lock: RowLockDto): string {
  return lock.ownerDisplayName !== null && lock.ownerDisplayName.trim().length > 0
    ? lock.ownerDisplayName
    : t('rowLock.ownerUnknown');
}

/** Пояснення значка: хто й до якого дня (включно). */
export function lockHint(lock: RowLockDto): string {
  return t('rowLock.badgeHint', { owner: lockOwner(lock), until: lockDate(lock.lockUntil) });
}

/** Стан форми діалогу. */
export interface LockForm {
  readonly rowKeys: readonly string[];
  readonly fromDate: string;
  readonly toDate: string;
  readonly note: string;
}

/** Найдовша примітка (`doc.RowLock.Note nvarchar(400)`); довшу сервер однаково відхилить (`lockNoteTooLong`). */
export const MaxNoteLength = 400;

/**
 * Ключ тексту першої помилки форми; `null` — форму можна надсилати.
 *
 * ⚠ Лише те, що видно без сервера: порожні рядки чи дати й кінець раніше за початок. «У минулому» і «довше
 * 10 років» рахує сервер у часовому поясі ПРОЄКТУ (`lockRangeInvalid`) — клієнт його не знає і не вгадує.
 */
export function lockFormError(form: LockForm): string | null {
  if (form.rowKeys.length === 0) return 'rowLock.errorNoRows';
  if (parseDateOnly(form.fromDate) === null || parseDateOnly(form.toDate) === null) return 'rowLock.errorDates';
  // `yyyy-MM-dd` порівнюється рядком так само, як датою.
  if (form.toDate < form.fromDate) return 'rowLock.errorRange';
  if (form.note.length > MaxNoteLength) return 'rowLock.errorNote';

  return null;
}

/** Тіло `POST …/row-locks` із форми (порожня примітка — `null`). */
export function lockRequest(form: LockForm, table: LockTable, periodKey: number): CreateRowLocksRequest {
  const note = form.note.trim();

  return {
    periodKey,
    sheetCode: table.sheetCode,
    tableCode: table.tableCode,
    sheetCopyId: table.sheetCopyId,
    rowKeys: [...form.rowKeys],
    fromDate: form.fromDate,
    toDate: form.toDate,
    note: note.length === 0 ? null : note,
  };
}

/**
 * Рядки, на які діє «Заблокувати рядки…»: виділений діапазон сітки, інакше рядок фокусної комірки.
 * ⚠ Рядок підсумків (індекс поза `rows`) не потрапляє: його немає в даних.
 */
export function pickedRowKeys(
  rows: readonly { readonly rowKey: string }[],
  range: { readonly fromRow: number; readonly toRow: number } | null,
  focusedRow: number | null,
): string[] {
  const from = range?.fromRow ?? focusedRow;
  const to = range?.toRow ?? focusedRow;
  if (from === null || to === null) return [];

  const keys: string[] = [];
  for (let index = Math.max(0, from); index <= Math.min(to, rows.length - 1); index += 1) {
    const row = rows[index];
    if (row !== undefined) keys.push(row.rowKey);
  }

  return keys;
}

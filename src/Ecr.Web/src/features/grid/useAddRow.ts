import { useMutation } from '@tanstack/react-query';
import { apiFetch, EcrApiError } from '@/api/client';
import type { CreateRowRequest } from '@/api/types';
import { hasText, t } from '@/shared/i18n';
import { showApiError, showErrorText } from '@/shared/ui/notify';
import { denyText } from './permissions';

/**
 * Текст відмови додавання рядка за правом (`403 ECR-ACCS-0403.addRowDenied`) — причина мовою
 * інтерфейсу, а не сирим кодом («InsufficientGrantLevel», D9 приймальної Land 12.10).
 *
 * ⚠ Сервер кладе в `reason` лише назву причини (`EditDenyReason`); текст бере каталог за тим самим
 * `deny.<Причина>`, що й підказка сірої комірки (`denyText`). Невідома причина чи немає ключа — `null`,
 * і відмову показує звичайний `showApiError`.
 */
export function addRowDenialText(error: unknown): string | null {
  if (!(error instanceof EcrApiError) || error.problem.errorCode !== 'ECR-ACCS-0403') return null;

  const extensions = error.problem.extensions2 ?? {};
  const reason = extensions['reason'];
  if (extensions['messageKey'] !== 'err.ECR-ACCS-0403.addRowDenied' || typeof reason !== 'string') return null;
  if (!hasText('err.ECR-ACCS-0403.addRowDenied')) return null;

  const text = denyText(reason);
  if (text === null) return null;

  return t('err.ECR-ACCS-0403.addRowDenied', { reason: text.replace(/\.+$/, '') });
}

function showAddRowError(error: unknown): void {
  const text = addRowDenialText(error);
  if (text === null) {
    showApiError(error);
    return;
  }

  showErrorText(text);
}

/** Скільки разів додавання рядка повторюється саме після минущої зайнятості (Y7-03). */
export const AddRowBusyRetries = 2;

/**
 * Чи повторювати додавання рядка (Y7-03, клієнтська частина).
 *
 * ⛔ Додавання рядка бере спільне блокування аркуша, як і збереження комірок: поки аркуш подається,
 * сервер відповідає `409 ECR-DOC-4091 sheetBeingSubmitted` і НІЧОГО не записує. Для комірок такий збій
 * повторює автозбереження (`scheduleBusyRetry`), а кнопка «Додати рядок» лишалась без повтору: людина бачила
 * червоний тост на відмову, яка минає сама за секунди.
 *
 * ⚠ Лише `isTransientBusy` (нічого не записано, повтор безпечний): `ECR-ROW-0409` (стеля рядків, дублікат
 * ключа) і решта 4xx повторенням не вилікуються й не повторюються.
 */
export function shouldRetryAddRow(failureCount: number, error: unknown): boolean {
  return error instanceof EcrApiError && error.isTransientBusy && failureCount < AddRowBusyRetries;
}

/** Відступ перед повтором: не менший за `Retry-After` сервера, інакше 1 с, 2 с. */
export function addRowRetryDelayMs(failureCount: number, error: unknown): number {
  const advised = error instanceof EcrApiError ? (error.problem.retryAfterSeconds ?? 0) * 1000 : 0;

  return Math.max(advised, 1000 * 2 ** failureCount);
}

/**
 * Додавання рядка динамічної таблиці (`ФВ-3.2`).
 *
 * ⛔ До аудиту цієї дії в інтерфейсі не було зовсім: ендпоінт існував і
 * працював, а динамічна таблиця лишалася порожньою назавжди — рядок у неї
 * не міг додати ніхто (`A7-39`).
 *
 * ⚠ Ключ не задається: сервер видає GUID у форматі `N`. Просити ключ у
 * користувача означало б віддати йому ідентичність рядка, на яку
 * посилаються формули й аудит.
 */
export function useAddRow(documentId: number, tableInstanceId: number, onAdded: () => void) {
  return useMutation({
    mutationFn: () =>
      apiFetch(`/api/v1/documents/${documentId}/rows`, {
        method: 'POST',
        body: JSON.stringify({ tableInstanceId, rowKey: null } satisfies CreateRowRequest),
      }),
    onSuccess: onAdded,
    retry: shouldRetryAddRow,
    retryDelay: addRowRetryDelayMs,

    // ⚠ Стеля рядків і дублікат ключа приходять як `ECR-ROW-0409` з числом у
    // тексті: «досягнуто межу динамічних рядків таблиці: 200». Це те, що
    // людина може зрозуміти й погодити, а «не вдалося» — ні.
    onError: showAddRowError,
  });
}

import { notifications } from '@mantine/notifications';
import { EcrApiError } from '@/api/client';
import { hasText, t } from '@/shared/i18n';
import { notificationCloseButtonProps, showApiError } from '@/shared/ui/notify';

/**
 * Відмови робочого процесу, що називають аркуш (`ECR-ACCS-0403`).
 *
 * ⛔ A2-08 (= A1-10, приймальна №2): сервер складає ці речення з каталогу і
 * підставляє в `{sheetDefId}` ЧИСЛО — «Sheet 2 cannot be approved…». Назви
 * аркуша мовою користувача в обробнику немає (лише id), і тягнути її туди
 * означало б новий порт у трьох обробниках. Екран же назву має — тож речення
 * перескладається тут із ТОГО САМОГО рядка каталогу (`messageKey`), з тими
 * самими підстановками, лише замість id — назва. Сервер і контракт не
 * змінюються; споживач API і далі отримує id.
 */
const SheetDenialKeys: ReadonlySet<string> = new Set([
  'err.ECR-ACCS-0403.submitDenied',
  'err.ECR-ACCS-0403.approveDenied',
  'err.ECR-ACCS-0403.approveOwnSubmission',
  'err.ECR-ACCS-0403.reopenDenied',
]);

/**
 * Текст відмови з назвою аркуша; `null` — це інша відмова (або назви немає),
 * і її показує звичайний `showApiError`.
 *
 * ⚠ Причина — як у сервера (`UiStringResolver.WithLocalizedReason`): рядок
 * каталогу за `reasonKey` без кінцевої крапки, інакше — сирий `reason`.
 *
 * @param error Відмова з мутації.
 * @param sheetName Назва аркуша мовою інтерфейсу.
 */
export function sheetDenialText(error: unknown, sheetName: string | null | undefined): string | null {
  if (!(error instanceof EcrApiError) || error.problem.errorCode !== 'ECR-ACCS-0403') return null;

  const name = sheetName?.trim() ?? '';
  if (name.length === 0) return null;

  const extensions = error.problem.extensions2 ?? {};
  const key = extensions['messageKey'];
  if (typeof key !== 'string' || !SheetDenialKeys.has(key) || !hasText(key)) return null;

  const params: Record<string, string> = {};
  for (const [param, value] of Object.entries(extensions)) {
    if (typeof value === 'string') params[param] = value;
  }

  const reasonKey = extensions['reasonKey'];
  if (typeof reasonKey === 'string' && hasText(reasonKey)) {
    params['reason'] = t(reasonKey).replace(/\.+$/, '');
  }

  params['sheetDefId'] = `«${name}»`;

  return t(key, params);
}

/**
 * Показує відмову дії над аркушем: з назвою, якщо це відмова про аркуш,
 * інакше — як `showApiError`.
 *
 * @param error Відмова з мутації.
 * @param sheetName Назва аркуша мовою інтерфейсу.
 */
export function showSheetError(error: unknown, sheetName: string | null | undefined): void {
  const text = sheetDenialText(error, sheetName);
  if (text === null) {
    showApiError(error);
    return;
  }

  notifications.show({
    color: 'statusError',
    message: text,
    closeButtonProps: notificationCloseButtonProps,
  });
}

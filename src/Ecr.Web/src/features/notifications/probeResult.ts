import { notifications } from '@mantine/notifications';
import type { components } from '@/api/schema';
import { t } from '@/shared/i18n';
import { notificationCloseButtonProps, showDone } from '@/shared/ui/notify';

type NotificationTestResult = components['schemas']['NotificationTestResult'];

/**
 * Підсумок проби каналу чи SMTP (`D-263`): успіх — підтвердження, відмова — категорія з каталогу.
 *
 * ⛔ Не `showApiError(new Error(...))`: той показує лише `EcrApiError`, а будь-який інший виняток
 * зводить до голого «Помилка» — категорія відмови транспорту (`notifications.test.smtp.dns` …)
 * губилася рівно там, де людина її чекала.
 *
 * ⚠ Сирий `error` сервера (текст винятку транспорту, не перекладений) на екран не йде: категорія
 * невідома — загальне «канал відхилив».
 */
export function showProbeResult(result: NotificationTestResult): void {
  if (result.ok) {
    showDone(t('notifications.testOk'));
    return;
  }

  const key = result.messageKey ?? null;
  notifications.show({
    color: 'statusError',
    message: key === null ? t('notifications.testFailed') : t(key),
    closeButtonProps: notificationCloseButtonProps,
  });
}

import type { JSX } from 'react';
import { Text } from '@mantine/core';
import { t } from '@/shared/i18n';
import type { NotificationChannel } from './api';

/**
 * Звідки поштовий канал бере транспорт і чи є йому чим доставляти — рівно так,
 * як це сказав сервер.
 *
 * ⛔ Сервер віддає ДВІ незалежні ознаки:
 *   - `transportFromConfiguration` (`c3652cee`) — звідки транспорт: для пошти
 *     завжди `true` (сервер, порт, TLS, відправник беруться з конфігурації
 *     процесу, а не з полів каналу);
 *   - `transportConfigured` (`208fb92e`) — чи є каналу ЧИМ доставляти: для
 *     пошти — чи налаштовано SMTP-транспорт процесу (`Smtp:Host` не
 *     порожній), для Teams — те саме, що `hasSecret`.
 * Жодного значення транспорту (хоста, порту, TLS, відправника) у відповіді
 * немає. Тому тут немає й «smtp.corp:587»: такий рядок був би вигаданий
 * клієнтом, а не взятий із сервера.
 *
 * ⚠ Раніше попередження показувалося за `transportFromConfiguration === false`
 * — а сервер для пошти цього ЗНАЧЕННЯ не віддає ніколи (воно завжди `true`),
 * тож попередження було мертвим кодом: жодна відповідь сервера його не
 * вмикала. `transportConfigured` — ознака саме про це: чи налаштовано сам
 * транспорт, незалежно від того, звідки він (завжди з конфігурації).
 *
 * ⚠ Дві ознаки незалежні й можуть бути на екрані РАЗОМ: канал бере транспорт
 * із налаштувань застосунку (`transportFromConfiguration: true`) і водночас
 * не має його налаштованим (`transportConfigured: false`, порожній
 * `Smtp:Host`) — обидва рядки тоді показані одночасно, бо обидва факти
 * істинні.
 *
 * ⚠ Рядки — наявні в каталозі (`smtpTransportHint`, `test.smtpNotConfigured`):
 * вони кажуть рівно це, а новий ключ без рядка в `09-seed.sql` червонить
 * `EndpointCoverageTests` і гейт `a11y` (на екрані був би `⟦…⟧`).
 *
 * ⚠ Для Teams тут нічого: попередження про відсутню адресу вебхука вже дає
 * сусідня колонка (`notifications.webhookMissing`, `ChannelsPanel.tsx`) —
 * рівно та сама ознака (`transportConfigured === hasSecret` для Teams), і
 * дублювати її тут означало б два попередження про один факт.
 */
export function TransportSource({
  channel,
}: {
  readonly channel: Pick<NotificationChannel, 'kind' | 'transportFromConfiguration' | 'transportConfigured'>;
}): JSX.Element | null {
  if (channel.kind !== 'Smtp') return null;

  const hint = channel.transportFromConfiguration && (
    <Text size="sm" c="dimmed" data-transport="configuration">
      {t('notifications.smtpTransportHint')}
    </Text>
  );

  const warning = !channel.transportConfigured && (
    <Text size="sm" fw={600} c="statusWarning" data-transport="missing">
      {t('notifications.test.smtpNotConfigured')}
    </Text>
  );

  if (!hint && !warning) return null;

  return (
    <>
      {hint || null}
      {warning || null}
    </>
  );
}

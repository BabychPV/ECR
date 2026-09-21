import type { JSX } from 'react';
import { Text } from '@mantine/core';
import { t } from '@/shared/i18n';
import type { NotificationChannel } from './api';

/**
 * Звідки поштовий канал бере транспорт — рівно так, як це сказав сервер.
 *
 * ⛔ Сервер віддає ОДНУ ознаку — `transportFromConfiguration` (`c3652cee`), —
 * і жодного значення: ні хоста, ні порту, ні TLS, ні відправника у відповіді
 * немає. Тому тут немає й «smtp.corp:587»: такий рядок був би вигаданий
 * клієнтом, а не взятий із сервера.
 *
 * ⚠ `false` для пошти — попередження, а не порожнє місце: полів транспорту в
 * каналі немає (їх відхиляє `422`), тож канал, який не бере транспорт із
 * налаштувань застосунку, не має його НІЗВІДКИ й не надішле нічого. Сьогодні
 * сервер такого не віддає (ознака = «канал поштовий»), але читати її як
 * «пошта — отже налаштовано» означало б мовчати в день, коли він почне.
 *
 * ⚠ Рядки — наявні в каталозі (`smtpTransportHint`, `test.smtpNotConfigured`):
 * вони кажуть рівно це, а новий ключ без рядка в `09-seed.sql` червонить
 * `EndpointCoverageTests` і гейт `a11y` (на екрані був би `⟦…⟧`).
 *
 * ⚠ Для Teams нічого: адреса доставки — секрет самого каналу, і про неї вже
 * говорить сусідня колонка (`notifications.webhookMissing`).
 */
export function TransportSource({
  channel,
}: {
  readonly channel: Pick<NotificationChannel, 'kind' | 'transportFromConfiguration'>;
}): JSX.Element | null {
  if (channel.kind !== 'Smtp') return null;

  if (channel.transportFromConfiguration) {
    return (
      <Text size="sm" c="dimmed" data-transport="configuration">
        {t('notifications.smtpTransportHint')}
      </Text>
    );
  }

  return (
    <Text size="sm" fw={600} c="statusWarning" data-transport="missing">
      {t('notifications.test.smtpNotConfigured')}
    </Text>
  );
}

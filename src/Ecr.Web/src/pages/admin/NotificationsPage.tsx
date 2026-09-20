import type { JSX } from 'react';
import { Stack } from '@mantine/core';
import { ChannelsPanel } from '@/features/notifications/ChannelsPanel';
import { t } from '@/shared/i18n';
import { PageHeader } from '@/shared/ui/PageHeader';

/**
 * Сповіщення (`BE-33`, рішення 2.3 директиви №15): канали, правила «подія ×
 * канал» і журнал доставок.
 *
 * ⛔ Один екран, а не три: усі три відповідають на одне питання — «чому я
 * (не) отримав повідомлення». Канал без секрету, вимкнене правило й відмова
 * доставки — три різні відповіді на нього, і розкидані по вкладках вони
 * змусили б шукати причину в трьох місцях.
 *
 * ⚠ Правила й журнал додаються наступними кроками в цю ж сторінку
 * (`RulesMatrixPanel`, `DeliveriesPanel`); тут поки лише канали — вони вісь
 * для обох, і без жодного каналу решта екрана не має про що говорити.
 */
export function NotificationsPage(): JSX.Element {
  return (
    <Stack gap="lg">
      <PageHeader title={t('notifications.title')} />

      <ChannelsPanel />
    </Stack>
  );
}

import type { JSX } from 'react';
import { Stack } from '@mantine/core';
import { ChannelsPanel } from '@/features/notifications/ChannelsPanel';
import { DeliveriesPanel } from '@/features/notifications/DeliveriesPanel';
import { RulesMatrixPanel } from '@/features/notifications/RulesMatrixPanel';
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
 * ⚠ Порядок блоків причинний, а не за важливістю: канал існує → правило
 * вирішує, коли ним слати → журнал каже, що з цього вийшло. Людина приходить
 * сюди з питанням «чому я (не) отримав повідомлення» і йде цим самим шляхом.
 */
export function NotificationsPage(): JSX.Element {
  return (
    <Stack gap="lg">
      <PageHeader title={t('notifications.title')} />

      <ChannelsPanel />

      <RulesMatrixPanel />

      <DeliveriesPanel />
    </Stack>
  );
}

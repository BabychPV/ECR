import { lazy, Suspense, type JSX } from 'react';
import { Skeleton, Stack, Title } from '@mantine/core';
import { ChannelsPanel } from '@/features/notifications/ChannelsPanel';
import { DeliveriesPanel } from '@/features/notifications/DeliveriesPanel';
import { RulesMatrixPanel } from '@/features/notifications/RulesMatrixPanel';
import { t } from '@/shared/i18n';
import { PageHeader } from '@/shared/ui/PageHeader';

/**
 * ⚠ Налаштування SMTP — ЛІНИВИЙ чанк: форма з десятком полів потрібна раз на розгортання, а
 * NotificationsPage стоїть біля стелі бюджету маршруту (D-132).
 */
const loadSmtpSettingsPanel = () => import('@/features/notifications/SmtpSettingsPanel');
const SmtpSettingsPanel = lazy(async () => ({ default: (await loadSmtpSettingsPanel()).SmtpSettingsPanel }));

/**
 * ⚠ Шаблони повідомлень (`CL-6`) — теж ЛІНИВИЙ чанк, з тієї ж причини: правляться рідко, а маршрут
 * стоїть біля стелі бюджету (D-132). Модалок у панелі немає — фокус нікуди не переноситься.
 */
const loadNotificationTemplatesPanel = () => import('@/features/notifications/NotificationTemplatesPanel');
const NotificationTemplatesPanel = lazy(async () => ({
  default: (await loadNotificationTemplatesPanel()).NotificationTemplatesPanel,
}));

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

      <Suspense fallback={<Skeleton height={160} radius="sm" />}>
        <SmtpSettingsPanel />
      </Suspense>

      <ChannelsPanel />

      {/*
        ⚠ `RulesMatrixPanel` навмисно не малює власного заголовка
        (`RulesMatrixPanel.tsx`, коментар над `aria-label` таблиці): окремий
        `<Title>` усередині блоку рвав би `heading-order`, тож рівень
        задає сторінка — рівно так само, як `ChannelsPanel` малює свій
        (`order={2} size="h5"`), щоб два блоки лишалися сусідніми заголовками
        одного рівня, а не одним заголовком і одним без нього.

        ⛔ Без цього заголовка порожній стенд (каналів ще нуль) показував два
        ІДЕНТИЧНІ блоки тексту («каналів немає») підряд без жодного заголовка
        між ними — один від `ChannelsPanel`, другий від `RulesMatrixPanel`, і
        це читалося як зламаний дублікат, а не як дві різні секції.
      */}
      <Title order={2} size="h5">
        {t('notifications.rules')}
      </Title>

      <RulesMatrixPanel />

      {/* Що саме відправляється за подією і кому — після правил, бо адресати виводяться з них. */}
      <Suspense fallback={<Skeleton height={160} radius="sm" />}>
        <NotificationTemplatesPanel />
      </Suspense>

      <DeliveriesPanel />
    </Stack>
  );
}

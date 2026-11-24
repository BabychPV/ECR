import type { JSX, ReactNode } from 'react';
import { Stack } from '@mantine/core';
import type { CurrentUserDto } from '@/api/types';
import { t } from '@/shared/i18n';
import { can, useSession } from '@/shared/session/useSession';
import { EmptyState } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';

/**
 * Чи бачить користувач на головній пояснення «звіти — у SSRS» замість переліку документів: перегляду документів
 * немає, а право переглядача звітів є (вбудована роль `ReportViewer`).
 *
 * ✎ P-1124-3 (D-NEW, людина 24.11): зрізи звітів у застосунку прибрано, тож цій ролі більше нема куди
 * перенаправлятися (FP-1 вів на `/admin/snapshots`). Перелік документів дав би червону відмову, перелік проєктів
 * закритий правом `Period.Configure`. Чесна стартова сторінка — пояснення, де тепер звіти, без посилання: адресу
 * сервера звітів замовника застосунок не знає, а мертве посилання гірше за його відсутність.
 *
 * ⚠ Умова та сама вузька, що в FP-1: усі, хто має `Document.View`, і далі бачать перелік документів; користувач без
 * жодного з двох прав — чесну відмову головної.
 */
export function startsOnReportsNotice(me: CurrentUserDto | undefined): boolean {
  return me !== undefined && !can(me, 'Document.View') && can(me, 'Report.ViewRegulatory');
}

/** Пояснення для ролі переглядача звітів: звіти будуються в SSRS із сирого шару `rpt.*`. */
export function ReportsInSsrsHome(): JSX.Element {
  return (
    <Stack gap="md" data-testid="reports-in-ssrs">
      {/* ⛔ Без опису маршруту / (
av.documents.description: «…Open a document to work in it»): для ролі без документів він
          радив би те, чого вона зробити не може (знімок 24.11). Пояснення — у підказці нижче. */}
      <PageHeader title={t('home.reportsInSsrs.title')} description={null} />
      <EmptyState headingOrder={2} title={t('home.reportsInSsrs.heading')} hint={t('home.reportsInSsrs.hint')} />
    </Stack>
  );
}

/** Домашній маршрут: перелік документів або, для ролі без документів, пояснення «звіти — у SSRS». */
export function HomeRoute({ children }: { children: ReactNode }): JSX.Element {
  const session = useSession();

  if (startsOnReportsNotice(session.data)) {
    return <ReportsInSsrsHome />;
  }

  return <>{children}</>;
}

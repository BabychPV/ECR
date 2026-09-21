import type { JSX } from 'react';
import { Stack, Text, Title } from '@mantine/core';
import {
  isCampaignTruncated,
  useCampaignSummary,
  type CampaignProject,
  type CampaignSummary,
} from '@/features/campaign/api';
import { campaignTotals, isLagging } from '@/features/campaign/campaignView';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { Banner } from '@/shared/ui/Banner';
import { DataTable, type DataTableColumn } from '@/shared/ui/DataTable';
import { StatStrip } from '@/shared/ui/StatStrip';
import { statusKey } from '@/shared/ui/StatusBadge';

/** За який період зводити кампанію; `null` — період не обрано. */
export interface CampaignOverviewProps {
  readonly periodKey: number | null;
}

/**
 * Огляд звітної кампанії періоду (`BE-22`): хто її затримує.
 *
 * ⛔ Лічильники й перелік — по ВСІХ проєктах періоду, а не лише по тих, на які
 * в користувача є грант. Це не пропущений фільтр, а рішення людини (`Q15-07`):
 * доступ дає окреме право `Report.ViewCampaign`, і без чужих проєктів питання
 * «хто затримує кампанію» не має відповіді. Фільтра проєктів тут немає й не
 * повинно бути.
 *
 * ⛔ Правило `L10`: помилка → очікування → дані, саме в такому порядку
 * (`AsyncBoundary`). Відмова сервера не малює ні нульових лічильників, ні
 * порожнього переліку — «кампанія порожня» і «сервер не відповів» не можуть
 * виглядати однаково. Те саме з дублем у кеші: невдалий повторний запит
 * показує відмову, а не старий перелік під видом свіжого.
 */
export function CampaignOverview({ periodKey }: CampaignOverviewProps): JSX.Element {
  const summary = useCampaignSummary(periodKey);

  return (
    <AsyncBoundary<CampaignSummary>
      // ⚠ Вимкнений запит (періоду немає) у TanStack Query теж `isPending`;
      // без цієї умови екран без періоду вічно показував би скелет.
      isPending={periodKey !== null && summary.isPending}
      error={summary.error}
      data={summary.data}
      isEmpty={(data) => data.totalProjects === 0}
      emptyTitle={periodKey === null ? t('campaign.pickPeriod') : t('campaign.emptyTitle')}
      emptyHint={periodKey === null ? undefined : t('campaign.emptyHint')}
      skeleton="table"
      onRetry={() => void summary.refetch()}
    >
      {(data) => <CampaignBody summary={data} />}
    </AsyncBoundary>
  );
}

/** Зміст огляду, коли відповідь є. */
function CampaignBody({ summary }: { readonly summary: CampaignSummary }): JSX.Element {
  const shown = summary.projects.length;
  const totals = campaignTotals(summary.projects);
  const lagging = summary.projects.filter(isLagging);

  return (
    <Stack gap="lg">
      {/*
        ⛔ Головна вимога екрана. Сервер обрізає перелік стелею
        (`GetCampaignSummaryHandler.MaxProjects`), і відстаючий може бути саме в
        тій частині, якої не видно. Мовчки показати 200 рядків із 300 означало б
        відповісти на «хто затримує кампанію» неправдою.
      */}
      {isCampaignTruncated(summary) && (
        <Banner
          tone="warning"
          testId="campaign-truncated"
          title={t('campaign.truncatedTitle', { shown, total: summary.totalProjects })}
          text={t('campaign.truncatedHint')}
        />
      )}

      <Stack gap="xs">
        <Title order={4}>{t('documents.summaryLabel')}</Title>

        {/*
          ⚠ Підписи станів — ті самі рядки каталогу, що й у `<StatusBadge>`
          (`status.sheet.*`), як у смузі переліку документів: число тут і бейдж
          там мусять називати стан одним словом. Колір — лише у відхилених і
          лише коли вони є (`L3`, `problemTone`).
        */}
        <StatStrip
          label={t('documents.summaryLabel')}
          items={[
            { id: 'draft', label: t(statusKey('sheet', 'Draft')), value: totals.draft, filter: false },
            {
              id: 'submitted',
              label: t(statusKey('sheet', 'Submitted')),
              value: totals.submitted,
              filter: false,
            },
            {
              id: 'rejected',
              label: t(statusKey('sheet', 'Rejected')),
              value: totals.rejected,
              tone: 'danger',
              filter: false,
            },
            { id: 'approved', label: t(statusKey('sheet', 'Approved')), value: totals.approved, filter: false },
          ]}
        />
      </Stack>

      <Stack gap="xs">
        <Title order={4}>{t('campaign.laggingTitle')}</Title>

        <Text size="sm" c="dimmed" data-campaign-lagging-count="">
          {t('campaign.laggingCount', { lagging: lagging.length, shown })}
        </Text>

        <DataTable<CampaignProject>
          columns={laggingColumns()}
          rows={lagging}
          rowKey={(project) => String(project.projectId)}
          emptyTitle={t('campaign.nobodyLagging')}
          emptyHint={t('campaign.nobodyLaggingHint')}
        />
      </Stack>
    </Stack>
  );
}

/**
 * Колонки переліку відстаючих.
 *
 * ⛔ Код проєкту — ІДЕНТИФІКАТОР, а не величина: без `num`, рядком як є. Код
 * `10000` на екрані `10,000` був би кодом, якого в системі немає. Лічильники —
 * величини, тому `num`.
 *
 * ⚠ Затверджених окремої колонки немає: це `documents − draft − submitted −
 * rejected` (агрегат `BE-09`), а питання екрана — що ще НЕ дійшло до кінця.
 *
 * ⚠ Функція, а не константа модуля: підписи беруться з каталогу, який доїжджає
 * після завантаження модуля.
 */
function laggingColumns(): readonly DataTableColumn<CampaignProject>[] {
  return [
    {
      key: 'projectCode',
      label: t('campaign.projectCode'),
      mono: true,
      render: (project) => project.projectCode,
    },
    {
      key: 'name',
      label: t('campaign.projectName'),
      sortValue: (project) => localized(project.nameL10n),
      render: (project) => localized(project.nameL10n),
    },
    { key: 'documents', label: t('campaign.documents'), num: true },
    { key: 'draft', label: t(statusKey('sheet', 'Draft')), num: true },
    { key: 'submitted', label: t(statusKey('sheet', 'Submitted')), num: true },
    { key: 'rejected', label: t(statusKey('sheet', 'Rejected')), num: true },
    { key: 'snapshots', label: t('campaign.snapshots'), num: true },
  ];
}

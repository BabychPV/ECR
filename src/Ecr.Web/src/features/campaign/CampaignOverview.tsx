import type { JSX } from 'react';
import { Badge, Stack, Text, Title } from '@mantine/core';
import {
  isCampaignTruncated,
  useCampaignSummary,
  type CampaignProgress,
  type CampaignProject,
  type CampaignSummary,
} from '@/features/campaign/api';
import { holdingUp, holdingUpRank, lastSubmissionDay } from '@/features/campaign/campaignView';
import { formatDate } from '@/shared/format';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { Banner } from '@/shared/ui/Banner';
import { DataTable, type DataTableColumn } from '@/shared/ui/DataTable';
import { StatStrip } from '@/shared/ui/StatStrip';
import { statusKey, toneFills, type StatusTone } from '@/shared/ui/StatusBadge';

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
 * ⛔ Підсумки й класифікацію рахує СЕРВЕР (`totals`, `progress`). Клієнт їх не
 * складає і не виводить: перелік обрізано стелею, і сума по ньому брехала б
 * саме тоді, коли проєктів більше за стелю; а правило «хто затримує» залежить
 * від строку подання в поясі проєкту, якого клієнт не знає.
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
  const { totals } = summary;
  const lagging = holdingUp(summary.projects);

  return (
    <Stack gap="lg">
      {/*
        ⛔ Головна вимога екрана. Сервер обрізає перелік стелею
        (`GetCampaignSummaryHandler.MaxProjects`), і відстаючий може бути саме в
        тій частині, якої не видно. Мовчки показати 200 рядків із 300 означало б
        відповісти на «хто затримує кампанію» неправдою.

        ⚠ Обрізано ЛИШЕ перелік: підсумки нижче — з `totals`, по всіх проєктах
        періоду, і підказка банера каже саме це, а не «підсумки неповні».
      */}
      {isCampaignTruncated(summary) && (
        <Banner
          tone="warning"
          testId="campaign-truncated"
          title={t('campaign.truncatedTitle', { shown: summary.projects.length, total: summary.totalProjects })}
          text={t('campaign.truncatedListHint', { total: totals.projects })}
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

        {/*
          ⚠ Лічильники класів — з `totals` сервера, по всіх проєктах періоду.
          Колір — лише у тих, що затримують, і лише коли вони є (`L3`).
        */}
        <StatStrip
          label={t('campaign.laggingTitle')}
          items={[
            { id: 'overdue', label: progressLabel('Overdue'), value: totals.overdue, tone: 'danger', filter: false },
            { id: 'atRisk', label: progressLabel('AtRisk'), value: totals.atRisk, tone: 'warning', filter: false },
            { id: 'inProgress', label: progressLabel('InProgress'), value: totals.inProgress, filter: false },
            { id: 'done', label: progressLabel('Done'), value: totals.done, filter: false },
          ]}
        />

        <DataTable<CampaignProject>
          columns={laggingColumns()}
          rows={lagging}
          rowKey={(project) => String(project.projectId)}
          emptyTitle={t('campaign.nobodyLagging')}
          emptyHint={t('campaign.nobodyLaggingServerHint')}
        />
      </Stack>
    </Stack>
  );
}

/**
 * Підпис класу кампанії.
 *
 * ⚠ Ключі — ЛІТЕРАЛАМИ, а не шаблоном `campaign.progress.${progress}`: сторож
 * каталогу (`Кожен_рядок_якого_просить_клієнт_є_в_каталозі`) бачить лише
 * літерал, і складений ключ пройшов би повз нього.
 */
function progressLabel(progress: CampaignProgress): string {
  switch (progress) {
    case 'Overdue':
      return t('campaign.progress.Overdue');
    case 'AtRisk':
      return t('campaign.progress.AtRisk');
    case 'InProgress':
      return t('campaign.progress.InProgress');
    case 'Done':
      return t('campaign.progress.Done');
  }
}

/**
 * Тон класу.
 *
 * ⚠ Різновиду `campaign` у `StatusBadge` немає, тому бейдж локальний, але
 * кольори — з тієї самої таблиці токенів (`toneFills`), а не власні. Тон лише
 * у тих, що затримують: прострочений — `danger`, під загрозою — `warning`.
 */
const ProgressTone: Readonly<Record<CampaignProgress, StatusTone>> = {
  Overdue: 'danger',
  AtRisk: 'warning',
  InProgress: 'neutral',
  Done: 'neutral',
};

function ProgressBadge({ progress }: { readonly progress: CampaignProgress }): JSX.Element {
  const tone = ProgressTone[progress];
  const fill = toneFills[tone];

  return (
    <Badge
      size="sm"
      miw="fit-content"
      variant="default"
      bg={fill.bg}
      c={fill.text}
      data-campaign-progress={progress}
      data-status-tone={tone}
    >
      {progressLabel(progress)}
    </Badge>
  );
}

/** Останній день подання — датою набору, або «строк не визначено». */
function LastDay({ project }: { readonly project: CampaignProject }): JSX.Element {
  const day = lastSubmissionDay(project.submissionDeadline);

  return (
    <Text span size="sm" data-last-day={day ?? ''}>
      {day === null ? t('campaign.deadlineUnknown') : formatDate(day)}
    </Text>
  );
}

/**
 * Колонки переліку відстаючих.
 *
 * ⛔ Код проєкту — ІДЕНТИФІКАТОР, а не величина: без `num`, рядком як є. Код
 * `10000` на екрані `10,000` був би кодом, якого в системі немає. Лічильники —
 * величини, тому `num`.
 *
 * ⚠ Межа `L5` — сім колонок. Клас і останній день подання — відповідь на
 * питання екрана, тому вони витіснили «чернетки» й «подані»: їхні суми є в
 * смузі підсумків, а причину затримки називає клас.
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
    {
      key: 'progress',
      label: t('campaign.progressColumn'),
      sortValue: (project) => holdingUpRank(project.progress),
      render: (project) => <ProgressBadge progress={project.progress} />,
    },
    {
      key: 'lastDay',
      label: t('campaign.lastSubmissionDay'),
      sortValue: (project) => lastSubmissionDay(project.submissionDeadline),
      render: (project) => <LastDay project={project} />,
    },
    { key: 'documents', label: t('campaign.documents'), num: true },
    { key: 'rejected', label: t(statusKey('sheet', 'Rejected')), num: true },
    { key: 'snapshots', label: t('campaign.snapshots'), num: true },
  ];
}

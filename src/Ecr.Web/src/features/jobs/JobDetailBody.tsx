import type { JSX } from 'react';
import { Progress, Stack, Text } from '@mantine/core';
import type { JobStatus, JobSummary } from '@/api/types';
import { t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import { ErrorAlert, TechnicalDetails } from '@/shared/ui/ErrorAlert';
import { KeyValue } from '@/shared/ui/KeyValue';
import { Timestamp } from '@/shared/ui/Timestamp';
import { useJobStatus } from './useJobStatus';
import { JobFailure, attemptText, jobAuthor } from './JobFacts';

/**
 * Похідний стан батька-розкладу (P4): «розкладено N, виконано M з N, помилок K».
 *
 * ⛔ Збережений стан такого батька — `Succeeded`, хоча дочірні ще не пораховані;
 * без цього рядка оператор читав би «виконано» замість «ще не пораховано».
 */
function FanOutSummary({ status }: { readonly status: JobStatus }): JSX.Element | null {
  const fan = status.fanOut;

  if (fan === null || fan === undefined) return null;

  const label =
    status.effectiveState === 'FannedOut'
      ? t('jobs.fanOutPending')
      : status.effectiveState === 'SucceededWithErrors'
        ? t('jobs.fanOutDoneWithErrors', { failed: fan.failed })
        : t('jobs.fanOutDone');

  return (
    <Stack gap="xs" data-job-fanout={status.effectiveState ?? ''}>
      <Text size="sm" fw={600}>
        {label}
      </Text>
      <Text size="xs" c="dimmed">
        {t('jobs.fanOutProgress', {
          total: fan.total,
          done: fan.succeeded,
          failed: fan.failed,
        })}
      </Text>
    </Stack>
  );
}

/**
 * Вміст шторки задачі (UI-28; макет `screens-ops.js` `/admin/jobs`, `drawer`).
 *
 * ⚠ ЛІНИВИЙ чанк: шторка закрита за замовчуванням (`L2`, бюджет маршруту).
 *
 * ⚠ Стан — з `GET /jobs/{id}` (`useJobStatus`, опитування поки задача йде), а
 * не з рядка переліку: шторку відкривають і для задачі, якої в переліку
 * останніх немає (`?id=` з чужого повідомлення, «Find a job by id»).
 *
 * ⚠ Документ задачі — кнопкою «Open document» у підвалі шторки (макет), а не
 * ще одним посиланням тут: два однакові посилання поруч — шум для читалки.
 * Ідентифікатор задачі — у підзаголовку шторки.
 *
 * ⛔ Розділів макета «What to do», «Steps» і «Attempt 1/2/3» з часами тут
 * НЕМАЄ (`UI-ADOPTION-TASKS` §1.2): `JobStatus` не віддає ні кроків, ні поради,
 * ні історії спроб — лише `attempt`/`maxAttempts`. Вигаданий текст гірший за
 * відсутній (`D15-06`); потрібне названо TODO-контрактом.
 */
export default function JobDetailBody({
  jobId,
  summary,
}: {
  readonly jobId: string;
  readonly summary?: JobSummary | undefined;
}): JSX.Element {
  const job = useJobStatus(jobId);

  if (job.error !== null) {
    // ⚠ Невідомий ідентифікатор — `404` з кодом, а не вічний прогрес задачі,
    // якої не існує.
    return <ErrorAlert error={job.error} onRetry={() => void job.refetch()} />;
  }

  const status = job.data;

  if (status === undefined) {
    return (
      <Text size="sm" c="dimmed" role="status" aria-busy="true">
        {t('common.loading')}
      </Text>
    );
  }

  const createdAt = status.createdAt ?? summary?.createdAt ?? null;

  return (
    <Stack gap="md" data-job-detail={status.jobId}>
      {status.state === 'Failed' && (
        <Banner
          tone="danger"
          title={t('jobs.whyFailed')}
          text={<JobFailure state={status.state} errorCode={status.errorCode} correlationId={status.correlationId} />}
        />
      )}

      {/* ⚠ Прогрес — лише поки задача йде: для завершеної 100 % нічого не
          кажуть, а для проваленої «зупинилась на N %» каже перелік. */}
      {(status.state === 'Running' || status.state === 'Queued') && (
        <Progress
          value={status.percent}
          animated={status.state === 'Running'}
          aria-label={t('jobs.progress')}
        />
      )}

      {status.message !== null && status.message !== '' && <Text size="sm">{status.message}</Text>}

      <FanOutSummary status={status} />

      <KeyValue
        items={[
          // ⚠ Автор — лише з переліку (`JobSummary.createdByDisplayName`):
          // `JobStatus` імені не віддає, а «System» для задачі людини збрехав би.
          { label: t('jobs.createdBy'), value: summary === undefined ? null : jobAuthor(summary.createdByDisplayName) },
          { label: t('jobs.createdAt'), value: createdAt === null ? null : <Timestamp value={createdAt} /> },
          {
            label: t('jobs.recentStarted'),
            value: summary === undefined ? null : <Timestamp value={summary.startedAt} />,
          },
          { label: t('jobs.attempts'), value: attemptText(status.attempt, status.maxAttempts) },
          // ⛔ Сирого коду помилки окремим рядком тут немає (макет має «Error
          // code»): причину людині каже `JobFailure` рядком каталогу, а голий
          // `ECR-…` поруч читався б як другий, неперекладений текст відмови
          // (сторож `jobFacts.test.tsx`). Для підтримки — кореляція з копіюванням.
        ]}
      />

      {/* ⛔ `X-04`: сирий `error` — `ex.Message` сервера, лише згорнутим під
          локалізованою причиною (`JobFailure`). Без стека (ФВ-6.11). */}
      {status.error !== null && status.error !== '' && (
        <TechnicalDetails label={t('common.technicalDetails')}>{status.error}</TechnicalDetails>
      )}
    </Stack>
  );
}

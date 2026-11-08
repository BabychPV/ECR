import type { JSX } from 'react';
import { Anchor, Group, Stack, Text } from '@mantine/core';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import type { JobSummary } from '@/api/types';
import { JobRetry } from '@/features/jobs/JobFacts';
import { t } from '@/shared/i18n';
import { errorCodeText } from '@/shared/ui/problemText';

/** Скільки провалених документів показати (більше — на сторінці задач). */
export const FailedListLimit = 50;

/** Адреса провалених ВЛАСНИХ дочірніх задач розкладу. */
export function failedChildrenUrl(parentJobId: string): string {
  return `/api/v1/jobs?mine=true&state=Failed&parentJobId=${encodeURIComponent(parentJobId)}&limit=${String(FailedListLimit)}`;
}

/**
 * ✎ UAT recalc-progress (a6): «один дочірній провалився — підсумок каже "finished with errors", перелічує провалені
 * документи з Retry». Повтор — той самий `POST /jobs/{id}/restart` (автор власної задачі, UX-09); після нього
 * батько знову `FannedOut`, і банер повертається до «Recalculating…».
 */
export function RecalcFailedDocuments({
  parentJobId,
  onNavigate,
}: {
  readonly parentJobId: string;
  readonly onNavigate?: (() => void) | undefined;
}): JSX.Element {
  const queryClient = useQueryClient();
  const failed = useQuery({
    queryKey: ['jobs', 'recalc-failed', parentJobId],
    queryFn: () => apiFetch<JobSummary[]>(failedChildrenUrl(parentJobId)),
    retry: false,
  });

  if (failed.isPending) {
    return (
      <Text size="xs" c="dimmed">
        {t('common.loading')}
      </Text>
    );
  }

  if (failed.isError) {
    return (
      <Text size="xs" c="statusError">
        {t('recalcProgress.failedListError')}
      </Text>
    );
  }

  return (
    <Stack gap="xs" data-recalc-failed-list={parentJobId}>
      {failed.data.map((job) => (
        <Group key={job.jobId} gap="xs" wrap="nowrap" data-recalc-failed-doc={job.documentId ?? ''}>
          {job.documentId !== null && job.documentId !== undefined && (
            <Anchor component={Link} to={`/documents/${String(job.documentId)}`} size="xs" onClick={onNavigate}>
              {t('jobs.openDocument', { id: job.documentId })}
            </Anchor>
          )}
          <Text size="xs" c="dimmed" className="ecr-ellipsis">
            {errorCodeText(job.errorCode, t('workflow.recalcFailed'))}
          </Text>
          <JobRetry
            jobId={job.jobId}
            state={job.state}
            isOwnJob
            hasViewHealth={false}
            onRestarted={() => void queryClient.invalidateQueries({ queryKey: ['jobs'] })}
          />
        </Group>
      ))}
    </Stack>
  );
}

import { useEffect, useRef, useState, type JSX } from 'react';
import { Button, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { JobStatus, ProjectRecalculationRequest } from '@/api/types';
import { markSlicesStale } from '@/features/grid/sliceCache';
import { MountedLazyConfirmModal } from '@/features/projects/lazyPeriodsDialogs';
import { pollInterval, outcomeOf } from '@/features/workflow/jobFollow';
import { humanizeJobId } from '@/features/workflow/jobLabel';
import { RecalcSkippedList, type RecalcRequestOutcome } from '@/features/workflow/RecalcSkippedList';
import { t } from '@/shared/i18n';
import { FilterInline } from '@/shared/ui/FilterBar';
import { notificationCloseButtonProps, showApiError, showDone } from '@/shared/ui/notify';
import { errorCodeText } from '@/shared/ui/problemText';

/**
 * Перерахунок УСЬОГО проєкту (Q-151/Q-162) — дія переліку документів.
 *
 * ✎ Period clarity (людина 07.11, п. b): дія переїхала зі сторінки Periods (там лишилось лише блокування місяців)
 * сюди, разом із переліком пропущених затверджених аркушів (UAT-recalc-ux, `RecalcSkippedList`). Поведінка та сама:
 * підтвердження ПЕРЕД запитом (`X-25`), стеження за задачею до кінця ДОЧІРНІХ (P4 ФВ-9.8), тост «завершено» лише за
 * M = N і K = 0.
 *
 * ⚠ `periodKey: null` — повний рік (`RecalculationJob`, Q-162).
 */
export function ProjectRecalculateAction({
  project,
  label,
}: {
  readonly project: { readonly id: number; readonly code: string };
  /** ✎ UAT-p3: підпис кнопки, коли він має назвати область («Recalculate project…» на Periods); типово — «Recalculate». */
  readonly label?: string | undefined;
}): JSX.Element {
  const queryClient = useQueryClient();
  const [confirming, setConfirming] = useState(false);
  const [recalcJobId, setRecalcJobId] = useState<string | null>(null);

  // ✎ UAT-recalc-ux: відповідь несе пропущені (затверджені) аркуші; `jobId: null` — рахувати нічого.
  const [recalcRequest, setRecalcRequest] = useState<RecalcRequestOutcome | null>(null);

  const recalculate = useMutation({
    mutationFn: (id: number) =>
      apiFetch<RecalcRequestOutcome>(`/api/v1/projects/${id}/recalculate`, {
        method: 'POST',
        body: JSON.stringify({ periodKey: null } satisfies ProjectRecalculationRequest),
      }),
    onSuccess: (job) => {
      setRecalcRequest(job);
      if (job.jobId === null || job.jobId === undefined) {
        setRecalcJobId(null);
        return;
      }

      setRecalcJobId(job.jobId);
      // ⛔ Аудит-пас 8, lane6, п.8: людський вигляд у ТОСТІ, `jobId` у стані й у запиті опитування — без змін.
      showDone(t('workflow.recalcQueued', { job: humanizeJobId(job.jobId) }));
    },
    onError: showApiError,
  });

  const recalcJob = useQuery({
    queryKey: ['job', recalcJobId],
    queryFn: () => apiFetch<JobStatus>(`/api/v1/jobs/${encodeURIComponent(recalcJobId ?? '')}`),
    enabled: recalcJobId !== null,
    // ⛔ P4 ФВ-9.8: батько-розклад уже `Succeeded`, коли документи ще рахуються (`effectiveState = FannedOut`).
    refetchInterval: (query) => pollInterval(query.state.data?.state, query.state.data?.effectiveState),
    // ⚠ `GET /jobs/{id}` вимагає `System.ViewHealth` (Q-156) — без нього лишається поставлена задача, а не червоне.
    retry: false,
  });

  const recalcOutcome =
    recalcJobId === null ? null : outcomeOf(recalcJob.data?.state, recalcJob.isError, recalcJob.data?.effectiveState);
  const recalcRunning = recalcOutcome === 'running';
  // «Розкладено N, виконано M з N, помилок K» — правдивий стан проєктної задачі.
  const recalcFan = recalcJobId === null ? null : (recalcJob.data?.fanOut ?? null);
  const recalcReported = useRef<string | null>(null);

  useEffect(() => {
    if (recalcJobId === null || recalcOutcome === null || recalcOutcome === 'running') return;
    if (recalcOutcome === 'unknown') return;
    if (recalcReported.current === recalcJobId) return;

    recalcReported.current = recalcJobId;

    if (recalcOutcome === 'succeeded' || recalcOutcome === 'partial') {
      if (recalcOutcome === 'succeeded') {
        showDone(t('workflow.recalcDone'));
      } else {
        const fan = recalcJob.data?.fanOut;

        notifications.show({
          color: 'statusWarning',
          message: t('workflow.recalcDoneWithErrors', {
            total: fan?.total ?? 0,
            done: fan?.succeeded ?? 0,
            failed: fan?.failed ?? 0,
          }),
          closeButtonProps: notificationCloseButtonProps,
        });
      }

      // ⚠ Кеш сіток скидається САМЕ тут, а не на постановці в чергу; ⛔ `CL-02`: без перезапиту — зрізи прочитають
      // свіже самі, коли документ відкриють. Перелік документів (стани, позначки) — перечитується.
      void markSlicesStale(queryClient);
      void queryClient.invalidateQueries({ queryKey: ['document'] });
      void queryClient.invalidateQueries({ queryKey: ['documents'] });

      return;
    }

    // ⛔ Q-234/`X-04`: причина — за КОДОМ із каталогу, а не `message`/`error` сервера.
    notifications.show({
      color: 'statusError',
      message: errorCodeText(recalcJob.data?.errorCode, t('workflow.recalcFailed')),
      closeButtonProps: notificationCloseButtonProps,
    });
  }, [recalcJobId, recalcOutcome, recalcJob.data?.errorCode, recalcJob.data?.fanOut, queryClient]);

  return (
    <>
      <FilterInline>
        <Button
          variant="default"
          loading={recalculate.isPending || recalcRunning}
          onClick={() => setConfirming(true)}
          data-project-recalculate={project.id}
        >
          {recalcRunning ? t('workflow.recalcRunning') : (label ?? t('workflow.recalculate'))}
        </Button>
      </FilterInline>

      {recalcFan !== null && (
        <FilterInline>
          <Text size="xs" c="dimmed" role="status" data-recalc-fanout={recalcJob.data?.effectiveState ?? ''}>
            {t('jobs.fanOutProgress', { total: recalcFan.total, done: recalcFan.succeeded, failed: recalcFan.failed })}
          </Text>
        </FilterInline>
      )}

      {/* ✎ UAT-recalc-ux: пропущені затверджені аркуші — після завершення задачі (або одразу, коли рахувати нічого). */}
      {recalcRequest !== null &&
        (recalcRequest.nothingToRecalculate === true || (recalcJobId !== null && !recalcRunning)) && (
          <RecalcSkippedList outcome={recalcRequest} />
        )}

      <MountedLazyConfirmModal
        opened={confirming}
        title={t('periods.recalcTitle', { code: project.code })}
        text={t('periods.recalcConfirm')}
        verb={t('workflow.recalculate')}
        danger={false}
        isPending={recalculate.isPending}
        onConfirm={() => {
          recalculate.mutate(project.id);
          setConfirming(false);
        }}
        onClose={() => setConfirming(false)}
      />
    </>
  );
}

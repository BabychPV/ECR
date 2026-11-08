import { useState, type JSX } from 'react';
import { Button } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { PeriodCalendarDto, ProjectRecalculationRequest } from '@/api/types';
import { effectivePeriodState } from '@/features/projects/periodEffective';
import { MountedLazyConfirmModal } from '@/features/projects/lazyPeriodsDialogs';
import { isRecalcActive } from '@/features/recalc/recalcProgress';
import { projectRecalcKey, useProjectRecalcProgress } from '@/features/recalc/useProjectRecalcProgress';
import { RecalcSkippedList, type RecalcRequestOutcome } from '@/features/workflow/RecalcSkippedList';
import { formatPeriodKey } from '@/shared/format';
import { t } from '@/shared/i18n';
import { FilterInline } from '@/shared/ui/FilterBar';
import { showApiError, showDone } from '@/shared/ui/notify';

/**
 * Область перерахунку проєкту для діалогу підтвердження (сценарій a1): рік, «усі документи», місяці, що
 * пропускаються (заблоковані за ЕФЕКТИВНИМ станом — сервер вилучає їх із річного перерахунку).
 */
export function recalcScopeText(
  calendar: PeriodCalendarDto | undefined,
  projectCode: string,
  now: number = Date.now(),
): string {
  if (calendar === undefined) return t('recalcProgress.confirmScopeUnknown', { project: projectCode });

  const current = calendar.periods.find((period) => period.isCurrent) ?? calendar.periods.at(-1);
  const year = current?.year ?? new Date(now).getFullYear();
  const locked = calendar.periods
    .filter((period) => period.year === year && effectivePeriodState(period, now) === 'Closed')
    .map((period) => formatPeriodKey(period.periodKey, calendar.periodKind));

  return locked.length === 0
    ? t('recalcProgress.confirmScope', { project: projectCode, year })
    : t('recalcProgress.confirmScopeSkipping', { project: projectCode, year, months: locked.join(', ') });
}

/**
 * Перерахунок УСЬОГО проєкту (Q-151/Q-162) — дія переліку документів і сторінки Periods.
 *
 * ✎ UAT recalc-progress (08.11, a-P3): тост «finished: figures up to date» за секунду після розкладу прибрано.
 * Після підтвердження — лише «поставлено в чергу»; прогрес, завершення з лічильниками й провалені документи
 * показує `ProjectRecalcBanner`, який читає стан із СЕРВЕРА (переживає перехід на іншу сторінку). Кнопка
 * зайнята, поки перерахунок цього проєкту йде, — теж за сервером.
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

  // ✎ UAT-recalc-ux: відповідь несе пропущені (затверджені) аркуші; `jobId: null` — рахувати нічого.
  const [recalcRequest, setRecalcRequest] = useState<RecalcRequestOutcome | null>(null);

  const progress = useProjectRecalcProgress(project.id);
  const running = progress.items.some(isRecalcActive);

  // Календар — той самий ключ `['periods', id]`, що й скрізь: зазвичай уже в кеші.
  const calendar = useQuery({
    queryKey: ['periods', project.id],
    queryFn: () => apiFetch<PeriodCalendarDto>(`/api/v1/projects/${String(project.id)}/periods`),
    enabled: confirming,
  });

  const recalculate = useMutation({
    mutationFn: (id: number) =>
      apiFetch<RecalcRequestOutcome>(`/api/v1/projects/${id}/recalculate`, {
        method: 'POST',
        body: JSON.stringify({ periodKey: null } satisfies ProjectRecalculationRequest),
      }),
    onSuccess: (job) => {
      setRecalcRequest(job);
      if (job.jobId === null || job.jobId === undefined) return;

      // ⛔ a1: «Queued…», ніколи «finished» — завершення скаже банер, коли завершаться всі документи.
      showDone(t('recalcProgress.queuedToast', { project: project.code }));
      void queryClient.invalidateQueries({ queryKey: projectRecalcKey(project.id) });
      void queryClient.invalidateQueries({ queryKey: projectRecalcKey(null) });
    },
    onError: showApiError,
  });

  return (
    <>
      <FilterInline>
        <Button
          variant="default"
          loading={recalculate.isPending || running}
          onClick={() => setConfirming(true)}
          data-project-recalculate={project.id}
        >
          {running ? t('workflow.recalcRunning') : (label ?? t('workflow.recalculate'))}
        </Button>
      </FilterInline>

      {/* ✎ UAT-recalc-ux: пропущені затверджені аркуші — одразу, коли рахувати нічого, або коли перерахунок завершився. */}
      {recalcRequest !== null && (recalcRequest.nothingToRecalculate === true || (recalcRequest.jobId !== null && !running)) && (
        <RecalcSkippedList outcome={recalcRequest} />
      )}

      <MountedLazyConfirmModal
        opened={confirming}
        title={t('periods.recalcTitle', { code: project.code })}
        text={`${recalcScopeText(calendar.data, project.code)} ${t('periods.recalcConfirm')}`}
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

import { useState, type JSX } from 'react';
import { Button } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { PeriodCalendarDto, ProjectRecalculationRequest } from '@/api/types';
import { effectivePeriodState } from '@/features/projects/periodEffective';
import { MountedLazyConfirmModal } from '@/features/projects/lazyPeriodsDialogs';
import { isRecalcActive } from '@/features/recalc/recalcProgress';
import {
  projectRecalcKey,
  rememberRecalcTotal,
  useProjectRecalcProgress,
} from '@/features/recalc/useProjectRecalcProgress';
import { RecalcSkippedList, type RecalcRequestOutcome } from '@/features/workflow/RecalcSkippedList';
import { formatMonthName, formatPeriodKey } from '@/shared/format';
import { t } from '@/shared/i18n';
import { FilterInline } from '@/shared/ui/FilterBar';
import { showApiError, showDone } from '@/shared/ui/notify';

type CalendarPeriod = PeriodCalendarDto['periods'][number];

/**
 * Відкриті місяці стислим рядком: суцільний відрізок — «September–November 2026», інакше перелік через кому.
 */
export function openMonthsText(periods: readonly CalendarPeriod[], kind: string): string {
  if (periods.length === 0) return '';

  const sorted = [...periods].sort((a, b) => a.periodKey - b.periodKey);
  const first = sorted[0] as CalendarPeriod;
  const last = sorted.at(-1) as CalendarPeriod;
  const contiguous =
    kind === 'Monthly' &&
    first.year === last.year &&
    sorted.every((period, index) => period.sequence === first.sequence + index);

  if (contiguous && sorted.length > 1) {
    return `${formatMonthName(first.sequence)}–${formatMonthName(last.sequence)} ${String(first.year)}`;
  }

  return sorted.map((period) => formatPeriodKey(period.periodKey, kind)).join(', ');
}

/**
 * Область перерахунку проєкту для діалогу підтвердження (сценарій a1; P2 живого проходу 08.11): ВІДКРИТІ місяці
 * (за ефективним станом — заблоковані сервер вилучає з річного перерахунку), кількість документів, і що
 * пропускається. Замість старого «Every document of every period…», яке було неправдою.
 */
export function recalcScopeText(
  calendar: PeriodCalendarDto | null | undefined,
  projectCode: string,
  documentCount: number | null,
  now: number = Date.now(),
): string {
  const documents = documentCount ?? '—';

  // ⚠ Календар ще не приїхав (або відповідь без періодів) — область без місяців, а не падіння діалогу.
  if (calendar === undefined || calendar === null || !Array.isArray(calendar.periods)) {
    return t('recalcProgress.confirmScopeUnknown', { project: projectCode, documents });
  }

  const open = calendar.periods.filter((period) => {
    const state = effectivePeriodState(period, now);
    return state === 'Open' || state === 'Grace';
  });

  return open.length === 0
    ? t('recalcProgress.confirmScopeNoOpen', { documents })
    : t('recalcProgress.confirmScope', { months: openMonthsText(open, calendar.periodKind), documents });
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
  /** `documentCount` — з переліку проєктів (`ProjectSummary.documentCount`); без нього діалог пише «—». */
  readonly project: { readonly id: number; readonly code: string; readonly documentCount?: number | undefined };
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
      // ✎ a1 (08.11, P3): «Queued N documents» з реальним N — рік розкладає всі документи проєкту (= documentCount);
      // банер бере те саме N, поки сервер ще не розклав (`withExpectedTotal`). Без числа — текст без N.
      const total = project.documentCount;
      if (total === undefined) {
        showDone(t('recalcProgress.queuedToast', { project: project.code }));
      } else {
        rememberRecalcTotal(job.jobId, total);
        showDone(t('recalcProgress.queuedDocumentsToast', { project: project.code, total }));
      }
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
        text={`${recalcScopeText(calendar.data, project.code, project.documentCount ?? null)}. ${t('periods.recalcConfirm')}`}
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

import type { JobSummary } from '@/api/types';
import { FannedOut, SucceededWithErrors } from '@/features/workflow/jobFollow';
import { t } from '@/shared/i18n';

/**
 * ✎ UAT recalc-progress (людина 08.11: «як мені зрозуміти що перерахунок завершен?»): стан перерахунку ПРОЄКТУ з
 * рядка переліку задач (`GET /jobs?kind=projectRecalculation`) — батько розкладу і лічильники його дітей.
 *
 * ⛔ Батько зберігає `Succeeded`, щойно РОЗКЛАВ документи; «завершено» — лише коли сервер каже, що діти завершились
 * (`effectiveState` ≠ `FannedOut`). До цього тост «finished: figures up to date» з'являвся за секунду після
 * розкладу, поки 92 документи ще рахувались.
 */
export type RecalcPhase = 'queued' | 'running' | 'finished' | 'finishedWithErrors' | 'failed';

/** Стан перерахунку проєкту для банера, «Моїх задач» і переліку задач. */
export interface RecalcProgress {
  readonly jobId: string;
  readonly projectId: number | null;
  readonly projectCode: string;
  readonly phase: RecalcPhase;
  /** Документів розкладено; `null` — розклад ще не відбувся. */
  readonly total: number | null;
  /** Документів оброблено (виконано + провалено). */
  readonly done: number;
  /** Затверджених аркушів пропущено (їхні числа не змінено). */
  readonly skipped: number;
  /** Документів провалено або скасовано. */
  readonly failed: number;
  /** Документів перераховано успішно. */
  readonly recalculated: number;
  readonly updatedAt: string;
}

/** Повне ім'я маркера перерахунку (`JobListKinds.RecalculationJobCode`). */
export const RecalculationJobCode = 'Ecr.Application.Ports.IRecalculationJob';

/** Чи рядок переліку — перерахунок УСЬОГО проєкту (батько розкладу, без документа). */
export function isProjectRecalculation(job: JobSummary): boolean {
  return job.jobCode === RecalculationJobCode && (job.documentId === null || job.documentId === undefined);
}

/** Стан перерахунку проєкту з рядка переліку. */
export function progressOf(job: JobSummary): RecalcProgress {
  const fan = job.fanOut ?? null;
  const effective = job.effectiveState ?? job.state;

  let phase: RecalcPhase;
  if (job.state === 'Queued' || job.state === 'Running') {
    phase = 'queued';
  } else if (job.state !== 'Succeeded') {
    phase = 'failed';
  } else if (effective === FannedOut) {
    phase = 'running';
  } else if (effective === SucceededWithErrors) {
    phase = 'finishedWithErrors';
  } else {
    phase = 'finished';
  }

  const succeeded = fan?.succeeded ?? 0;
  const failed = fan?.failed ?? 0;

  return {
    jobId: job.jobId,
    projectId: job.projectId ?? null,
    projectCode: job.projectCode ?? (job.projectId === null || job.projectId === undefined ? '' : String(job.projectId)),
    phase,
    // ✎ a1 (живий стенд 09.11): поки батько ще розкладає (Queued/Running), `fanOut.total` — лише вже поставлені діти
    // («Queued 1 documents» при трьох); N невідоме, доки розклад не завершено (банер бере `withExpectedTotal`).
    total: fan === null || phase === 'queued' ? null : fan.total,
    done: succeeded + failed,
    skipped: fan?.skippedSheets ?? 0,
    failed,
    recalculated: succeeded,
    updatedAt: job.updatedAt,
  };
}

/** Чи перерахунок ще йде (опитувати далі). */
export function isRecalcActive(progress: RecalcProgress): boolean {
  return progress.phase === 'queued' || progress.phase === 'running';
}

/**
 * Рядок стану мовою інтерфейсу — однаковий у банері, «Моїх задачах» і на сторінці задач.
 *
 * ⛔ Тексти — зі сценарію приймання (a1–a3, a6): «Queued N documents», «Recalculating X: k of N documents, m skipped,
 * f failed», «Finished: N recalculated, m skipped (approved), f failed», «finished with errors». Слова «finished» до
 * завершення ВСІХ дітей немає ніде.
 */
export function recalcProgressText(progress: RecalcProgress): string {
  const project = progress.projectCode;

  switch (progress.phase) {
    case 'queued':
      return progress.total === null
        ? t('recalcProgress.queuedProject', { project })
        : t('recalcProgress.queuedDocuments', { project, total: progress.total });
    case 'running':
      return progress.done === 0
        ? t('recalcProgress.queuedDocuments', { project, total: progress.total ?? 0 })
        : t('recalcProgress.running', {
            project,
            done: progress.done,
            total: progress.total ?? 0,
            skipped: progress.skipped,
            failed: progress.failed,
          });
    case 'finished':
      return t('recalcProgress.finished', {
        project,
        recalculated: progress.recalculated,
        skipped: progress.skipped,
        failed: progress.failed,
      });
    case 'finishedWithErrors':
      return t('recalcProgress.finishedWithErrors', {
        project,
        recalculated: progress.recalculated,
        skipped: progress.skipped,
        failed: progress.failed,
      });
    default:
      return t('recalcProgress.failed', { project });
  }
}

/**
 * ✎ a5 (приймання 08.11, P3): назва рядка перерахунку ПРОЄКТУ — «Project recalculation <CODE>», як у «Моїх задачах»;
 * у переліку задач батько називався «Recalculation», так само як його документні діти. `null` — не батько розкладу.
 */
export function projectRecalcTitle(job: JobSummary): string | null {
  return isProjectRecalculation(job) ? t('recalcProgress.myTaskTitle', { project: progressOf(job).projectCode }) : null;
}

/** Скільки годин завершений перерахунок лишається на банері (далі — лише в «Моїх задачах»). */
export const FinishedVisibleHours = 24;

/** Чи завершений перерахунок ще варто показувати банером. */
export function isRecentlyFinished(progress: RecalcProgress, now: number): boolean {
  const at = Date.parse(progress.updatedAt);

  return !Number.isNaN(at) && now - at <= FinishedVisibleHours * 60 * 60 * 1000;
}

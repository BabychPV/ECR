import { useEffect, useRef } from 'react';
import { useQueryClient, type UseQueryResult } from '@tanstack/react-query';
import type { JobStatus } from '@/api/types';
import { useJobStatus } from '@/features/jobs/useJobStatus';
import { t } from '@/shared/i18n';
import { showDone, showWarning } from '@/shared/ui/notify';

/**
 * Завершення фонового переносу версії шаблону (D-2 RC15B): опитує задачу, а коли вона завершилась -
 * інвалідує кеші й показує підсумок.
 *
 * ⛔ Живе на рівні СТОРІНКИ (`useVersionMigrationAction`), а не діалогу: діалог можна закрити, поки перенос
 * іде ("можна закрити, задача лишається в My tasks"). Якби інвалідація й тост були в діалозі, після його
 * закриття сітки лишалися б на старій структурі проєкту, а людина не побачила б підсумку.
 *
 * ⚠ Структура ВСІХ документів проєкту змінилась (аркуші, таблиці, колонки) - вузька інвалідація ризикувала б
 * лишити засталу сітку, тому скидається увесь кеш запитів. Стан задачі - той самий запит `['job', id]`, що
 * його читає діалог: додаткових запитів немає.
 *
 * @param jobId Задача переносу; `null` - переносу немає.
 * @param dialogOpen Діалог відкрито: причину відмови він уже показує сам, тост не потрібен.
 * @param onSucceeded Перенос завершено успішно (кеші вже скинуто): закрити діалог, забути задачу.
 */
export function useMigrationJobCompletion(
  jobId: string | null,
  dialogOpen: boolean,
  onSucceeded: () => void,
): UseQueryResult<JobStatus> {
  const queryClient = useQueryClient();
  const job = useJobStatus(jobId);
  const state = job.data?.state ?? null;
  const message = job.data?.message ?? null;
  const error = job.data?.error ?? null;

  // Остання актуальна пара "діалог відкрито / колбек" без повторного запуску ефекту на кожен рендер.
  const latest = useRef({ dialogOpen, onSucceeded });
  latest.current = { dialogOpen, onSucceeded };
  const handled = useRef<string | null>(null);

  useEffect(() => {
    if (jobId === null || handled.current === jobId) return;

    if (state === 'Succeeded') {
      handled.current = jobId;
      void queryClient.invalidateQueries().then(() => {
        showDone(message ?? t('documents.migrateVersionTitle'));
        latest.current.onSucceeded();
      });
    } else if (state === 'Failed' || state === 'Cancelled') {
      handled.current = jobId;
      if (!latest.current.dialogOpen) {
        showWarning(t('documents.migrateJobFailed', { reason: error ?? '' }));
      }
    }
  }, [jobId, state, message, error, queryClient]);

  return job;
}

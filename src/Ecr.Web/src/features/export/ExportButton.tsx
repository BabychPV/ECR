import { useEffect, useRef, useState, type JSX } from 'react';
import { Anchor, Button, Group } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQuery } from '@tanstack/react-query';
import { apiEnqueue, apiFetch } from '@/api/client';
import type { ExportRequest, JobStatus } from '@/api/types';
import { outcomeOf, pollInterval } from '@/features/workflow/jobFollow';
import { showApiError } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/** Що і за який період експортувати. */
export interface ExportButtonProps {
  /** Документ. */
  documentId: number;
  /** Період. */
  periodKey: number;
  /** Мова книги; береться з профілю. */
  language: string;
}

/**
 * Експорт у `.xlsx` разом із **посиланням на готовий файл**.
 *
 * ⛔ Побудова повертає `202` з `jobId`, а не файл: книга на 500×60×12
 * будується довше за будь-який розумний таймаут проксі. Але до аудиту на
 * цьому все й закінчувалося — екран показував «поставлено в чергу як
 * `4f2c…`», і **забрати книгу було нічим**: адреса `GET /documents/{id}/export/{exportId}`
 * не мала в клієнті жодного споживача, а екран задач знає лише про `jobId`.
 * Користувач отримував GUID і не мав куди його ввести.
 *
 * ⚠ Ключ файлу приходить у повідомленні прогресу на 100 % — так його передає
 * `ExcelExportJob`. Тому тут не «показати jobId», а дочекатися завершення і
 * дати посилання.
 *
 * ⚠ Посилання — звичайне `<a>`, а не завантаження через `fetch`: автентифікація
 * на cookie, і навігація тим самим походженням несе її сама. Обгортка через
 * `fetch` + `blob:` дала б ту саму книгу вдвічі дорожче і зламала б «зберегти
 * як» у браузері.
 *
 * ⛔ Q-234: опитування задачі перевикористовує `jobFollow.ts` (`pollInterval`,
 * `outcomeOf`), а не власну копію правила. До цього тут була саме та копія, з
 * якої `jobFollow.ts` і виник (коментар там прямо посилався на ці рядки як на
 * зразок) — але сама кнопка лишилася на старій версії й не отримала фікс,
 * заради якого модуль узагалі виділили: `outcomeOf` розрізняє «немає відповіді»
 * і «відповідь — відмова» (`job.isError`, той самий `Q-156` — `GET /jobs/{id}`
 * вимагає `System.ViewHealth`). Стара умова `job.data?.state !== 'Failed'`
 * на відмові читання залишала `job.data` порожнім НАЗАВЖДИ: `building`
 * лишався `true`, `refetchInterval` бачив `undefined` замість `'Queued'` й
 * зупиняв опитування — кнопка крутилася «Формується…» вічно, без жодного
 * повідомлення, і побудований файл забрати було нічим. Не гіпотетично: та сама
 * пастка, від якої `outcomeOf` захищає «Перерахувати» на `PeriodsPage`, тут
 * стояла незахищеною поруч.
 */
export function ExportButton({
  documentId,
  periodKey,
  language,
}: ExportButtonProps): JSX.Element {
  const [jobId, setJobId] = useState<string | null>(null);

  const start = useMutation({
    mutationFn: () =>
      apiEnqueue(`/api/v1/documents/${documentId}/export`, {
        includeFormulas: true,
        includeStyles: true,
        language,
        periodKey,
      } satisfies ExportRequest),
    onSuccess: (job) => setJobId(job.jobId),
    onError: showApiError,
  });

  const job = useQuery({
    queryKey: ['job', jobId],
    queryFn: () => apiFetch<JobStatus>(`/api/v1/jobs/${encodeURIComponent(jobId ?? '')}`),
    enabled: jobId !== null,
    refetchInterval: (query) => pollInterval(query.state.data?.state),
    retry: false,
  });

  const outcome = jobId === null ? null : outcomeOf(job.data?.state, job.isError);
  const done = outcome === 'succeeded';
  const exportId = done ? (job.data?.message ?? '') : '';
  const building = outcome === 'running';

  // ⚠ Повідомлення про відмову — ОДИН раз на задачу, а не на кожен рендер:
  // `job.data` не змінюється після кінцевого стану, і без захисту `ref`
  // тост показувався б повторно щоразу, коли компонент перемальовується з
  // будь-якої іншої причини (наприклад, зміна `periodKey` сусіднього select).
  const reported = useRef<string | null>(null);

  useEffect(() => {
    if (jobId === null || outcome === null || outcome === 'running') return;
    if (reported.current === jobId) return;

    reported.current = jobId;

    // ⛔ `unknown` (стан прочитати не вдалося, `Q-156`) навмисно без тосту:
    // причина — брак права на читання задачі, а не збій експорту, і показ
    // помилки тут звинуватив би експорт у тому, чого він не робив.
    if (outcome === 'failed') {
      // ⛔ `error`, а не `message`: перше несе причину відмови
      // (`FinishAsync(..., errorMessage: ex.Message, ...)`), друге — останній
      // прогрес (`IJobProgress.ReportAsync`), який на відмові лишається тим,
      // яким був до неї, — часто порожнім або застарілим текстом «Виконується».
      notifications.show({
        color: 'statusError',
        message: job.data?.error ?? t('document.exportFailed'),
      });
    }
  }, [jobId, outcome, job.data?.error]);

  return (
    <Group gap="xs">
      <Button
        size="xs"
        variant="default"
        loading={start.isPending || building}
        onClick={() => start.mutate()}
      >
        {building ? t('document.exportBuilding') : t('document.export')}
      </Button>

      {/* ⚠ Посилання з'являється лише тоді, коли файл справді є. Показане
          заздалегідь, воно вело б на 404 рівно доти, доки книга будується, —
          тобто саме тоді, коли на нього тиснуть. */}
      {done && exportId.length > 0 && (
        <Anchor
          size="sm"
          href={`/api/v1/documents/${documentId}/export/${encodeURIComponent(exportId)}`}
          download
        >
          {t('document.exportReady')}
        </Anchor>
      )}
    </Group>
  );
}

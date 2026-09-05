import { useState, type JSX } from 'react';
import { Anchor, Button, Group } from '@mantine/core';
import { useMutation, useQuery } from '@tanstack/react-query';
import { apiEnqueue, apiFetch } from '@/api/client';
import type { ExportRequest, JobStatus } from '@/api/types';
import { showApiError } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/** Як часто питати стан побудови книги. */
const PollMs = 1500;

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

    // ⚠ Опитування зупиняється, щойно задача завершилася: нескінченне
    // опитування готової книги — це запит на секунду від кожної вкладки.
    refetchInterval: (query) => {
      const state = query.state.data?.state;

      return state === 'Queued' || state === 'Running' ? PollMs : false;
    },
    retry: false,
  });

  const done = job.data?.state === 'Succeeded';
  const exportId = done ? (job.data?.message ?? '') : '';
  const building = jobId !== null && !done && job.data?.state !== 'Failed';

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

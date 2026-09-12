import { useState, type JSX } from 'react';
import { Badge, Button, Card, Group, Progress, Stack, Table, Text, TextInput } from '@mantine/core';
import { useMutation, useQuery } from '@tanstack/react-query';
import { apiEnqueue, apiFetch } from '@/api/client';
import type { JobStatus, JobSummary } from '@/api/types';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { useUrlState } from '@/shared/ui/useUrlState';
import { showApiError } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/** Як часто опитувати стан задачі, поки вона виконується. */
const PollMs = 1500;

/**
 * Стеження за фоновою задачею.
 *
 * ⚠ Опитування зупиняється, щойно задача завершилася. Нескінченне опитування
 * завершеної задачі — це запит на секунду від кожної відкритої вкладки, і
 * саме воно перетворює нешкідливий екран на постійне навантаження.
 *
 * ⚠ Невідомий ідентифікатор дає 404, а не порожній стан: інакше клієнт
 * показував би вічний прогрес задачі, якої не існує.
 */
export function JobsPage(): JSX.Element {
  const [input, setInput] = useState('');
  // ⛔ Ідентифікатор задачі — в адресі. Саме це посилання надсилають
  // адміністраторові зі словами «подивись, чому впало»; без нього доводиться
  // диктувати GUID голосом.
  const [jobId, setJobId] = useUrlState('id');

  const job = useQuery({
    queryKey: ['job', jobId],
    queryFn: () => apiFetch<JobStatus>(`/api/v1/jobs/${encodeURIComponent(jobId ?? '')}`),
    enabled: jobId !== null,
    refetchInterval: (query) => {
      const state = query.state.data?.state;

      return state === 'Queued' || state === 'Running' ? PollMs : false;
    },
    retry: false,
  });

  // ⛔ Директива №11, T10 #40. До цього ендпоінта провалена задача, чию
  // причину вже полагодили (недоступне джерело, зайняте з'єднання), можна
  // було повторити лише поставивши НОВУ — і зв'язок зі старим прогресом,
  // на який уже дивиться колега, губився.
  const restart = useMutation({
    mutationFn: () => apiEnqueue(`/api/v1/jobs/${encodeURIComponent(jobId ?? '')}/restart`),
    // ⚠ Той самий jobId — не новий. `refetch`, а не інвалідація: опитування
    // саме підхопить `Queued` і продовжить, як після першої постановки.
    onSuccess: () => void job.refetch(),
    onError: showApiError,
  });

  return (
    <>
      <PageHeader title={t('jobs.title')} />

      <Group align="end" mb="md">
        <TextInput
          label={t('jobs.id')}
          value={input}
          onChange={(event) => setInput(event.currentTarget.value)}
          // ⚠ Без цього Enter у полі не робив нічого — ідентифікатор задачі
          // найчастіше приходить вставленим із чужого повідомлення
          // («подивись, чому впало»), і природний наступний рух — Enter, не
          // потяг миші до кнопки. Той самий обробник, що й клік «Дивитись»:
          // одна дія, два способи її викликати, не дві копії логіки.
          onKeyDown={(event) => {
            if (event.key === 'Enter') setJobId(input.trim().length === 0 ? null : input.trim());
          }}
          miw={280}
          flex="1"
        />
        <Button onClick={() => setJobId(input.trim().length === 0 ? null : input.trim())}>
          {t('jobs.watch')}
        </Button>
      </Group>

      {/*
       * ⚠ Доки ідентифікатор не введено, `data` — `undefined`: обгортка каже
       * «введіть ідентифікатор», а не «задачі немає». Невідомий ідентифікатор
       * дає 404 і показується станом помилки з кодом — саме тому клієнт не
       * малює вічний прогрес задачі, якої не існує.
       */}
      <AsyncBoundary<JobStatus>
        isPending={jobId !== null && job.isPending}
        error={job.error}
        data={jobId === null ? undefined : job.data}
        emptyTitle={t('jobs.pick')}
        emptyHint={t('jobs.pickHint')}
        onRetry={() => void job.refetch()}
      >
        {(status) => (
        <Card withBorder>
          <Stack gap="xs">
            <Group justify="space-between">
              <Text fw={600}>{status.jobId}</Text>
              <Badge color={stateColor(status.state)}>{status.state}</Badge>
            </Group>

            <Progress value={status.percent} animated={status.state === 'Running'} />

            {status.message !== null && <Text size="sm">{status.message}</Text>}

            {/* ⛔ Текст помилки — без стека (ФВ-6.11): стек виносить назовні
                шляхи, імена і подекуди значення. */}
            {status.error !== null && (
              <Text size="sm" c="statusError">
                {status.error}
              </Text>
            )}

            {/* ⛔ Лише для Failed: перезапускати задачу, що виконується чи вже
                успішна, немає сенсу — і сервер (ECR-JOB-0409) це відхилить. */}
            {status.state === 'Failed' && (
              <Group justify="flex-end">
                <Button
                  size="xs"
                  variant="default"
                  loading={restart.isPending}
                  onClick={() => restart.mutate()}
                >
                  {restart.isPending ? t('jobs.restarting') : t('jobs.restart')}
                </Button>
              </Group>
            )}
          </Stack>
        </Card>
        )}
      </AsyncBoundary>

      {/*
       * ⛔ До цього розділу задачу можна було побачити лише знаючи її GUID:
       * збій перерахунку існував у базі й був НЕДОСЯЖНИЙ з інтерфейсу
       * (директива №09 §6.5, `S-25`; `ФВ-12.4`). Перелік — журнал того, що
       * ЩОЙНО сталося, а не архів: рядок клацається і підставляє id вище.
       */}
      <RecentJobs onPick={setJobId} />
    </>
  );
}

/** Опитувати перелік, доки на екрані є задача не в кінцевому стані. */
const ListPollMs = 3000;

function RecentJobs({ onPick }: { onPick: (jobId: string) => void }): JSX.Element {
  const jobs = useQuery({
    queryKey: ['jobs'],
    queryFn: () => apiFetch<JobSummary[]>('/api/v1/jobs'),
    refetchInterval: (query) => {
      const list = query.state.data ?? [];

      return list.some((j) => j.state === 'Queued' || j.state === 'Running') ? ListPollMs : false;
    },
  });

  return (
    <AsyncBoundary<JobSummary[]>
      isPending={jobs.isPending}
      error={jobs.error}
      data={jobs.data}
      isEmpty={(list) => list.length === 0}
      emptyTitle={t('jobs.recentEmpty')}
      onRetry={() => void jobs.refetch()}
    >
      {(list) => (
        <Table>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('jobs.recentCode')}</Table.Th>
              <Table.Th>{t('jobs.recentState')}</Table.Th>
              <Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {list.map((job) => (
              <Table.Tr key={job.jobId}>
                <Table.Td>{job.jobCode}</Table.Td>
                <Table.Td>
                  <Badge color={stateColor(job.state)}>{job.state}</Badge>
                </Table.Td>
                <Table.Td>
                  <Button variant="subtle" size="xs" onClick={() => onPick(job.jobId)}>
                    {t('jobs.recentWatch')}
                  </Button>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}
    </AsyncBoundary>
  );
}

function stateColor(state: string): string {
  switch (state) {
    case 'Succeeded':
      return 'statusSuccess';
    case 'Failed':
      return 'statusError';
    case 'Cancelled':
      return 'gray';
    default:
      return 'blue';
  }
}

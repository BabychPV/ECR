import { useState, type JSX } from 'react';
import { Badge, Button, Card, Group, Progress, Stack, Text, TextInput } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { JobStatus } from '@/api/types';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { useUrlState } from '@/shared/ui/useUrlState';
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

  return (
    <>
      <PageHeader title={t('jobs.title')} />

      <Group align="end" mb="md">
        <TextInput
          label={t('jobs.id')}
          value={input}
          onChange={(event) => setInput(event.currentTarget.value)}
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
              <Text size="sm" c="red">
                {status.error}
              </Text>
            )}
          </Stack>
        </Card>
        )}
      </AsyncBoundary>
    </>
  );
}

function stateColor(state: string): string {
  switch (state) {
    case 'Succeeded':
      return 'green';
    case 'Failed':
      return 'red';
    case 'Cancelled':
      return 'gray';
    default:
      return 'blue';
  }
}

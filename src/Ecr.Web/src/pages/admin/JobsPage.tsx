import { useState, type JSX } from 'react';
import { Badge, Button, Card, Group, Progress, Stack, Text, TextInput } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PageHeader } from '@/shared/ui/PageHeader';
import { t } from '@/shared/i18n';

/** Стан задачі; форма з `IBackgroundJobScheduler.JobStatus`. */
interface JobStatus {
  jobId: string;
  /** `Queued`, `Running`, `Succeeded`, `Failed`, `Cancelled`. */
  state: string;
  percent: number;
  message: string | null;
  error: string | null;
}

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
  const [jobId, setJobId] = useState<string | null>(null);

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
          w={420}
        />
        <Button onClick={() => setJobId(input.trim().length === 0 ? null : input.trim())}>
          {t('jobs.watch')}
        </Button>
      </Group>

      <ErrorAlert error={job.error} />

      {job.data !== undefined && (
        <Card withBorder>
          <Stack gap="xs">
            <Group justify="space-between">
              <Text fw={600}>{job.data.jobId}</Text>
              <Badge color={stateColor(job.data.state)}>{job.data.state}</Badge>
            </Group>

            <Progress value={job.data.percent} animated={job.data.state === 'Running'} />

            {job.data.message !== null && <Text size="sm">{job.data.message}</Text>}

            {/* ⛔ Текст помилки — без стека (ФВ-6.11): стек виносить назовні
                шляхи, імена і подекуди значення. */}
            {job.data.error !== null && (
              <Text size="sm" c="red">
                {job.data.error}
              </Text>
            )}
          </Stack>
        </Card>
      )}
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

import type { JSX } from 'react';
import { Badge, Button, Group, Loader, Table, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQuery } from '@tanstack/react-query';
import { EcrApiError, apiEnqueue, apiFetch } from '@/api/client';
import { can, useSession } from '@/shared/session/useSession';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PageHeader } from '@/shared/ui/PageHeader';
import { t } from '@/shared/i18n';

interface SourceEntityDto {
  id: number;
  code: string;
  displayName: string | null;
  entityPath: string | null;
  /** `PiWebApi` або `PiSqlClient` — транспорт джерела (ФВ-11.2). */
  transport: string;
  isActive: boolean;
  /** Останній прогін збору; `null` — не збирали жодного разу. */
  lastRun: { finishedAt: string | null; status: string; pointsRetrieved: number } | null;
  /** Найстаріша непокрита прогалина; `null` — покриття суцільне. */
  oldestGap: string | null;
}

/**
 * Конфігуратор джерел і ручний запуск збору.
 *
 * ⛔ Запису в зовнішнє джерело тут немає і не буде: PI AF — **виключно
 * джерело** (D-44).
 *
 * ⚠ Колонка «прогалина» важливіша за колонку «останній прогін». Ознака
 * здоров'я інтеграції — журнал покриття, а не тиша: джерело, яке щоночі
 * успішно віддає нуль точок, і джерело, яке віддає дані, ззовні виглядають
 * однаково (ІНТ-3.3).
 */
export function SourcesPage(): JSX.Element {
  const session = useSession();

  const sources = useQuery({
    queryKey: ['sources'],
    queryFn: () => apiFetch<SourceEntityDto[]>('/api/v1/sources'),
  });

  const collect = useMutation({
    mutationFn: (id: number) => {
      const to = new Date();
      const from = new Date(to.getTime() - 7 * 24 * 60 * 60 * 1000);

      return apiEnqueue(`/api/v1/sources/${id}/collect`, {
        fromUtc: from.toISOString(),
        toUtc: to.toISOString(),
      });
    },
    onSuccess: (job) => {
      // ⚠ 202 з jobId: збір ходить по мережі до чужої системи, і його
      // тривалість визначає не наш код.
      notifications.show({ message: t('sources.queued', { job: job.jobId }) });
    },
    onError: (error) => {
      notifications.show({
        color: 'red',
        message: error instanceof EcrApiError ? error.message : String(error),
      });
    },
  });

  return (
    <>
      <PageHeader title={t('sources.title')} />
      <ErrorAlert error={sources.error} />

      {sources.isPending ? (
        <Loader />
      ) : (
        <Table striped highlightOnHover>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('sources.entity')}</Table.Th>
              <Table.Th>{t('sources.transport')}</Table.Th>
              <Table.Th>{t('sources.lastRun')}</Table.Th>
              <Table.Th>{t('sources.gap')}</Table.Th>
              <Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {(sources.data ?? []).map((source) => (
              <Table.Tr key={source.id} opacity={source.isActive ? 1 : 0.5}>
                <Table.Td>
                  {source.displayName ?? source.code}
                  <Text size="xs" c="dimmed">
                    {source.entityPath ?? source.code}
                  </Text>
                </Table.Td>
                <Table.Td>
                  <Badge variant="light">{source.transport}</Badge>
                </Table.Td>
                <Table.Td>
                  {source.lastRun === null ? (
                    <Text c="dimmed">{t('sources.never')}</Text>
                  ) : (
                    <Group gap={4}>
                      <Badge
                        variant="light"
                        color={source.lastRun.status === 'Succeeded' ? 'green' : 'orange'}
                      >
                        {source.lastRun.status}
                      </Badge>
                      <Text size="xs">{source.lastRun.pointsRetrieved}</Text>
                    </Group>
                  )}
                </Table.Td>
                <Table.Td>
                  {source.oldestGap === null ? (
                    <Text c="dimmed">—</Text>
                  ) : (
                    <Badge color="red" variant="light">
                      {source.oldestGap}
                    </Badge>
                  )}
                </Table.Td>
                <Table.Td>
                  {can(session.data, 'Integration.Manage') && (
                    <Button
                      size="compact-xs"
                      variant="default"
                      loading={collect.isPending}
                      onClick={() => collect.mutate(source.id)}
                    >
                      {t('sources.collect')}
                    </Button>
                  )}
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}
    </>
  );
}

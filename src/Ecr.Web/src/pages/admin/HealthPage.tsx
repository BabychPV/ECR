import type { JSX } from 'react';
import { Badge, Card, Group, Loader, SimpleGrid, Stack, Table, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PageHeader } from '@/shared/ui/PageHeader';
import { t } from '@/shared/i18n';

interface HealthEntry {
  status: string;
  description: string | null;
  data: Record<string, unknown>;
}

interface HealthReport {
  status: string;
  entries: Record<string, HealthEntry>;
}

/**
 * Операційний дашборд.
 *
 * ⚠ `/health/db` віддає **подробиці**: редакцію SQL Server, стан RCSI,
 * файлові групи, запас партицій і перелік того, що в цьому режимі недоступне
 * (АРХ-7 п. 5). Зведений «зелений» без цих полів був би обіцянкою, яку не
 * можна перевірити: система на Express формально жива і при цьому не вміє
 * половини того, на що розрахований регламент.
 */
export function HealthPage(): JSX.Element {
  const ready = useQuery({
    queryKey: ['health', 'ready'],
    queryFn: () => apiFetch<HealthReport>('/health/ready'),
    refetchInterval: 30_000,
  });

  const db = useQuery({
    queryKey: ['health', 'db'],
    queryFn: () => apiFetch<HealthReport>('/health/db'),
    refetchInterval: 60_000,
  });

  return (
    <>
      <PageHeader
        title={t('health.title')}
        actions={
          ready.data === undefined ? null : (
            <Badge color={ready.data.status === 'Healthy' ? 'green' : 'red'}>
              {ready.data.status}
            </Badge>
          )
        }
      />

      <ErrorAlert error={ready.error ?? db.error} />

      {ready.isPending ? (
        <Loader />
      ) : (
        <SimpleGrid cols={{ base: 1, md: 3 }} mb="lg">
          {Object.entries(ready.data?.entries ?? {}).map(([name, entry]) => (
            <Card key={name} withBorder>
              <Group justify="space-between">
                <Text fw={600}>{name}</Text>
                <Badge color={entry.status === 'Healthy' ? 'green' : 'red'} variant="light">
                  {entry.status}
                </Badge>
              </Group>
              {entry.description !== null && (
                <Text size="sm" mt="xs">
                  {entry.description}
                </Text>
              )}
            </Card>
          ))}
        </SimpleGrid>
      )}

      <Stack gap="xs">
        <Text fw={600}>{t('health.database')}</Text>

        {/* ⚠ Обмеження режиму показуються переліком, а не ховаються: саме за
            ними видно, чому вночі не працює архівація або чому немає запасу
            партицій. */}
        <Table striped withTableBorder>
          <Table.Tbody>
            {Object.entries(db.data?.entries['db']?.data ?? {}).map(([key, value]) => (
              <Table.Tr key={key}>
                <Table.Td w={280}>{key}</Table.Td>
                <Table.Td>{String(value)}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Stack>
    </>
  );
}

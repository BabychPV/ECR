import { useState, type JSX } from 'react';
import { Badge, Loader, NumberInput, Table, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { PeriodCalendarDto } from '@/api/types';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PageHeader } from '@/shared/ui/PageHeader';
import { t } from '@/shared/i18n';

/**
 * Календар періодів проєкту.
 *
 * ⚠ Стан періоду **обчислюється з часу і зсувів**, а не зберігається полем:
 * збережений статус розійшовся б із календарем рівно тоді, коли фонова задача
 * не спрацювала. Тому екран показує те, що віддає сервер, і не рахує сам.
 *
 * ⚠ `PeriodKey = Year*100 + Sequence` (R-A6), і `Sequence` — це **порядковий
 * номер періоду**, а не місяць: у квартальному проєкті їх чотири.
 */
export function PeriodsPage(): JSX.Element {
  const [projectId, setProjectId] = useState<number | null>(null);

  const periods = useQuery({
    queryKey: ['periods', projectId],
    queryFn: () => apiFetch<PeriodCalendarDto>(`/api/v1/projects/${projectId ?? 0}/periods`),
    enabled: projectId !== null,
  });

  return (
    <>
      <PageHeader
        title={t('periods.title')}
        actions={
          <NumberInput
            size="xs"
            w={160}
            placeholder={t('periods.project')}
            value={projectId ?? ''}
            onChange={(value) => setProjectId(typeof value === 'number' ? value : null)}
          />
        }
      />

      <ErrorAlert error={periods.error} />

      {projectId === null ? (
        <Text c="dimmed">{t('periods.pickProject')}</Text>
      ) : periods.isPending ? (
        <Loader />
      ) : (
        <Table striped>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('periods.key')}</Table.Th>
              <Table.Th>{t('periods.sequence')}</Table.Th>
              <Table.Th>{t('periods.range')}</Table.Th>
              <Table.Th>{t('periods.state')}</Table.Th>
              <Table.Th>{t('periods.grace')}</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {(periods.data?.periods ?? []).map((period) => (
              <Table.Tr key={period.periodKey}>
                <Table.Td>{period.periodKey}</Table.Td>
                <Table.Td>{period.sequence}</Table.Td>
                <Table.Td>
                  {period.startsAt} — {period.endsAt}
                </Table.Td>
                <Table.Td>
                  <Badge color={stateColor(period.state)} variant="light">
                    {period.state}
                  </Badge>
                </Table.Td>
                <Table.Td>{period.graceEndsAt ?? '—'}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}
    </>
  );
}

/**
 * Колір стану.
 *
 * ⚠ `Grace` виділено окремим кольором, а не зведено до «відкритого»: правка в
 * пільговому строку позначається як пізня (D-70) і виглядає в аудиті інакше.
 */
function stateColor(state: string): string {
  switch (state) {
    case 'Open':
      return 'green';
    case 'Grace':
      return 'yellow';
    case 'Closed':
      return 'gray';
    default:
      return 'blue';
  }
}

import { useState, type JSX } from 'react';
import { Anchor, Badge, Group, Loader, Select, Stack, Table, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { SourceEntityStatus } from '@/api/types';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { CollectionScheduleTab } from './CollectionScheduleTab';
import type { DataSource } from './dataSourceApi';
import { useCollectionSchedules } from './useCollectionSchedules';

/**
 * Вкладка «Schedule» шухляди з'єднання (`UI-09`, `ФВ-14.3`).
 *
 * ⚠ Розклад належить СУТНОСТІ джерела, а не з'єднанню
 * (`CreateCollectionScheduleRequest.sourceEntityId`). Тому вкладка — перелік
 * розкладів цього з'єднання (сервер фільтрує `?dataSource=<code>`) і вибір
 * однієї сутності, для якої наявний `CollectionScheduleTab` дає створити,
 * змінити чи прибрати розклад. Не форма на кожну сутність: у з'єднання їх
 * буває тисячі, і тисяча форм — це і повільно, і нечитабельно.
 *
 * ⚠ Сутності беруться з того самого запиту `['sources']`, що й таблиця над
 * з'єднаннями, — окремого звернення вкладка не робить.
 *
 * ⛔ `L10`: відмова читання розкладів — `ErrorAlert`, а не «розкладів немає».
 */
export function DataSourceScheduleTab({ source }: { readonly source: DataSource }): JSX.Element {
  const schedules = useCollectionSchedules(source.code);

  const entities = useQuery({
    queryKey: ['sources'],
    queryFn: () => apiFetch<SourceEntityStatus[]>('/api/v1/sources'),
  });

  const [selected, setSelected] = useState<number | null>(null);

  if (schedules.isError) {
    return <ErrorAlert error={schedules.error} onRetry={() => void schedules.refetch()} />;
  }

  if (entities.isError) {
    return <ErrorAlert error={entities.error} onRetry={() => void entities.refetch()} />;
  }

  if (schedules.isPending || entities.isPending) return <Loader size="sm" />;

  const own = entities.data.filter((entity) => entity.dataSourceCode === source.code);
  const label = (entity: SourceEntityStatus): string => entity.displayName ?? entity.code;

  return (
    <Stack gap="sm" data-schedule-tab={source.code}>
      {schedules.data.length === 0 ? (
        <Text size="sm" c="dimmed" data-schedules-empty="">
          {t('sources.schedulesNone')}
        </Text>
      ) : (
        <Table data-schedules="">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('sources.entity')}</Table.Th>
              <Table.Th>{t('schedule.cron')}</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {schedules.data.map((schedule) => (
              <Table.Tr key={schedule.id} data-schedule-row={schedule.sourceEntityCode}>
                <Table.Td>
                  {/* Кнопка, а не клац по рядку: з клавіатури рядок недосяжний. */}
                  <Anchor
                    component="button"
                    type="button"
                    size="sm"
                    onClick={() => setSelected(schedule.sourceEntityId)}
                  >
                    {schedule.sourceEntityName ?? schedule.sourceEntityCode}
                  </Anchor>
                </Table.Td>
                <Table.Td>
                  <Group gap="xs">
                    <Text size="sm" ff="monospace">
                      {schedule.cron}
                    </Text>
                    {!schedule.isEnabled && (
                      <Badge variant="outline" color="gray">
                        {t('sources.scheduleOff')}
                      </Badge>
                    )}
                  </Group>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}

      {own.length === 0 ? (
        <Text size="sm" c="dimmed">
          {t('sources.scheduleNoEntities')}
        </Text>
      ) : (
        <Select
          label={t('sources.scheduleEntity')}
          data={own.map((entity) => ({ value: String(entity.id), label: label(entity) }))}
          value={selected === null ? null : String(selected)}
          onChange={(value) => setSelected(value === null ? null : Number(value))}
          searchable
        />
      )}

      {selected !== null && (
        // ⚠ Нова сутність — нова форма: чернетка однієї не переходить в іншу.
        <CollectionScheduleTab key={selected} sourceEntityId={selected} dataSource={source.code} />
      )}
    </Stack>
  );
}

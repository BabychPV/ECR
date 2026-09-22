import { Suspense, useState, type JSX, type MouseEvent } from 'react';
import { Anchor, Badge, Button, Group, Stack, Title } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { t } from '@/shared/i18n';
import { can, useSession } from '@/shared/session/useSession';
import { DataTable } from '@/shared/ui/DataTable';
import { useDetailPanel } from '@/shared/ui/DetailDrawer';
import { TwoLine } from '@/shared/ui/TwoLine';
import { DataSourceDrawer, dataSourceName } from './DataSourceDrawer';
import { DataSourceFormModal } from './lazyDataSourceForm';
import { listDataSources, type DataSource } from './dataSourceApi';
import { DataSourcesQueryKey } from './dataSourcesKey';

/**
 * Перелік З'ЄДНАНЬ (джерел даних, `ФВ-14.3`) на екрані `/admin/sources`
 * (директива №15 §3 `UI-09`).
 *
 * ⛔ Не плутати з таблицею над ним: там СУТНОСТІ збору (`/api/v1/sources`),
 * тобто що саме збирається; тут — звідки й через що. Одна сутність належить
 * одному з'єднанню, тож лічильник `sourceEntities` — міст між двома таблицями.
 *
 * ⚠ Подробиці рядка — у шухляді за `?panel=<code>` (`useDetailPanel`), а не в
 * локальному стані: посилання на з'єднання можна надіслати колезі.
 */
export function DataSourcesTable(): JSX.Element {
  const session = useSession();
  const [panel, setPanel] = useDetailPanel();
  const [creating, setCreating] = useState(false);

  // ⛔ `Integration.Manage`: без нього кнопок правки НЕМАЄ, а не вимкнені —
  // сервер відповів би `403` на кожну.
  const canManage = can(session.data, 'Integration.Manage');

  const sources = useQuery({ queryKey: DataSourcesQueryKey, queryFn: listDataSources });

  // ⛔ Шухляда відкривається лише для з'єднання, яке справді є в переліку:
  // застарілий `?panel=` не дає порожньої шухляди з назвою-кодом.
  const opened = panel === null ? undefined : sources.data?.find((source) => source.code === panel);

  const open = (source: DataSource): void => {
    setPanel(source.code);
  };

  return (
    <Stack gap="sm" data-data-sources="">
      <Group justify="space-between" gap="sm">
        <Title order={2} size="h4">
          {t('sources.connections')}
        </Title>

        {/* ⚠ Єдина primary-кнопка в `main`: «Collect» у рядках — `default`. */}
        {canManage && (
          <Button onClick={() => setCreating(true)} data-new-connection="">
            {t('sources.newConnection')}
          </Button>
        )}
      </Group>

      <DataTable<DataSource>
        columns={[
          {
            key: 'name',
            label: t('sources.connection'),
            minWidth: 220,
            sortValue: dataSourceName,
            render: (source) => (
              <TwoLine
                primary={
                  /*
                   * ⛔ Кнопка, а не лише клац по `<tr>`: рядок таблиці не
                   * фокусується, тож без неї шухляда з клавіатури недосяжна.
                   *
                   * ⚠ `stopPropagation` — не косметика: інакше той самий клац
                   * дійшов би до `onRowClick`, і два синхронні записи
                   * `?panel=` поспіль губляться ОБИДВА (`useUrlState.ts`).
                   */
                  <Anchor
                    component="button"
                    type="button"
                    size="sm"
                    onClick={(event: MouseEvent) => {
                      event.stopPropagation();
                      open(source);
                    }}
                  >
                    {dataSourceName(source)}
                  </Anchor>
                }
                secondary={source.code}
                mono
              />
            ),
          },
          {
            key: 'transport',
            label: t('sources.transport'),
            render: (source) => <Badge variant="light">{source.transport}</Badge>,
          },
          {
            key: 'isActive',
            label: t('sources.state'),
            render: (source) =>
              source.isActive ? (
                <Badge variant="light" color="statusSuccess">
                  {t('sources.active')}
                </Badge>
              ) : (
                <Badge variant="outline" color="gray">
                  {t('sources.inactive')}
                </Badge>
              ),
          },
          // Лічильники — числа за своєю природою, тож форматування розрядів
          // `DataTable` тут доречне.
          { key: 'sourceEntities', label: t('sources.entities'), num: true },
          { key: 'collectionSchedules', label: t('sources.schedules'), num: true },
        ]}
        rows={sources.data}
        rowKey={(source) => source.code}
        isPending={sources.isPending}
        error={sources.error}
        onRetry={() => void sources.refetch()}
        emptyTitle={t('sources.connectionsEmpty')}
        emptyHint={t('sources.connectionsEmptyHint')}
        onRowClick={open}
        selectedKey={panel ?? undefined}
        rowLabel={(source) =>
          source.isActive ? null : `${dataSourceName(source)} — ${t('sources.inactive')}`
        }
      />

      {opened !== undefined && (
        <DataSourceDrawer
          source={opened}
          canManage={canManage}
          // Видалене з'єднання — закрита шухляда, а не застарілий `?panel=`.
          onDeleted={() => setPanel(null)}
        />
      )}

      {canManage && creating && (
        <Suspense fallback={null}>
          <DataSourceFormModal opened source={null} onClose={() => setCreating(false)} />
        </Suspense>
      )}
    </Stack>
  );
}

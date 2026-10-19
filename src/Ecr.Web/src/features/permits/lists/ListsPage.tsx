import { useMemo, type JSX } from 'react';
import { Alert, Box, Stack, Text } from '@mantine/core';
import { t } from '@/shared/i18n';
import { DataTable } from '@/shared/ui/DataTable';
import { useDetailPanel } from '@/shared/ui/DetailDrawer';
import { FilterBar } from '@/shared/ui/FilterBar';
import { PageHeader } from '@/shared/ui/PageHeader';
import { StatStrip } from '@/shared/ui/StatStrip';
import { useUrlParamsSetter, useUrlState } from '@/shared/ui/useUrlState';
import { PermitsSubnav } from '../shell/PermitsSubnav';
import { ListDrawer } from './ListDrawer';
import { listColumns } from './ListsColumns';
import { filterLists, kindLabel, kindParam, ListKinds, listsStats, type ListRow } from './listsModel';
import { useLists } from './useLists';

/**
 * Списки вибору пермітів (`/admin/permits/lists`, PM-08d; макет `screens-permits.js` екран «Lists»).
 * Будова — як у переліку типів: шапка → смуга розділів → показники → пошук і походження → таблиця;
 * клік по рядку відкриває шторку (`?panel=`). Пошук, походження й показник живуть в адресі.
 *
 * ⚠ Варіант A: списки зведені з визначень типів і довідників; екран ЧИТАЄ. Це сказано рядком
 * `permits.lists.readOnly` під шапкою, а не лишено здогадуватись.
 */
export function ListsPage(): JSX.Element {
  const data = useLists();
  const setParams = useUrlParamsSetter();
  const [query] = useUrlState('q');
  const [kindFilter] = useUrlState('kind');
  const [statParam, setStat] = useUrlState('stat');
  const [panel] = useDetailPanel();

  const stat = kindParam(statParam);
  const kind = stat !== '' ? stat : kindParam(kindFilter);
  const all = useMemo(() => data.rows ?? [], [data.rows]);
  const rows = useMemo(
    () => (data.rows === undefined ? undefined : filterLists(data.rows, { needle: query ?? '', kind })),
    [data.rows, query, kind],
  );
  const stats = useMemo(() => listsStats(all), [all]);
  const columns = useMemo(() => listColumns(), []);
  const opened: ListRow | undefined = all.find((row) => row.id === panel);
  const hasLists = all.length > 0;

  return (
    <Stack gap="md" data-list-page="">
      <PageHeader
        title={t('permits.lists.title')}
        description={t('permits.lists.description')}
        secondary={[{ label: t('permits.lists.openRegistries'), href: '/admin/registries' }]}
      />
      <PermitsSubnav current="lists" />
      <Text size="sm" c="dimmed" data-lists-readonly="">
        {t('permits.lists.readOnly')}
      </Text>

      {data.registriesFailed && (
        <Alert color="statusWarning" role="status" data-lists-registries-failed="">
          {t('permits.lists.registriesFailed')}
        </Alert>
      )}

      {hasLists && (
        <StatStrip
          label={t('permits.lists.stats.aria')}
          active={stat === '' ? null : stat}
          onSelect={setStat}
          items={[
            { id: 'all', label: t('permits.lists.stat.all'), value: stats.all, filter: false },
            { id: 'shared', label: t('permits.lists.stat.shared'), value: stats.shared },
            { id: 'type', label: t('permits.lists.stat.type'), value: stats.type },
            { id: 'permit', label: t('permits.lists.stat.permit'), value: stats.permit },
          ]}
        />
      )}

      {hasLists && (
        <Box data-list-filters="">
          <FilterBar
            search={{ label: t('permits.lists.search.label'), placeholder: t('permits.lists.search.placeholder') }}
            filters={[
              {
                id: 'kind',
                label: t('permits.lists.filter.kind'),
                options: ListKinds.map((value) => ({ value, label: kindLabel(value) })),
              },
            ]}
            clearLabel={t('filters.clear')}
          />
        </Box>
      )}

      <Box data-list-table="">
        <DataTable<ListRow>
          columns={columns}
          rows={rows}
          rowKey={(row) => row.id}
          isPending={data.isPending}
          error={data.error}
          onRetry={data.refetch}
          filtered={(query ?? '').trim().length > 0 || kind !== ''}
          emptyTitle={t('permits.lists.empty.title')}
          emptyHint={t('permits.lists.empty.hint')}
          noMatchTitle={t('permits.lists.noMatches')}
          onClearFilters={() => setParams({ q: null, kind: null, stat: null })}
          clearFiltersLabel={t('filters.clear')}
          onRowClick={(row) => setParams({ panel: row.id })}
        />
      </Box>

      {opened !== undefined && <ListDrawer row={opened} />}
    </Stack>
  );
}

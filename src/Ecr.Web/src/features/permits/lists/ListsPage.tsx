import { useMemo, type JSX } from 'react';
import { Box, Stack, Text } from '@mantine/core';
import { useNavigate } from 'react-router-dom';
import { NaeRegistryCode } from '../air/airApi';
import { t } from '@/shared/i18n';
import { DataTable } from '@/shared/ui/DataTable';
import { useDetailPanel } from '@/shared/ui/DetailDrawer';
import { FilterBar } from '@/shared/ui/FilterBar';
import { PageHeader } from '@/shared/ui/PageHeader';
import { useUrlParamsSetter, useUrlState } from '@/shared/ui/useUrlState';
import { PermitsPaths } from '../shell/permitPaths';
import { PermitsSubnav } from '../shell/PermitsSubnav';
import { ListDrawer } from './ListDrawer';
import { listColumns } from './ListsColumns';
import { filterLists, kindParam, kindShortLabel, ListKinds, listsStats, type ListRow } from './listsModel';
import { useLists } from './useLists';
import './lists.css';

/**
 * Списки вибору пермітів (`/admin/permits/lists`, PM-08d; макет `screens-permits.js` екран «Lists»).
 * Будова — за KIT.md §3 «Шаблон сторінки-переліку»: шапка з одним реченням → смуга розділів → пошук і походження → таблиця;
 * клік по рядку відкриває шторку (`?panel=`). Лічильники видів стоять у варіантах фільтра «Kind», окремої смуги показників немає.
 * Пошук і вид живуть в адресі.
 *
 * Дані — один запит `GET /permit-lists` (PM-08e; раніше визначення кожного типу читалось окремо). Правити значення
 * можна у шторці, якщо сервер віддав `editable`; без права під шапкою стоїть `permits.lists.readOnly`.
 * Дерево НАЕ — не плоский список: рядок веде на вкладку «NAE structure», а не до шторки значень.
 */
export function ListsPage(): JSX.Element {
  const data = useLists();
  const navigate = useNavigate();
  const setParams = useUrlParamsSetter();
  const [query] = useUrlState('q');
  const [kindFilter] = useUrlState('kind');
  const [panel] = useDetailPanel();

  const kind = kindParam(kindFilter);
  const all = useMemo(() => data.rows ?? [], [data.rows]);
  const rows = useMemo(
    () => (data.rows === undefined ? undefined : filterLists(data.rows, { needle: query ?? '', kind })),
    [data.rows, query, kind],
  );
  const stats = useMemo(() => listsStats(all), [all]);
  const columns = useMemo(() => listColumns(), []);
  const opened: ListRow | undefined = all.find((row) => row.id === panel);
  const hasLists = all.length > 0;
  // Права решає сервер на кожен список; підказка «лише читання» правдива, тільки коли правити нічого.
  const canEdit = all.some((row) => row.editable);

  return (
    <Stack gap="md" data-list-page="">
      <PageHeader
        title={t('permits.lists.title')}
        description={t('permits.lists.description')}
        secondary={[{ label: t('permits.lists.openRegistries'), href: '/admin/registries' }]}
      />
      <PermitsSubnav current="lists" />
      {!canEdit && (
        <Text size="sm" c="dimmed" data-lists-readonly="">
          {t('permits.lists.readOnly')}
        </Text>
      )}

      {hasLists && (
        <Box data-list-filters="">
          <FilterBar
            search={{ label: t('permits.lists.search.label'), placeholder: t('permits.lists.search.placeholder') }}
            filters={[
              {
                id: 'kind',
                label: t('permits.lists.filter.kind'),
                placeholder: t('permits.lists.filter.kindAll'),
                options: ListKinds.map((value) => ({ value, label: `${kindShortLabel(value)} (${String(stats[value])})` })),
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
          emptyHeadingOrder={2}
          noMatchTitle={t('permits.lists.noMatches')}
          onClearFilters={() => setParams({ q: null, kind: null })}
          clearFiltersLabel={t('filters.clear')}
          onRowClick={(row) => {
            if (row.registryCode === NaeRegistryCode) void navigate(PermitsPaths.nae);
            else setParams({ panel: row.id });
          }}
          // PUI-1: у клітинках немає посилання на шторку - рядок мусить відкриватися клавіатурою (Tab -> Enter/Space).
          rowFocusable
          rowLabel={(row) => row.name}
        />
      </Box>

      {opened !== undefined && <ListDrawer row={opened} />}
    </Stack>
  );
}

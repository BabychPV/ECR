import { useMemo, type JSX } from 'react';
import { Box, Button, Stack, Text } from '@mantine/core';
import { t } from '@/shared/i18n';
import { DataTable } from '@/shared/ui/DataTable';
import { useDetailPanel } from '@/shared/ui/DetailDrawer';
import { FilterBar } from '@/shared/ui/FilterBar';
import { PageHeader } from '@/shared/ui/PageHeader';
import { StatStrip } from '@/shared/ui/StatStrip';
import { useUrlParamsSetter, useUrlState } from '@/shared/ui/useUrlState';
import { useMayEditPermitTypes } from '../permitRights';
import { PermitsSubnav } from '../shell/PermitsSubnav';
import { GroupDrawer } from './GroupDrawer';
import type { PermitGroupDto } from './groupsApi';
import { groupColumns } from './GroupsColumns';
import { NewGroupPanel, filterGroups, groupName, groupsStatParam, groupsStats } from './groupsModel';
import { usePermitGroups } from './useGroups';

/**
 * Групи типів дозволів (`/admin/permits/groups`): іменований набір ОПУБЛІКОВАНИХ типів, що стає списком вибору цілі
 * Lookup у шаблонах і аркушах. Будова - як у «Lists»: шапка → смуга розділів → показники → пошук → таблиця; клік по
 * рядку відкриває шторку (`?panel=<код>`), «New group» - `?panel=_new`.
 *
 * Макет: KIT §6.4/§6.5/§6.8/§6.9. Екрана Groups у макеті немає - це нейтральний дефолт за аналогією зі списками
 * (потребує макетного рішення). Читання - `Registry.View`; керування - `Permit.EditType` (без права кнопок немає).
 */
export function GroupsPage(): JSX.Element {
  const groups = usePermitGroups();
  const setParams = useUrlParamsSetter();
  const [query] = useUrlState('q');
  const [statParam, setStat] = useUrlState('stat');
  const [panel] = useDetailPanel();
  const canEdit = useMayEditPermitTypes();

  const stat = groupsStatParam(statParam);
  const all = useMemo(() => groups.data ?? [], [groups.data]);
  const rows = useMemo(
    () => (groups.data === undefined ? undefined : filterGroups(groups.data, { needle: query ?? '', stat })),
    [groups.data, query, stat],
  );
  const stats = useMemo(() => groupsStats(all), [all]);
  const columns = useMemo(() => groupColumns(), []);
  const opened: PermitGroupDto | undefined = all.find((group) => group.code === panel);
  const hasGroups = all.length > 0;
  const openNew = (): void => setParams({ panel: NewGroupPanel });

  return (
    <Stack gap="md" data-list-page="" data-groups-page="">
      <PageHeader
        title={t('permits.groups.title')}
        description={t('permits.groups.description')}
        primary={canEdit ? { label: t('permits.groups.new'), onClick: openNew } : undefined}
      />
      <PermitsSubnav current="groups" />
      {!canEdit && (
        <Text size="sm" c="dimmed" data-groups-readonly="">
          {t('permits.groups.readOnly')}
        </Text>
      )}

      {hasGroups && (
        <StatStrip
          label={t('permits.groups.stats.aria')}
          active={stat === '' ? null : stat}
          onSelect={setStat}
          items={[
            { id: 'all', label: t('permits.groups.stat.all'), value: stats.all, filter: false },
            { id: 'active', label: t('permits.groups.stat.active'), value: stats.active },
            { id: 'inactive', label: t('permits.groups.stat.inactive'), value: stats.inactive, tone: 'warning' },
          ]}
        />
      )}

      {hasGroups && (
        <Box data-list-filters="">
          <FilterBar
            search={{ label: t('permits.groups.search.label'), placeholder: t('permits.groups.search.placeholder') }}
            clearLabel={t('filters.clear')}
          />
        </Box>
      )}

      <Box data-list-table="">
        <DataTable<PermitGroupDto>
          columns={columns}
          rows={rows}
          rowKey={(group) => group.code}
          isPending={groups.isPending}
          error={groups.error}
          onRetry={() => void groups.refetch()}
          filtered={(query ?? '').trim().length > 0 || stat !== ''}
          emptyTitle={t('permits.groups.empty.title')}
          emptyHint={t('permits.groups.empty.hint')}
          emptyHeadingOrder={2}
          emptyAction={canEdit ? <Button onClick={openNew}>{t('permits.groups.new')}</Button> : undefined}
          noMatchTitle={t('permits.groups.noMatches')}
          onClearFilters={() => setParams({ q: null, stat: null })}
          clearFiltersLabel={t('filters.clear')}
          onRowClick={(group) => setParams({ panel: group.code })}
          // У клітинках немає посилання на шторку - рядок мусить відкриватися клавіатурою (Tab -> Enter/Space).
          rowFocusable
          rowLabel={(group) => groupName(group)}
        />
      </Box>

      {panel === NewGroupPanel && canEdit && <GroupDrawer key={NewGroupPanel} group={null} canEdit />}
      {opened !== undefined && <GroupDrawer key={opened.code} group={opened} canEdit={canEdit} />}
    </Stack>
  );
}

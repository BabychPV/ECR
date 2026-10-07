import { Suspense, lazy, useMemo, useState, type JSX, type MouseEvent } from 'react';
import { Anchor, Badge, Box, Button, Group, Stack } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import { CreateRegistryModal } from '@/features/registries/CreateRegistryModal';
import { SourceKindSwitch } from '@/features/registries/SourceKindSwitch';
import { t } from '@/shared/i18n';
import { can, useSession } from '@/shared/session/useSession';
import { DataTable, type DataTableColumn } from '@/shared/ui/DataTable';
import { FilterBar } from '@/shared/ui/FilterBar';
import { PageHeader } from '@/shared/ui/PageHeader';
import { StatStrip, type StatItem, type StatStripItems } from '@/shared/ui/StatStrip';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { Timestamp } from '@/shared/ui/Timestamp';
import { TwoLine } from '@/shared/ui/TwoLine';
import { useUrlParamsSetter, useUrlState } from '@/shared/ui/useUrlState';
import {
  changedThisMonth,
  hasAny,
  hasField,
  isSynced,
  matchesSearch,
  matchesStat,
  parseStat,
  registryName,
  sourceKindLabel,
  type RegistryListItem,
} from './registryList';

/*
 * ⚠ Шторка — лінивим чанком: `Drawer` Mantine і «де використано» не потрібні,
 * доки рядок не відкрили, а чанк маршруту стоїть під бюджетом D-132.
 */
const RegistryDetailDrawer = lazy(async () => ({
  default: (await import('./RegistryDetailDrawer')).RegistryDetailDrawer,
}));

/**
 * Перелік довідників (`UI-35`; макет `screens-data.js` `/admin/registries`,
 * `KIT.md` §3 «сторінка-перелік»): шапка з однією головною дією, смуга
 * показників, рядок фільтрів, таблиця і шторка за `?panel=<code>`.
 *
 * ⛔ Раніше вхід у довідник був лише `Select` у шапці — огляду «що є» не
 * давав ніхто. Сторінка довідника (`?code=`) лишилася як була: це місце дій
 * над записами (вікно чинності, видалення, імпорт, вплив), шторка веде туди.
 */
export function RegistriesList(): JSX.Element {
  const session = useSession();
  const [, setCode] = useUrlState('code');
  const [query] = useUrlState('q');
  const setParams = useUrlParamsSetter();
  const [statParam, setStat] = useUrlState('stat');
  /*
   * ⚠ `?panel=` читається тут напряму, а не `useDetailPanel`/`ListPage`: обидва
   * тягнуть `DetailDrawer` (а з ним `Drawer` Mantine) у чанк маршруту, і
   * лінива шторка нічого б не заощадила (+6.7 КБ до RegistriesPage, D-132).
   */
  const [panel, setPanel] = useUrlState('panel');
  const [creating, setCreating] = useState(false);

  const registries = useQuery({
    queryKey: queryKeys.registries.list(),
    queryFn: () => apiFetch<RegistryListItem[]>('/api/v1/registries'),
  });

  const stat = parseStat(statParam);
  const needle = (query ?? '').trim().toLowerCase();

  // ⛔ `undefined` лишається `undefined`: «ще не приїхало» ≠ «довідників немає».
  const rows = useMemo(
    () =>
      registries.data?.filter(
        (registry) => matchesStat(registry, stat) && matchesSearch(registry, needle),
      ),
    [registries.data, stat, needle],
  );

  const all = registries.data ?? [];
  const opened = panel === null ? undefined : registries.data?.find((registry) => registry.code === panel);
  const canCreate = can(session.data, 'Registry.EditDefinition');

  /*
   * ⚠ Фокус повертається на назву в рядку, з якого шторку відкрили: шторка
   * лінива, тож `returnFocus` Drawer'а на першому відкритті може не знати
   * відкривача. Пошук за атрибутом — після рендера без `?panel=`.
   */
  const closeDrawer = (code: string): void => {
    window.setTimeout(() => {
      Array.from(document.querySelectorAll<HTMLElement>('[data-registry-open]'))
        .find((node) => node.dataset.registryOpen === code)
        ?.focus();
    }, 0);
  };

  const showEntries = hasAny(all, (registry) => registry.entryCount);
  const showUsedIn = hasAny(all, (registry) => registry.usedInColumns);
  const showUpdated = hasField(all, 'dataChangedAt');
  const showState = hasAny(all, (registry) => registry.hasDraft);

  const columns: readonly DataTableColumn<RegistryListItem>[] = [
    {
      key: 'registry',
      label: t('registries.list.registry'),
      minWidth: 220,
      sortValue: registryName,
      render: (registry) => (
        <TwoLine
          primary={
            <Anchor
              component="button"
              type="button"
              size="sm"
              data-registry-open={registry.code}
              onClick={(event: MouseEvent) => {
                event.stopPropagation();
                setPanel(registry.code);
              }}
            >
              {registryName(registry)}
            </Anchor>
          }
          secondary={registry.code}
          mono
        />
      ),
    },
    // ⛔ `D15-06`: колонка агрегату — лише коли сервер його віддав хоч одному рядку.
    ...(showEntries
      ? [
          {
            key: 'entryCount',
            label: t('registries.list.entries'),
            num: true,
            sortValue: (registry: RegistryListItem) => registry.entryCount ?? null,
            render: (registry: RegistryListItem) =>
              registry.entryCount === null || registry.entryCount === undefined ? '—' : String(registry.entryCount),
          } satisfies DataTableColumn<RegistryListItem>,
        ]
      : []),
    {
      key: 'fields',
      label: t('registries.fields'),
      num: true,
      sortValue: (registry) => registry.fields.length,
      render: (registry) => String(registry.fields.length),
    },
    ...(showUsedIn
      ? [
          {
            key: 'usedInColumns',
            label: t('registries.list.usedIn'),
            num: true,
            title: t('registries.list.usedInHint'),
            sortValue: (registry: RegistryListItem) => registry.usedInColumns ?? null,
            // ⚠ Нуль — дані («ніде»), `null` — «не знаю» (без права): різні написи.
            render: (registry: RegistryListItem) =>
              registry.usedInColumns === null || registry.usedInColumns === undefined
                ? '—'
                : String(registry.usedInColumns),
          } satisfies DataTableColumn<RegistryListItem>,
        ]
      : []),
    ...(showUpdated
      ? [
          {
            key: 'dataChangedAt',
            label: t('registries.list.updated'),
            sortValue: (registry: RegistryListItem) => registry.dataChangedAt ?? null,
            render: (registry: RegistryListItem) => (
              <Timestamp value={registry.dataChangedAt} dateOnly />
            ),
          } satisfies DataTableColumn<RegistryListItem>,
        ]
      : []),
    ...(showState
      ? [
          {
            key: 'hasDraft',
            label: t('registries.list.state'),
            sortValue: (registry: RegistryListItem) =>
              registry.hasDraft === null || registry.hasDraft === undefined ? null : Number(registry.hasDraft),
            render: (registry: RegistryListItem) =>
              registry.hasDraft === null || registry.hasDraft === undefined ? (
                '—'
              ) : (
                <StatusBadge kind="version" state={registry.hasDraft ? 'Draft' : 'Published'} quiet />
              ),
          } satisfies DataTableColumn<RegistryListItem>,
        ]
      : []),
    {
      key: 'traits',
      label: t('registries.list.traits'),
      sortable: false,
      render: (registry) =>
        registry.isTemporal || registry.isHierarchical || isSynced(registry) ? (
          <Group gap="xs" wrap="wrap">
            {/* ⚠ `tt="none"`: тихі позначки макета (`StatusBadge` quiet), а не капс Mantine. */}
            {registry.isTemporal && (
              <Badge variant="light" tt="none">
                {t('registries.temporal')}
              </Badge>
            )}
            {registry.isHierarchical && (
              <Badge variant="light" tt="none">
                {t('registries.hierarchical')}
              </Badge>
            )}
            {isSynced(registry) && (
              <Badge variant="outline" tt="none">
                {sourceKindLabel(registry.sourceKind)}
              </Badge>
            )}
          </Group>
        ) : null,
    },
  ];

  const hasRegistries = all.length > 0;

  return (
    /* ⚠ Будова `ListPage` (шапка → смуга → фільтри → таблиця → шторка), зібрана
       без нього самого — див. `?panel=` вище. `data-list-page` той самий. */
    <Stack gap="md" data-list-page="">
      <PageHeader
        title={t('registries.title')}
        count={registries.data === undefined ? undefined : all.length}
        description={t('registries.list.subtitle')}
        primary={
          canCreate ? { label: t('registries.newRegistry'), onClick: () => setCreating(true) } : undefined
        }
        // ⚠ Перемикання master набором (`ФВ-13.10`) — власний вузол із діалогом, не `HeaderAction`.
        actions={can(session.data, 'Integration.Manage') ? <SourceKindSwitch registries={all} /> : undefined}
      />

      {/* `D15-06`: без довідників смуги й фільтрів немає — лише порожній стан таблиці. */}
      {hasRegistries && (
        <StatStrip
          label={t('registries.list.stats')}
          active={stat}
          onSelect={setStat}
          items={statItems(all, showEntries, showUsedIn, showUpdated)}
        />
      )}

      {hasRegistries && (
        <Box data-list-filters="">
          <FilterBar
            search={{
              label: t('registries.search'),
              param: 'q',
              placeholder: t('registries.list.searchPlaceholder'),
            }}
            clearLabel={t('filters.clear')}
          />
        </Box>
      )}

      <Box data-list-table="">
        <DataTable<RegistryListItem>
          columns={columns}
          rows={rows}
          rowKey={(registry) => registry.code}
          isPending={registries.isPending}
          error={registries.error}
          onRetry={() => void registries.refetch()}
          filtered={needle.length > 0 || stat !== null}
          emptyTitle={t('registries.empty')}
          emptyHint={t('registries.emptyHint')}
          emptyAction={
            canCreate ? (
              <Button onClick={() => setCreating(true)}>{t('registries.newRegistry')}</Button>
            ) : undefined
          }
          noMatchTitle={t('registries.list.noMatches')}
          // ⛔ Один перехід на обидва параметри: два сеттери поспіль губили б зміни (`useUrlState.ts`).
          onClearFilters={() => setParams({ q: null, stat: null })}
          clearFiltersLabel={t('filters.clear')}
          onRowClick={(registry) => setPanel(registry.code)}
          selectedKey={opened?.code}
        />
      </Box>

      {opened !== undefined && (
        <Suspense fallback={null}>
          <RegistryDetailDrawer
            key={opened.code}
            registry={opened}
            canSeeUsage={canCreate}
            onClose={() => closeDrawer(opened.code)}
          />
        </Suspense>
      )}

      <CreateRegistryModal opened={creating} onClose={() => setCreating(false)} onCreated={setCode} />
    </Stack>
  );
}

/**
 * Показники смуги — у порядку макета (registries · entries · changed this month ·
 * referenced by), кожен лише коли сервер віддав його дані; вільні місця до
 * межі `L4` займають показники з наявних ознак (time-bound, synced from PI AF).
 */
function statItems(
  all: readonly RegistryListItem[],
  showEntries: boolean,
  showUsedIn: boolean,
  showUpdated: boolean,
): StatStripItems {
  const sum = (pick: (row: RegistryListItem) => number | null | undefined): number =>
    all.reduce((total, row) => total + (pick(row) ?? 0), 0);

  const first: StatItem = { id: 'all', label: t('registries.list.statAll'), value: all.length, filter: false };
  const rest: StatItem[] = [];

  if (showEntries) {
    rest.push({ id: 'entries', label: t('registries.list.statEntries'), value: sum((row) => row.entryCount), filter: false });
  }
  if (showUpdated) {
    rest.push({ id: 'changed', label: t('registries.list.statChanged'), value: all.filter((row) => changedThisMonth(row)).length });
  }
  if (showUsedIn) {
    rest.push({ id: 'used', label: t('registries.list.statUsedIn'), value: sum((row) => row.usedInColumns), filter: false });
  }

  const fallback: StatItem[] = [
    { id: 'temporal', label: t('registries.temporal'), value: all.filter((row) => row.isTemporal).length },
    { id: 'external', label: t('registries.list.statExternal'), value: all.filter(isSynced).length },
  ];

  const [b, c, d] = [...rest, ...fallback];

  // ⚠ Кортеж ≤ 4 (`L4` у типі): `rest` + `fallback` дає щонайменше два показники.
  return d === undefined ? (c === undefined ? [first, b!] : [first, b!, c]) : [first, b!, c!, d];
}

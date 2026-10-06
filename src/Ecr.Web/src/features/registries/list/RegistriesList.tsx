import { Suspense, lazy, useMemo, useState, type JSX, type MouseEvent } from 'react';
import { Anchor, Badge, Box, Button, Group, Stack } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { RegistryDefDto } from '@/api/types';
import { CreateRegistryModal } from '@/features/registries/CreateRegistryModal';
import { SourceKindSwitch } from '@/features/registries/SourceKindSwitch';
import { t } from '@/shared/i18n';
import { can, useSession } from '@/shared/session/useSession';
import { DataTable, type DataTableColumn } from '@/shared/ui/DataTable';
import { FilterBar } from '@/shared/ui/FilterBar';
import { PageHeader } from '@/shared/ui/PageHeader';
import { StatStrip } from '@/shared/ui/StatStrip';
import { TwoLine } from '@/shared/ui/TwoLine';
import { useUrlParamsSetter, useUrlState } from '@/shared/ui/useUrlState';
import { isSynced, sourceKindLabel, matchesSearch, matchesStat, parseStat, registryName } from './registryList';

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
    queryFn: () => apiFetch<RegistryDefDto[]>('/api/v1/registries'),
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

  const columns: readonly DataTableColumn<RegistryDefDto>[] = [
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
    {
      key: 'fields',
      label: t('registries.fields'),
      num: true,
      sortValue: (registry) => registry.fields.length,
      render: (registry) => String(registry.fields.length),
    },
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
        meta={t('registries.list.subtitle')}
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
          items={[
            { id: 'all', label: t('registries.list.statAll'), value: all.length, filter: false },
            {
              id: 'temporal',
              label: t('registries.temporal'),
              value: all.filter((registry) => registry.isTemporal).length,
            },
            {
              id: 'external',
              label: t('registries.list.statExternal'),
              value: all.filter(isSynced).length,
            },
          ]}
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
        <DataTable<RegistryDefDto>
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

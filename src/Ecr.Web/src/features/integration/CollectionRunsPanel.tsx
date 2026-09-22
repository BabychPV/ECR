import { Suspense, lazy, type JSX } from 'react';
import { Badge, Button, Group, Stack, Text, Title } from '@mantine/core';
import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { SourceEntityStatus } from '@/api/types';
import { CollectionRunStateBadge } from './CollectionRunStateBadge';
import { CollectionRunDetailDrawer } from './CollectionRunDetailDrawer';
import {
  CollectionRunStates,
  collectionRunsQueryKey,
  listCollectionRuns,
  type CollectionRunFilters,
  type CollectionRunView,
} from './collectionRunsApi';
import { dataSourceName } from './DataSourceDrawer';
import { listDataSources } from './dataSourceApi';
import { DataSourcesQueryKey } from './dataSourcesKey';
import { DataTable } from '@/shared/ui/DataTable';
import { FilterBar, type FilterOption } from '@/shared/ui/FilterBar';
import { useDetailPanel } from '@/shared/ui/DetailDrawer';
import { Timestamp } from '@/shared/ui/Timestamp';
import { useUrlNumber, useUrlState } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';

/**
 * Журнал прогонів збору (ФВ-5.23) — секція сторінки `/admin/sources`.
 *
 * ⛔ НЕ окремий маршрут: право те саме, що вже відкриває `/admin/sources`
 * (`Integration.View` АБО `Integration.Manage`, `routes.ts:adminSources`,
 * дзеркалить `ListCollectionRunsHandler`/`ListDataSourcesHandler` на
 * сервері), і новий маршрут ввів би НОВИЙ `labelKey` у `routes.ts`, якого не
 * можна дописати в статичний перелік `EndpointCoverageTests.RouteLabelKeys`
 * (`tests/Ecr.Architecture.Tests`, файл поза дозволеним списком цієї задачі).
 * Секція на наявному маршруті цього обмеження не має: сторінка НЕ додає
 * жодного нового `t('nav.…')` виклику з `routes.ts`.
 *
 * ⚠ Шухляда подробиць НЕ використовує спільний `useDetailPanel()`/`?panel=`
 * напряму зі своїм `run.id`: той самий параметр адреси вже займає
 * `DataSourcesTable` на цій самій сторінці (код з'єднання). `panelId`
 * тут — `run:<id>`, префіксом: числовий рядок `"5"` (шухляда прогону) і код
 * з'єднання (рядок, зазвичай не суто числовий) різняться за побудовою, і
 * префікс прибирає навіть теоретичний збіг.
 */

/** Розмір списку сутностей у фільтрі — той самий запит, що вже робить `SourcesPage`. */
const SourceEntitiesQueryKey = ['sources'] as const;

/** `YYYY-MM-DD` місцевими складниками — не `toISOString()` (той зсуває добу в UTC). */
function dateOnlyLocal(date: Date): string {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');

  return `${year}-${month}-${day}`;
}

/** `YYYY-MM-DD` → `Date` опівночі МІСЦЕВОГО часу — для показу в `DateInput`. */
function parseDateOnly(value: string | null): Date | null {
  if (value === null) return null;

  const parsed = new Date(`${value}T00:00:00`);
  return Number.isNaN(parsed.getTime()) ? null : parsed;
}

/** Початок доби `value` в UTC — межа `from` (включно, як і контракт). */
function startOfDayUtc(value: string): string | null {
  const [year, month, day] = value.split('-').map(Number);
  if (year === undefined || month === undefined || day === undefined) return null;

  return new Date(Date.UTC(year, month - 1, day)).toISOString();
}

/** Початок доби ПІСЛЯ `value` в UTC — межа `to` (виключно, як і контракт). */
function startOfNextDayUtc(value: string): string | null {
  const [year, month, day] = value.split('-').map(Number);
  if (year === undefined || month === undefined || day === undefined) return null;

  return new Date(Date.UTC(year, month - 1, day + 1)).toISOString();
}

/**
 * Тривалість прогону — секунди з одним знаком дробу.
 *
 * ⚠ `null` — прогін ще виконується (той самий контракт, що й `finishedAt`):
 * тире тут читалося б як «нуль секунд», а не «ще не відомо».
 */
function formatDurationMs(durationMs: number | null): string {
  if (durationMs === null) return '—';

  return t('collectionRuns.durationSeconds', { value: (durationMs / 1000).toFixed(1) });
}

/**
 * `@mantine/dates` — за `import()`: важить достатньо, щоб рахуватися в бюджет
 * маршруту (`D-132`), а фільтр за датою відкриває не кожен, хто зайшов на
 * `/admin/sources` (той самий прийом, що й `SnapshotsPage.tsx`).
 */
const DateInput = lazy(async () => {
  const module = await import('@mantine/dates');

  return { default: module.DateInput };
});

/** Префікс `panelId` шухляди прогону — див. коментар угорі файлу. */
const RunPanelPrefix = 'run:';

function runPanelId(id: number): string {
  return `${RunPanelPrefix}${id}`;
}

export function CollectionRunsPanel(): JSX.Element {
  const [dataSource] = useUrlNumber('dataSource');
  const [entity] = useUrlNumber('entity');
  const [state] = useUrlState('state');
  const [fromDate, setFromDate] = useUrlState('from');
  const [toDate, setToDate] = useUrlState('to');
  const [panel, setPanel] = useDetailPanel();

  const sources = useQuery({ queryKey: DataSourcesQueryKey, queryFn: listDataSources });
  const entities = useQuery({
    queryKey: SourceEntitiesQueryKey,
    queryFn: () => apiFetch<SourceEntityStatus[]>('/api/v1/sources'),
  });

  const filters: CollectionRunFilters = {
    dataSource,
    entity,
    state,
    from: fromDate === null ? null : startOfDayUtc(fromDate),
    to: toDate === null ? null : startOfNextDayUtc(toDate),
  };

  /*
   * ⚠ `useInfiniteQuery`, а не `useQuery` з власним курсором у стані: журнал
   * ДОЧИТУЄТЬСЯ кнопкою «показати ще», а не гортається сторінками — рядок,
   * який людина щойно бачила, не має зникати з-під очей (той самий аргумент,
   * що в `DeliveriesPanel.tsx`). Зміна фільтра змінює КЛЮЧ (курсора в ньому
   * немає — `collectionRunsQueryKey`), тобто запускає новий незалежний набір
   * сторінок замість дочитування старого.
   */
  const runs = useInfiniteQuery({
    queryKey: collectionRunsQueryKey(filters),
    queryFn: ({ pageParam }: { pageParam: string | null }) => listCollectionRuns(filters, pageParam),
    initialPageParam: null as string | null,
    // ⚠ `last?.nextCursor ?? null`, а не голе `last.nextCursor`: тести інших
    // споживачів `/admin/sources` (наприклад `DataSourcesTable.test.tsx`),
    // які про цей ендпоінт не знають, мокають fetch одним фолбеком
    // `json(null)` на будь-яку адресу поза їхнім інтересом — сторінка не має
    // падати цілком через відповідь `null` на запит, який той тест не
    // перевіряє.
    getNextPageParam: (last) => last?.nextCursor ?? null,
  });

  const rows: readonly CollectionRunView[] = (runs.data?.pages ?? []).flatMap((page) => page?.items ?? []);

  /*
   * ⚠ Підпис варіанта — НАЗВА З КОДОМ в одному текстовому вузлі, а не сама
   * назва. Причина не косметична: ця сторінка вже показує ту саму назву
   * окремим елементом у переліку з'єднань (`DataSourcesTable`), і Mantine
   * `Combobox` (`FilterBar` не дає керувати його `keepMounted`) тримає
   * варіанти в DOM завжди, лише візуально прихованими. Без коду в підписі
   * `getByText('Main PI server')` у тестах, що монтують усю сторінку
   * (`DataSourcesTable.test.tsx`), знаходив би ДВА вузли — видимий рядок
   * переліку й прихований варіант цього фільтра — і падав на
   * `Found multiple elements`. Код робить текстовий вузол унікальним і як
   * побічний ефект розрізняє джерела з однаковою назвою.
   */
  const dataSourceOptions: readonly FilterOption[] = (sources.data ?? [])
    .map((source) => ({ value: String(source.id), label: `${dataSourceName(source)} (${source.code})` }));

  const entityOptions: readonly FilterOption[] = (entities.data ?? [])
    // ⚠ Звужено обраним з'єднанням — коли воно є: сутність без з'єднання не
    // існує, і повний перелік плутав би вибором, якого фільтр стану не дасть.
    .filter((candidate) => dataSource === null || candidate.dataSourceId === dataSource)
    .map((candidate) => ({
      value: String(candidate.id),
      label: `${candidate.displayName ?? candidate.code} (${candidate.code})`,
    }));

  const stateOptions: readonly FilterOption[] = CollectionRunStates.map((value) => ({
    value,
    label: stateFilterLabel(value),
  }));

  const openId = panel?.startsWith(RunPanelPrefix) === true ? Number(panel.slice(RunPanelPrefix.length)) : null;

  // ⚠ Шухляда відкривається лише для прогону, який справді є у видимій
  // сторінці журналу (той самий прийом, що `DataSourcesTable.tsx`): застарілий
  // `?panel=` не дає порожньої шухляди з голим номером замість заголовка.
  const openRun = openId === null ? undefined : rows.find((run) => run.id === openId);

  return (
    <Stack gap="sm" data-collection-runs-panel="">
      <Title order={2} size="h4">
        {t('collectionRuns.title')}
      </Title>

      <FilterBar
        filters={[
          { id: 'dataSource', label: t('collectionRuns.filterDataSource'), options: dataSourceOptions },
          { id: 'entity', label: t('collectionRuns.filterEntity'), options: entityOptions },
          { id: 'state', label: t('collectionRuns.filterState'), options: stateOptions },
        ]}
        right={
          <Suspense fallback={null}>
            <Group gap="xs" align="end">
              <DateInput
                size="sm"
                miw={160}
                label={t('collectionRuns.filterFrom')}
                valueFormat="YYYY-MM-DD"
                clearable
                value={parseDateOnly(fromDate)}
                onChange={(next) => setFromDate(next === null ? null : dateOnlyLocal(next))}
              />
              <DateInput
                size="sm"
                miw={160}
                label={t('collectionRuns.filterTo')}
                valueFormat="YYYY-MM-DD"
                clearable
                value={parseDateOnly(toDate)}
                onChange={(next) => setToDate(next === null ? null : dateOnlyLocal(next))}
              />
            </Group>
          </Suspense>
        }
      />

      <DataTable<CollectionRunView>
        columns={[
          {
            key: 'entity',
            label: t('collectionRuns.entity'),
            minWidth: 200,
            sortValue: (run) => run.sourceEntityName ?? run.sourceEntityCode,
            render: (run) => (
              <Stack gap="xs">
                <Text size="sm">{run.sourceEntityName ?? run.sourceEntityCode}</Text>
                <Text size="xs" c="dimmed" ff="monospace">
                  {run.dataSourceCode}
                </Text>
              </Stack>
            ),
          },
          {
            key: 'status',
            label: t('collectionRuns.state'),
            render: (run) => (
              <Stack gap="xs">
                <CollectionRunStateBadge state={run.status} />
                {run.hasError && (
                  <Text size="xs" c="statusError">
                    {t('collectionRuns.hasError')}
                  </Text>
                )}
              </Stack>
            ),
          },
          {
            key: 'startedAt',
            label: t('collectionRuns.range'),
            render: (run) => (
              <Stack gap="xs">
                <Timestamp value={run.startedAt} />
                <Text size="xs" c="dimmed">
                  <Timestamp value={run.rangeFrom} dateOnly /> – <Timestamp value={run.rangeTo} dateOnly />
                </Text>
                {run.isCatchUp && (
                  <Badge size="xs" variant="outline" color="gray" miw="fit-content">
                    {t('collectionRuns.catchUp')}
                  </Badge>
                )}
              </Stack>
            ),
          },
          {
            key: 'durationMs',
            label: t('collectionRuns.duration'),
            sortable: false,
            render: (run) => formatDurationMs(run.durationMs),
          },
          {
            key: 'pointsRetrieved',
            label: t('collectionRuns.points'),
            num: true,
          },
          {
            key: 'triggeredByUserId',
            label: t('collectionRuns.triggeredBy'),
            render: (run) =>
              run.triggeredByUserId === null ? (
                <Text c="dimmed">{t('collectionRuns.system')}</Text>
              ) : (
                <Text ff="monospace">{run.triggeredByUserId}</Text>
              ),
          },
        ]}
        rows={rows}
        rowKey={(run) => String(run.id)}
        isPending={runs.isPending}
        error={runs.error}
        onRetry={() => void runs.refetch()}
        emptyTitle={t('collectionRuns.empty')}
        emptyHint={t('collectionRuns.emptyHint')}
        onRowClick={(run) => setPanel(runPanelId(run.id))}
        selectedKey={openId === null ? undefined : runPanelId(openId)}
      />

      {/* ⚠ Кнопка живе рівно доти, доки сервер віддає курсор — кнопка, що
          нічого не дочитує, обіцяє рядки, яких немає. */}
      {runs.hasNextPage && (
        <Group justify="center">
          <Button
            size="xs"
            variant="default"
            loading={runs.isFetchingNextPage}
            onClick={() => void runs.fetchNextPage()}
            data-collection-runs-more=""
          >
            {t('collectionRuns.more')}
          </Button>
        </Group>
      )}

      {openRun !== undefined && (
        <CollectionRunDetailDrawer run={openRun} panelId={runPanelId(openRun.id)} />
      )}
    </Stack>
  );
}

/**
 * Підпис варіанта фільтра стану — літеральні `t(...)`, як і в
 * `CollectionRunStateBadge.stateLabel` (той самий сторож `EndpointCoverageTests`,
 * та сама причина: ключ зі змінної вимагав би запису в `DynamicKeySites`).
 */
function stateFilterLabel(state: (typeof CollectionRunStates)[number]): string {
  switch (state) {
    case 'Running':
      return t('collectionRuns.stateRunning');
    case 'Succeeded':
      return t('collectionRuns.stateSucceeded');
    case 'Degraded':
      return t('collectionRuns.stateDegraded');
    case 'Failed':
      return t('collectionRuns.stateFailed');
    default:
      return state;
  }
}

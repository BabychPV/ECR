import { lazy, Suspense, useState, type JSX } from 'react';
import { Anchor, Badge, Button, Text } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useSearchParams } from 'react-router-dom';
import { queryKeys } from '@/api/queryKeys';
import {
  deleteStream,
  getStream,
  listComponents,
  listStreams,
  monthOf,
  startImport,
  type GasChangeResult,
  type GasImportRun,
  type GasStreamDetail,
  type GasStreamListItem,
} from '@/features/gasComposition/api';
import { StreamFormModal } from '@/features/gasComposition/StreamFormModal';
import { formatDateTime } from '@/shared/format';
import { t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { DataTable, type DataTableColumn } from '@/shared/ui/DataTable';
import { useDetailPanel } from '@/shared/ui/DetailDrawer';
import { FilterBar } from '@/shared/ui/FilterBar';
import { ListPage } from '@/shared/ui/ListPage';
import { showApiError, showDone, showWarning } from '@/shared/ui/notify';
import type { StatItem } from '@/shared/ui/StatStrip';
import { TwoLine } from '@/shared/ui/TwoLine';
import { useUrlParamsSetter, useUrlState } from '@/shared/ui/useUrlState';

/** Шторка потоку — лінивий чанк: закрита за замовчуванням, маршрут переліку за неї не платить. */
const StreamDetailBody = lazy(() => import('@/features/gasComposition/StreamDetailBody'));

/** Значення `?panel=` шторки потоку. */
export function streamPanelId(id: number): string {
  return `stream-${String(id)}`;
}

/** Показник смуги, що фільтрує: потоки з версією, де Σ поза 100 ± 0.5. */
const DeviatingStat = 'deviating';

/** Стан прогону імпорту текстом мовою інтерфейсу. */
function importRunText(run: GasImportRun): string {
  const status = t(`gasComposition.import.status.${run.status.toLowerCase()}`);
  const time = formatDateTime(run.startedAt);
  const head = t('gasComposition.import.lastRun', { time, status });
  if (run.reason !== null && run.reason !== undefined) return `${head} ${t(`gasComposition.import.reason.${run.reason}`)}`;
  return `${head} ${t('gasComposition.import.counts', { rows: run.sourceRows, added: run.added })}`;
}

/**
 * Склад газу (D-NEW, людина 24.11): потоки з кейсами й версіями складу, власник — ECR. Перелік — фільтри й Σ-перевірка;
 * шторка потоку — шкала версій кожного кейсу й редактор. Поки в ECR немає жодного потоку, угорі — разовий імпорт із
 * FLERT (FLERT лише читається).
 */
export function GasCompositionPage(): JSX.Element {
  const queryClient = useQueryClient();
  const [panel, setPanel] = useDetailPanel();
  const [params] = useSearchParams();
  const [stat, setStat] = useUrlState('stat');
  const setParams = useUrlParamsSetter();
  // Коли натиснули «Імпортувати з FLERT» (мс); `null` — не чекаємо імпорту.
  const [importingSince, setImportingSince] = useState<number | null>(null);

  const list = useQuery({
    queryKey: queryKeys.gasComposition.streams(),
    queryFn: listStreams,
    // Після «Імпортувати з FLERT» — поки задача не дописала журнал, перелік перечитується (імпорт іде секунди).
    refetchInterval: (query) => {
      if (importingSince === null) return false;
      const run = query.state.data?.import.lastRun;
      const finished = run?.finishedAt !== null && run?.finishedAt !== undefined && Date.parse(run.startedAt) >= importingSince - 60_000;
      return finished ? false : 3000;
    },
  });
  const components = useQuery({ queryKey: queryKeys.gasComposition.components(), queryFn: listComponents, staleTime: 60 * 60 * 1000 });

  const streams = list.data?.streams;
  const openId = streams?.find((s) => streamPanelId(s.id) === panel)?.id;
  const detail = useQuery({
    queryKey: queryKeys.gasComposition.stream(openId),
    queryFn: () => getStream(openId ?? 0),
    enabled: openId !== undefined,
  });

  const [form, setForm] = useState<GasStreamDetail | 'new' | null>(null);
  const [deleting, setDeleting] = useState<GasStreamDetail | null>(null);

  const applied = async (result: GasChangeResult): Promise<void> => {
    if (result.stream !== null && result.stream !== undefined) {
      queryClient.setQueryData(queryKeys.gasComposition.stream(result.stream.id), result.stream);
    }
    await queryClient.invalidateQueries({ queryKey: queryKeys.gasComposition.streams() });
    showDone(
      result.queuedYears.length > 0
        ? `${t('gasComposition.saved')} ${t('gasComposition.recalcQueued', { years: result.queuedYears.join(', ') })}`
        : t('gasComposition.saved'),
    );
    if (result.failedYears.length > 0) {
      showWarning(t('gasComposition.recalcFailed', { years: result.failedYears.join(', ') }));
    }
  };

  const remove = useMutation({
    mutationFn: (id: number) => deleteStream(id),
    onSuccess: async (result) => {
      setDeleting(null);
      setPanel(null);
      await queryClient.invalidateQueries({ queryKey: queryKeys.gasComposition.streams() });
      showDone(t('gasComposition.deleted'));
      if (result.failedYears.length > 0) showWarning(t('gasComposition.recalcFailed', { years: result.failedYears.join(', ') }));
    },
    onError: showApiError,
  });

  const runImport = useMutation({
    mutationFn: startImport,
    onSuccess: () => {
      setImportingSince(Date.now());
      showDone(t('gasComposition.import.queued'));
    },
    onError: showApiError,
  });

  const query = (params.get('q') ?? '').trim().toLowerCase();
  const location = params.get('location');
  const sour = params.get('sour');
  const shown =
    streams === undefined
      ? undefined
      : streams.filter(
          (s) =>
            (query.length === 0 ||
              s.name.toLowerCase().includes(query) ||
              (s.number ?? '').toLowerCase().includes(query) ||
              String(s.streamId) === query) &&
            (location === null || (location === 'onshore') === s.isOnshore) &&
            (sour === null || (sour === 'yes') === s.isSour) &&
            (stat !== DeviatingStat || s.deviatingVersions > 0),
        );
  const filtered = query.length > 0 || location !== null || sour !== null || stat !== null;

  const stats: readonly [StatItem, StatItem, StatItem] = [
    { id: 'streams', label: t('gasComposition.statStreams'), value: streams?.length ?? 0, filter: false },
    { id: 'versions', label: t('gasComposition.statVersions'), value: (streams ?? []).reduce((n, s) => n + s.versionCount, 0), filter: false },
    { id: DeviatingStat, label: t('gasComposition.statDeviating'), value: (streams ?? []).filter((s) => s.deviatingVersions > 0).length },
  ];

  const columns: readonly DataTableColumn<GasStreamListItem>[] = [
    { key: 'streamId', label: t('gasComposition.col.id'), num: true, sortValue: (s) => s.streamId, render: (s) => String(s.streamId) },
    { key: 'number', label: t('gasComposition.col.number'), sortValue: (s) => s.number ?? '', render: (s) => s.number ?? '—' },
    {
      key: 'name',
      label: t('gasComposition.col.name'),
      sortValue: (s) => s.name,
      // ⚠ Назва — кнопка шторки: клац по рядку не має клавіатурного шляху, кнопка — має.
      render: (s) => (
        <TwoLine
          primary={
            <Anchor
              component="button"
              type="button"
              size="sm"
              data-stream-open={s.streamId}
              onClick={(event) => {
                event.stopPropagation();
                setPanel(streamPanelId(s.id));
              }}
            >
              {s.name}
            </Anchor>
          }
          secondary={
            [s.isOnshore ? t('gasComposition.onshore') : t('gasComposition.offshore'), s.isSour ? t('gasComposition.sour') : null, s.hidden ? t('gasComposition.hiddenBadge') : null]
              .filter((x) => x !== null)
              .join(' · ')
          }
        />
      ),
    },
    { key: 'caseCount', label: t('gasComposition.col.cases'), num: true, render: (s) => String(s.caseCount) },
    { key: 'versionCount', label: t('gasComposition.col.versions'), num: true, render: (s) => String(s.versionCount) },
    { key: 'latestFrom', label: t('gasComposition.col.latest'), sortValue: (s) => s.latestFrom ?? '', render: (s) => (s.latestFrom === null || s.latestFrom === undefined ? '—' : monthOf(s.latestFrom)) },
    {
      key: 'deviatingVersions',
      label: t('gasComposition.col.sum'),
      sortValue: (s) => s.deviatingVersions,
      render: (s) =>
        s.deviatingVersions === 0 ? (
          <Text span size="sm" c="dimmed">
            {t('gasComposition.sumOk')}
          </Text>
        ) : (
          <Badge size="sm" variant="light" color="statusWarning">
            {t('gasComposition.sumDeviating', { count: s.deviatingVersions })}
          </Badge>
        ),
    },
  ];

  const canEdit = list.data?.canEdit === true;
  const open = detail.data;
  const lastRun = list.data?.import.lastRun ?? null;

  return (
    <ListPage
      header={{
        title: t('gasComposition.title'),
        description: t('gasComposition.description'),
        primary: canEdit ? { label: t('gasComposition.newStream'), onClick: () => setForm('new') } : undefined,
      }}
      stats={streams === undefined ? undefined : { label: t('gasComposition.statsLabel'), items: stats, active: stat, onSelect: setStat }}
      filters={
        <>
          {list.data?.import.available === true && (
            <Banner
              tone="info"
              testId="gas-import"
              title={t('gasComposition.import.title')}
              text={
                <>
                  <Text size="sm">{t('gasComposition.import.text')}</Text>
                  {lastRun !== null && (
                    <Text size="sm" c="dimmed" mt="xs" data-testid="gas-import-last-run">
                      {importRunText(lastRun)}
                    </Text>
                  )}
                  {!list.data.canImport && (
                    <Text size="sm" c="dimmed" mt="xs">
                      {t('gasComposition.import.noRight')}
                    </Text>
                  )}
                </>
              }
              actions={
                list.data.canImport
                  ? [{ label: t('gasComposition.import.button'), onClick: () => { if (!runImport.isPending) runImport.mutate(); } }]
                  : undefined
              }
            />
          )}
          <FilterBar
            search={{ label: t('gasComposition.search'), placeholder: t('gasComposition.searchPlaceholder') }}
            filters={[
              {
                id: 'location',
                label: t('gasComposition.location'),
                options: [
                  { value: 'onshore', label: t('gasComposition.onshore') },
                  { value: 'offshore', label: t('gasComposition.offshore') },
                ],
              },
              {
                id: 'sour',
                label: t('gasComposition.sour'),
                options: [
                  { value: 'yes', label: t('gasComposition.yes') },
                  { value: 'no', label: t('gasComposition.no') },
                ],
              },
            ]}
            clearLabel={t('filters.clear')}
          />
        </>
      }
      table={
        <DataTable<GasStreamListItem>
          columns={columns}
          rows={shown}
          rowKey={(s) => String(s.id)}
          defaultSort={{ key: 'streamId', direction: 'asc' }}
          urlSortParam="sort"
          isPending={list.isPending}
          error={list.error}
          emptyTitle={t('gasComposition.empty')}
          emptyHint={t('gasComposition.emptyHint')}
          filtered={filtered}
          noMatchTitle={t('gasComposition.noMatch')}
          onClearFilters={() => setParams({ q: null, location: null, sour: null, stat: null })}
          clearFiltersLabel={t('filters.clear')}
          onRetry={() => void list.refetch()}
          onRowClick={(s) => setPanel(streamPanelId(s.id))}
          selectedKey={openId === undefined ? undefined : String(openId)}
        />
      }
      detail={
        open === undefined || openId === undefined
          ? undefined
          : {
              panelId: streamPanelId(open.id),
              title: open.name,
              subtitle: `${t('gasComposition.detail.streamId')} ${String(open.streamId)}`,
              closeLabel: t('common.close'),
              size: 'xl',
              footer: open.canEdit ? (
                <>
                  <Button
                    variant="outline"
                    color="statusError"
                    mr="auto"
                    onClick={() => {
                      remove.reset();
                      setDeleting(open);
                    }}
                  >
                    {t('gasComposition.deleteStream')}
                  </Button>
                  <Button onClick={() => setForm(open)}>{t('gasComposition.editStream')}</Button>
                </>
              ) : undefined,
              children: (
                <Suspense fallback={<Text size="sm" c="dimmed">{t('common.loading')}</Text>}>
                  <StreamDetailBody stream={open} components={components.data ?? []} onChanged={(result) => void applied(result)} />
                </Suspense>
              ),
            }
      }
    >
      <StreamFormModal
        target={form}
        onClose={() => setForm(null)}
        onSaved={(result) => {
          setForm(null);
          void applied(result);
          if (result.stream !== null && result.stream !== undefined) setPanel(streamPanelId(result.stream.id));
        }}
      />

      <ConfirmModal
        opened={deleting !== null}
        title={t('gasComposition.deleteStreamTitle', { name: deleting?.name ?? '' })}
        verb={t('common.delete')}
        isPending={remove.isPending}
        onConfirm={() => {
          if (deleting !== null) remove.mutate(deleting.id);
        }}
        onClose={() => setDeleting(null)}
      >
        <Text size="sm">{t('gasComposition.deleteStreamText')}</Text>
      </ConfirmModal>
    </ListPage>
  );
}

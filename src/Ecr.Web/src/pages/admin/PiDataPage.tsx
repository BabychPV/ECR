import { Suspense, lazy, useMemo, useState, type JSX } from 'react';
import { Anchor, Button, Card, Group, SegmentedControl, Select, Skeleton, Stack, Text, Title } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import type { SourceEntityStatus } from '@/api/types';
import {
  fetchRawCoverage,
  fetchRawExport,
  fetchRawPoints,
  fetchRawSeries,
  fetchRawTags,
  type RawCoverageView,
  type RawDataTagsView,
  type RawSeriesRef,
  type RawSeriesView,
} from '@/features/piData/api';
import { CoverageSummary } from '@/features/piData/CoverageSummary';
import { EntityCoverageEvents } from '@/features/piData/EntityCoverageEvents';
import {
  DefaultPreset,
  RangePresets,
  isRangePreset,
  presetWindow,
  type RangePreset,
} from '@/features/piData/rangePresets';
import { RawPointsTable } from '@/features/piData/RawPointsTable';
import { saveBlob } from '@/features/piData/saveBlob';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { FilterRow } from '@/shared/ui/FilterBar';
import { PageHeader } from '@/shared/ui/PageHeader';
import { useUrlNumber, useUrlState } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';

/**
 * Графік — окремий лінивий чанк (`D-132`): таблиця й покриття малюються одразу, SVG-графік доїжджає слідом.
 */
const SeriesChart = lazy(async () => ({ default: (await import('@/features/piData/SeriesChart')).SeriesChart }));

/** D-465: події PI (Event Frames) — свій лінивий чанк: сторінка часових рядів його не вантажить. */
const PiEventsPanel = lazy(() => import('@/features/piData/PiEventsPanel'));

/** Що показує сторінка: часові ряди (`ext.RawDataPoint`) чи події PI (`ext.SourceEventLink`). */
type PiDataView = 'series' | 'events';

/**
 * «Дані з PI» (`D-463`): що саме збір приніс із PI для сутності й тега — графік за вікно (до року), сирі точки
 * сторінками, покриття вікна збором і події «чому не лягло в документи», CSV.
 *
 * ⚠ Лише вже зібране (`ext.RawDataPoint`): у PI наживо екран не ходить, тож недоступне джерело не ламає перегляд.
 * ⚠ Сутність, тег і вікно живуть в адресі (`ФВ-14.29`): посиланням можна поділитися з тим, хто розбирає дані.
 */
export function PiDataPage(): JSX.Element {
  const [entityId, setEntityId] = useUrlNumber('entity');
  const [tagParam, setTag] = useUrlState('tag');
  const [rangeParam, setRange] = useUrlState('range');
  const [viewParam, setView] = useUrlState('view');
  const preset: RangePreset = isRangePreset(rangeParam) ? rangeParam : DefaultPreset;

  // ⚠ Вікно рахується раз на зміну пресета, а не на кожен рендер: інакше ключ запиту зсувався б щомиті.
  const window = useMemo(() => presetWindow(preset, new Date()), [preset]);

  const sources = useQuery({
    queryKey: ['sources'],
    queryFn: () => apiFetch<SourceEntityStatus[]>('/api/v1/sources'),
  });

  const tags = useQuery({
    queryKey: ['piData', 'tags', entityId],
    queryFn: () => fetchRawTags(entityId ?? 0),
    enabled: entityId !== null,
  });

  // Тег з адреси, якщо він є серед тегів сутності; інакше — перший.
  const tag =
    tags.data?.tags.find((item) => item.sourcePath === tagParam)?.sourcePath ?? tags.data?.tags[0]?.sourcePath ?? null;
  const unit = tags.data?.tags.find((item) => item.sourcePath === tag)?.sourceUnitCode ?? null;
  const ref: RawSeriesRef | null = entityId === null || tag === null ? null : { entityId, path: tag, ...window };

  // D-465: сутність-шаблон подій (є мапінги подій) відкривається на «Подіях»; перемикач — в адресі.
  const hasEvents = (tags.data?.eventMapCount ?? 0) > 0;
  const view: PiDataView = !hasEvents || viewParam === 'series' ? 'series' : 'events';

  return (
    <>
      <PageHeader
        title={t('piData.title')}
        actions={
          <FilterRow>
            <Select
              size="xs"
              miw={240}
              label={t('piData.entity')}
              placeholder={t('piData.pickEntity')}
              searchable
              value={entityId === null ? null : String(entityId)}
              onChange={(value) => {
                setEntityId(value === null ? null : Number(value));
                setTag(null);
              }}
              data={(sources.data ?? []).map((source) => ({
                value: String(source.id),
                label: source.displayName ?? source.code,
              }))}
            />
            {hasEvents && (
              <SegmentedControl
                size="xs"
                aria-label={t('piData.viewLabel')}
                value={view}
                onChange={(value) => setView(value === 'series' ? 'series' : null)}
                data={[
                  { value: 'events', label: t('piData.viewEvents') },
                  { value: 'series', label: t('piData.viewSeries') },
                ]}
                data-pi-data-view=""
              />
            )}
            {view === 'series' && (
              <Select
                size="xs"
                miw={260}
                label={t('piData.tag')}
                searchable
                disabled={tags.data === undefined}
                value={tag}
                onChange={(value) => setTag(value)}
                data={(tags.data?.tags ?? []).map((item) => ({ value: item.sourcePath, label: item.sourcePath }))}
              />
            )}
            <Select
              size="xs"
              miw={180}
              label={t('piData.range')}
              allowDeselect={false}
              value={preset}
              onChange={(value) => setRange(value === DefaultPreset ? null : value)}
              data={RangePresets.map((value) => ({ value, label: presetLabel(value) }))}
            />
            {view === 'series' && ref !== null && <ExportButton series={ref} />}
          </FilterRow>
        }
      />

      <AsyncBoundary<SourceEntityStatus[]>
        isPending={sources.isPending}
        error={sources.error}
        data={sources.data}
        isEmpty={(all) => all.length === 0}
        emptyTitle={t('sources.empty')}
        emptyHint={t('sources.emptyHint')}
        onRetry={() => void sources.refetch()}
      >
        {() => null}
      </AsyncBoundary>

      {entityId === null ? (
        <Text c="dimmed">{t('piData.pickHint')}</Text>
      ) : (
        <AsyncBoundary<RawDataTagsView>
          isPending={tags.isPending}
          error={tags.error}
          data={tags.data}
          skeleton="form"
          onRetry={() => void tags.refetch()}
        >
          {(loaded) =>
            view === 'events' ? (
              <Suspense fallback={<Skeleton height={320} />}>
                <PiEventsPanel entityId={loaded.sourceEntityId} window={window} />
              </Suspense>
            ) : (
              <Stack gap="lg">
                {ref !== null && <SeriesSection series={ref} unit={unit} />}
                <CoverageSection entityId={loaded.sourceEntityId} window={window} />
                {ref !== null && (
                  <Section title={t('piData.pointsTitle')}>
                    <RawPointsTable
                      queryKey={['piData', 'points', ref.entityId, ref.path, ref.fromUtc, ref.toUtc]}
                      load={(cursor) => fetchRawPoints(ref, cursor)}
                    />
                  </Section>
                )}
                <Section
                  title={t('piData.eventsTitle')}
                  hint={t('piData.eventsHint')}
                  extra={
                    <Anchor component={Link} to={`/admin/sources?entity=${String(loaded.sourceEntityId)}`} size="sm">
                      {t('piData.runsLink')}
                    </Anchor>
                  }
                >
                  <EntityCoverageEvents entityId={loaded.sourceEntityId} />
                </Section>
              </Stack>
            )
          }
        </AsyncBoundary>
      )}
    </>
  );
}

function SeriesSection({ series, unit }: { readonly series: RawSeriesRef; readonly unit: string | null }): JSX.Element {
  const query = useQuery({
    queryKey: ['piData', 'series', series.entityId, series.path, series.fromUtc, series.toUtc],
    queryFn: () => fetchRawSeries(series),
  });

  return (
    <Section title={t('piData.chartTitle')}>
      <AsyncBoundary<RawSeriesView>
        isPending={query.isPending}
        error={query.error}
        data={query.data}
        isEmpty={(data) => data.totalPoints === 0}
        emptyTitle={t('piData.pointsEmpty')}
        emptyHint={t('piData.pointsEmptyHint')}
        onRetry={() => void query.refetch()}
      >
        {(data) => (
          <Stack gap="xs">
            <Text size="xs" c="dimmed">
              {t('piData.chartMeta', {
                points: data.totalPoints,
                buckets: data.buckets.length,
                seconds: data.bucketSeconds,
              })}
            </Text>
            <Suspense fallback={<Skeleton height={220} />}>
              <SeriesChart series={data} unit={unit} />
            </Suspense>
          </Stack>
        )}
      </AsyncBoundary>
    </Section>
  );
}

function CoverageSection({
  entityId,
  window,
}: {
  readonly entityId: number;
  readonly window: { readonly fromUtc: string; readonly toUtc: string };
}): JSX.Element {
  const query = useQuery({
    queryKey: ['piData', 'coverage', entityId, window.fromUtc, window.toUtc],
    queryFn: () => fetchRawCoverage(entityId, window),
  });

  return (
    <Section title={t('piData.coverageTitle')} hint={t('piData.coverageHint')}>
      <AsyncBoundary<RawCoverageView>
        isPending={query.isPending}
        error={query.error}
        data={query.data}
        onRetry={() => void query.refetch()}
      >
        {(data) => <CoverageSummary coverage={data} />}
      </AsyncBoundary>
    </Section>
  );
}

function Section({
  title,
  hint,
  extra,
  children,
}: {
  readonly title: string;
  readonly hint?: string | undefined;
  readonly extra?: JSX.Element | undefined;
  readonly children: JSX.Element;
}): JSX.Element {
  return (
    <Card withBorder padding="md" component="section" aria-label={title}>
      <Group justify="space-between" align="baseline" mb="xs">
        <Title order={2} size="h4">
          {title}
        </Title>
        {extra}
      </Group>
      {hint !== undefined && (
        <Text size="sm" c="dimmed" mb="xs">
          {hint}
        </Text>
      )}
      {children}
    </Card>
  );
}

/** «Експорт CSV» тега за вікно: серверний потік зі стелею й подією безпеки; відмова — текстом сервера. */
function ExportButton({ series }: { readonly series: RawSeriesRef }): JSX.Element {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  const run = async (): Promise<void> => {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      const file = await fetchRawExport(series);
      saveBlob(file.blob, file.fileName ?? 'pi-data.csv');
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Stack gap="xs">
      <Button size="xs" variant="default" loading={busy} onClick={() => void run()} data-pi-export="">
        {t('piData.exportCsv')}
      </Button>
      {error !== null && <ErrorAlert error={error} />}
    </Stack>
  );
}

/**
 * Підпис пресета — літеральні `t(...)`: ключ зі змінної вимагав би запису в `EndpointCoverageTests.DynamicKeySites`.
 */
function presetLabel(preset: RangePreset): string {
  switch (preset) {
    case '24h':
      return t('piData.range24h');
    case '7d':
      return t('piData.range7d');
    case '30d':
      return t('piData.range30d');
    case '90d':
      return t('piData.range90d');
    case 'year':
      return t('piData.rangeYear');
    case 'lastYear':
      return t('piData.rangeLastYear');
  }
}

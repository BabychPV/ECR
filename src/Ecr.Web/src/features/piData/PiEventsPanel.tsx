import { useState, type JSX } from 'react';
import {
  Alert,
  Anchor,
  Badge,
  Button,
  Card,
  Group,
  MultiSelect,
  Select,
  Stack,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { SourceEventStatuses, statusColor, statusExplanation, statusLabel } from '@/features/sources/sourceEventStatus';
import { documentHref } from '@/features/sources/sourceEventsData';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { DataTable } from '@/shared/ui/DataTable';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { FilterRow } from '@/shared/ui/FilterBar';
import { Timestamp } from '@/shared/ui/Timestamp';
import { useUrlNumber } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';
import type { RawWindow } from './api';
import { EntityCoverageEvents } from './EntityCoverageEvents';
import {
  fetchPiEventExport,
  fetchPiEventMaps,
  fetchPiEventRemovals,
  fetchPiEvents,
  formatProjectTime,
  formatUtcMoment,
  type PiEventFilters,
  type PiEventMapView,
  type PiEventMapsView,
  type PiEventRemovalView,
  type PiEventRemovalsView,
  type PiEventView,
  type PiEventsRef,
  type SourceEventLinkStatus,
} from './eventsApi';
import { PiEventAttributes } from './PiEventAttributes';
import { reasonLabel } from './piEventLabels';
import { saveBlob } from './saveBlob';

/**
 * «Дані з PI» → «Події» (`D-465`): події PI (Event Frames) сутності-шаблону за вікно (до року) — що синк прочитав
 * з PI востаннє, у який рядок документа лягло, і чи збігається комірка (з причиною, якщо ні); журнал синку за
 * період, журнал видалень повною звіркою, CSV.
 *
 * ⚠ Окремий лінивий чанк (`D-132`): сторінка часових рядів його не вантажить.
 * ⚠ Лише сховище: значення атрибутів — останнє прочитання синку; недоступний PI екран не ламає.
 */
export default function PiEventsPanel({
  entityId,
  window,
}: {
  readonly entityId: number;
  readonly window: RawWindow;
}): JSX.Element {
  const [mapParam, setMapParam] = useUrlNumber('map');
  const [statuses, setStatuses] = useState<SourceEventLinkStatus[]>([]);
  const [element, setElement] = useState('');
  const [name, setName] = useState('');
  const [debouncedElement] = useDebouncedValue(element, 300);
  const [debouncedName] = useDebouncedValue(name, 300);

  const maps = useQuery({
    queryKey: ['piData', 'eventMaps', entityId, window.fromUtc, window.toUtc],
    queryFn: () => fetchPiEventMaps(entityId, window),
  });

  const mapId = maps.data?.maps.find((map) => map.mapId === mapParam)?.mapId ?? maps.data?.maps[0]?.mapId ?? null;
  const filters: PiEventFilters = {
    mapId,
    statuses,
    element: debouncedElement,
    name: debouncedName,
  };
  const ref: PiEventsRef = { entityId, ...window, filters };
  const filtered = statuses.length > 0 || debouncedElement.trim().length > 0 || debouncedName.trim().length > 0;

  return (
    <AsyncBoundary<PiEventMapsView>
      isPending={maps.isPending}
      error={maps.error}
      data={maps.data}
      isEmpty={(data) => data.maps.length === 0}
      emptyTitle={t('piEvents.noMaps')}
      emptyHint={t('piEvents.noMapsHint')}
      skeleton="form"
      onRetry={() => void maps.refetch()}
    >
      {(loaded) => (
        <Stack gap="lg" data-pi-events="">
          <Section
            title={t('piEvents.listTitle')}
            hint={t('piEvents.listHint')}
            extra={mapId === null ? undefined : <ExportButton events={ref} />}
          >
            <Stack gap="sm">
              <FilterRow>
                <Select
                  size="xs"
                  miw={280}
                  label={t('piEvents.map')}
                  allowDeselect={false}
                  value={mapId === null ? null : String(mapId)}
                  onChange={(value) => setMapParam(value === null ? null : Number(value))}
                  data={loaded.maps.map((map) => ({
                    value: String(map.mapId),
                    label: mapLabel(map),
                  }))}
                  data-pi-events-map=""
                />
                <MultiSelect
                  size="xs"
                  miw={220}
                  label={t('piEvents.statusFilter')}
                  clearable
                  value={statuses}
                  onChange={(values) => setStatuses(SourceEventStatuses.filter((status) => values.includes(status)))}
                  data={SourceEventStatuses.map((status) => ({
                    value: status,
                    label: statusLabel(status),
                  }))}
                />
                <TextInput
                  size="xs"
                  label={t('piEvents.elementFilter')}
                  value={element}
                  maxLength={200}
                  onChange={(event) => setElement(event.currentTarget.value)}
                  data-pi-events-element=""
                />
                <TextInput
                  size="xs"
                  label={t('piEvents.nameFilter')}
                  value={name}
                  maxLength={200}
                  onChange={(event) => setName(event.currentTarget.value)}
                  data-pi-events-name=""
                />
              </FilterRow>
              {loaded.maps.some((map) => map.mapId === mapId && map.hasMonthColumnFields) && (
                <Alert color="statusWarning" variant="light" data-pi-events-month-warning="">
                  {t('piEvents.monthColumnWarning')}
                </Alert>
              )}
              {mapId !== null && (
                <EventsList
                  events={ref}
                  filtered={filtered}
                  onClearFilters={() => {
                    setStatuses([]);
                    setElement('');
                    setName('');
                  }}
                />
              )}
            </Stack>
          </Section>

          <CoverageSection entityId={entityId} maps={loaded} />

          <Section title={t('piEvents.removedTitle')} hint={t('piEvents.removedHint')}>
            <Removals entityId={entityId} window={window} />
          </Section>
        </Stack>
      )}
    </AsyncBoundary>
  );
}

function mapLabel(map: PiEventMapView): string {
  const document = map.documentVisible && map.documentKey !== null ? map.documentKey : t('piEvents.documentHidden');
  const paused = map.isActive ? '' : ` (${t('piEvents.mapPaused')})`;

  return `${document} · ${map.tableCode}${paused}`;
}

function EventsList({
  events,
  filtered,
  onClearFilters,
}: {
  readonly events: PiEventsRef;
  readonly filtered: boolean;
  readonly onClearFilters: () => void;
}): JSX.Element {
  const [selected, setSelected] = useState<string | null>(null);
  const query = useInfiniteQuery({
    queryKey: [
      'piData',
      'events',
      events.entityId,
      events.fromUtc,
      events.toUtc,
      events.filters.mapId,
      events.filters.statuses.join(','),
      events.filters.element,
      events.filters.name,
    ],
    queryFn: ({ pageParam }: { pageParam: string | null }) => fetchPiEvents(events, pageParam),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last?.nextCursor ?? null,
  });

  const rows: readonly PiEventView[] = (query.data?.pages ?? []).flatMap((page) => page?.items ?? []);
  const total = query.data?.pages[0]?.totalCount ?? undefined;
  const current = rows.find((row) => String(row.linkId) === selected) ?? null;

  return (
    <Stack gap="sm">
      <DataTable<PiEventView>
        caption={t('piEvents.listTitle')}
        columns={[
          {
            key: 'start',
            label: t('piEvents.start'),
            render: (row) => <EventTime row={row} />,
            sortable: false,
          },
          {
            key: 'event',
            label: t('piEvents.event'),
            sortable: false,
            minWidth: 200,
            render: (row) => (
              <Stack gap="xs">
                <Text size="sm">{row.eventName ?? row.eventId}</Text>
                <Text size="xs" c="dimmed" ff="monospace">
                  {row.eventId}
                </Text>
                {row.primaryElement !== null && (
                  <Text size="xs" c="dimmed" ff="monospace">
                    {row.primaryElement}
                  </Text>
                )}
              </Stack>
            ),
          },
          {
            key: 'modified',
            label: t('piEvents.modified'),
            sortable: false,
            render: (row) => <Timestamp value={row.sourceModifiedUtc} />,
          },
          {
            key: 'status',
            label: t('piEvents.status'),
            sortable: false,
            render: (row) => <EventStatus row={row} />,
          },
          {
            key: 'row',
            label: t('piEvents.row'),
            sortable: false,
            minWidth: 180,
            render: (row) => <EventRowLink row={row} />,
          },
          {
            key: 'attributes',
            label: t('piEvents.attributesColumn'),
            sortable: false,
            render: (row) => <AttributesSummary row={row} />,
          },
        ]}
        rows={rows}
        rowKey={(row) => String(row.linkId)}
        rowLabel={(row) => row.eventName ?? row.eventId}
        onRowClick={(row) => setSelected(String(row.linkId))}
        rowFocusable
        selectedKey={selected ?? undefined}
        isPending={query.isPending}
        error={query.error}
        onRetry={() => void query.refetch()}
        filtered={filtered}
        emptyTitle={t('piEvents.empty')}
        emptyHint={t('piEvents.emptyHint')}
        noMatchTitle={t('piEvents.noMatch')}
        onClearFilters={onClearFilters}
        clearFiltersLabel={t('piEvents.clearFilters')}
        total={total}
        {...(query.hasNextPage
          ? {
              onShowMore: () => void query.fetchNextPage(),
              showMoreLabel: t('piData.more'),
              isFetchingMore: query.isFetchingNextPage,
            }
          : {})}
      />
      {current !== null && (
        <Card withBorder padding="sm" aria-live="polite" data-pi-event-detail={current.linkId}>
          <Group justify="space-between" mb="xs">
            <Title order={3} size="h5">
              {t('piEvents.attributesTitle', {
                event: current.eventName ?? current.eventId,
              })}
            </Title>
            <Text size="xs" c="dimmed">
              {t('piEvents.lastSync')} <Timestamp value={current.lastSyncAt} />
            </Text>
          </Group>
          <PiEventAttributes
            attributes={current.attributes}
            caption={t('piEvents.attributesTitle', {
              event: current.eventName ?? current.eventId,
            })}
          />
        </Card>
      )}
    </Stack>
  );
}

function EventTime({ row }: { readonly row: PiEventView }): JSX.Element {
  return (
    <Stack gap="xs">
      <Text size="sm" title={formatUtcMoment(row.startUtc)} data-pi-event-start="">
        {formatProjectTime(row.startUtc, row.timeZoneId, row.startLocal)}
      </Text>
      <Text size="xs" c="dimmed">
        {formatUtcMoment(row.startUtc)}
      </Text>
      <Text size="xs" c="dimmed" title={row.endUtc === null ? undefined : formatUtcMoment(row.endUtc)}>
        {row.endUtc === null
          ? t('piEvents.stillOpen')
          : `→ ${formatProjectTime(row.endUtc, row.timeZoneId, row.endLocal)}`}
      </Text>
    </Stack>
  );
}

function EventStatus({ row }: { readonly row: PiEventView }): JSX.Element {
  const status = row.status as SourceEventLinkStatus;

  return (
    <Stack gap="xs">
      <Badge
        variant="light"
        color={statusColor(status)}
        title={statusExplanation(status)}
        data-pi-event-status={row.status}
      >
        {statusLabel(status)}
      </Badge>
      {row.reason !== null && (
        <Text size="xs" c="dimmed" data-pi-event-reason={row.reason}>
          {reasonLabel(row.reason)}
        </Text>
      )}
    </Stack>
  );
}

function EventRowLink({ row }: { readonly row: PiEventView }): JSX.Element {
  if (row.row !== null) {
    return (
      <Stack gap="xs">
        <Anchor
          component={Link}
          to={documentHref(row.row.documentId, row.row.periodKey)}
          size="sm"
          data-pi-event-document=""
        >
          {row.row.documentKey}
        </Anchor>
        <Text size="xs" c="dimmed" ff="monospace">
          {row.row.rowKey} · {row.row.periodKey}
        </Text>
      </Stack>
    );
  }

  return (
    <Text size="xs" c="dimmed" data-pi-event-row-hidden={row.rowHidden ? '' : undefined}>
      {row.rowHidden ? t('piEvents.rowHidden') : t('piEvents.noRow')}
    </Text>
  );
}

function AttributesSummary({ row }: { readonly row: PiEventView }): JSX.Element {
  const compared = row.attributes.filter((attribute) => attribute.comparison !== null);
  const differs = compared.filter(
    (attribute) => attribute.comparison !== 'Match' && attribute.comparison !== 'NoSourceValue',
  ).length;

  return (
    <Badge variant="light" color={differs === 0 ? 'statusSuccess' : 'statusWarning'} data-pi-event-differs={differs}>
      {differs === 0 ? t('piEvents.allMatch') : t('piEvents.differs', { count: differs, total: compared.length })}
    </Badge>
  );
}

function CoverageSection({
  entityId,
  maps,
}: {
  readonly entityId: number;
  readonly maps: PiEventMapsView;
}): JSX.Element {
  const [period, setPeriod] = useState<number | null>(null);
  const shown = period ?? maps.periods[maps.periods.length - 1]?.periodKey ?? null;

  return (
    <Section title={t('piEvents.coverageTitle')} hint={t('piEvents.coverageHint')}>
      <Stack gap="sm">
        {maps.periods.length > 0 && (
          <Select
            size="xs"
            maw={240}
            label={t('piEvents.period')}
            allowDeselect={false}
            value={shown === null ? null : String(shown)}
            onChange={(value) => setPeriod(value === null ? null : Number(value))}
            data={maps.periods.map((item) => ({
              value: String(item.periodKey),
              label: String(item.periodKey),
            }))}
          />
        )}
        <EntityCoverageEvents entityId={entityId} periodKey={shown} />
      </Stack>
    </Section>
  );
}

function Removals({ entityId, window }: { readonly entityId: number; readonly window: RawWindow }): JSX.Element {
  const query = useQuery({
    queryKey: ['piData', 'eventRemovals', entityId, window.fromUtc, window.toUtc],
    queryFn: () => fetchPiEventRemovals(entityId, window),
  });

  return (
    <AsyncBoundary<PiEventRemovalsView>
      isPending={query.isPending}
      error={query.error}
      data={query.data}
      onRetry={() => void query.refetch()}
    >
      {(data) => (
        <Stack gap="xs">
          {data.isTruncated && (
            <Text size="xs" c="dimmed">
              {t('piEvents.removedTruncated', { count: data.items.length })}
            </Text>
          )}
          <DataTable<PiEventRemovalView>
            caption={t('piEvents.removedTitle')}
            columns={[
              {
                key: 'removedAt',
                label: t('piEvents.removedAt'),
                render: (row) => <Timestamp value={row.removedAt} />,
              },
              {
                key: 'event',
                label: t('piEvents.event'),
                sortable: false,
                render: (row) => (
                  <Stack gap="xs">
                    <Text size="sm">{row.eventName ?? row.eventId ?? '—'}</Text>
                    <Text size="xs" c="dimmed" ff="monospace">
                      {row.eventId ?? ''}
                    </Text>
                  </Stack>
                ),
              },
              {
                key: 'start',
                label: t('piEvents.startUtc'),
                sortable: false,
                render: (row) => <Text size="sm">{formatUtcMoment(row.eventStartUtc)}</Text>,
              },
              {
                key: 'row',
                label: t('piEvents.row'),
                sortable: false,
                render: (row) =>
                  row.row !== null ? (
                    <Stack gap="xs">
                      <Anchor component={Link} to={documentHref(row.row.documentId, row.row.periodKey)} size="sm">
                        {row.row.documentKey}
                      </Anchor>
                      <Text size="xs" c="dimmed" ff="monospace">
                        {row.row.rowKey} · {row.row.periodKey}
                      </Text>
                    </Stack>
                  ) : (
                    <Text size="xs" c="dimmed">
                      {row.rowHidden ? t('piEvents.rowHidden') : '—'}
                    </Text>
                  ),
              },
              {
                key: 'runId',
                label: t('piEvents.run'),
                mono: true,
                sortable: false,
                render: (row) => row.runId ?? '—',
              },
            ]}
            rows={data.items}
            rowKey={(row) => `${row.removedAt}:${row.eventId ?? ''}:${row.row?.rowKey ?? ''}`}
            emptyTitle={t('piEvents.removedEmpty')}
            emptyHint={t('piEvents.removedEmptyHint')}
          />
        </Stack>
      )}
    </AsyncBoundary>
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

/** «Експорт CSV» подій × полів за вікно: серверний потік зі стелею й подією безпеки; відмова — текстом сервера. */
function ExportButton({ events }: { readonly events: PiEventsRef }): JSX.Element {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  const run = async (): Promise<void> => {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      const file = await fetchPiEventExport(events);
      saveBlob(file.blob, file.fileName ?? 'pi-events.csv');
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Stack gap="xs">
      <Button size="xs" variant="default" loading={busy} onClick={() => void run()} data-pi-events-export="">
        {t('piData.exportCsv')}
      </Button>
      {error !== null && <ErrorAlert error={error} />}
    </Stack>
  );
}

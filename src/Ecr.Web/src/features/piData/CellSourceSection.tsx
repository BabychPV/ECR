import { Suspense, lazy, useState, type JSX } from 'react';
import { Badge, Box, Button, Group, Stack, Text, Title } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { collectionRunErrorText } from '@/features/integration/collectionRunMessage';
import { KeyValue, type KeyValueItem } from '@/shared/ui/KeyValue';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { Timestamp } from '@/shared/ui/Timestamp';
import { t } from '@/shared/i18n';
import { formatMonthYear } from '@/shared/format/period';
import { fetchCellSource, type CellFoldView, type CellRef, type CellSourceItem, type CellSourceView } from './api';
import { formatProjectTime, formatUtcMoment, piValueText, type CellEventSourceItem } from './eventsApi';
import { ComparisonCell } from './PiEventAttributes';
import { lastCollectionItems } from './lastCollectionItems';
import { exactDecimal } from './RawPointsTable';

/** Вікно з графіком і точками — окремий лінивий чанк: відкривають його рідко, а секцію бачать у кожній комірці. */
const CellSourceModal = lazy(async () => ({ default: (await import('./CellSourceModal')).CellSourceModal }));

/**
 * «Звідки це значення» (`D-463`) — секція вкладки Info інспектора документа для комірки, яку живить мапінг PI.
 *
 * ⛔ Доступ — рівно доступ до комірки: сервер відповідає тією самою 404, що й зріз, на все, чого людина не бачить,
 * і точки лише вікна ПЕРІОДУ цієї комірки. Клієнт нічого не вирішує сам: комірка без мапінгу дає порожній
 * перелік — і секції просто немає.
 *
 * ⚠ Запит — лише коли вкладка Info відкрита й комірка обрана (компонент монтується там), з кешем на комірку:
 * рух курсора туди-сюди не повторює запитів.
 */
export function CellSourceSection({ cell }: { readonly cell: CellRef }): JSX.Element | null {
  const query = useQuery({
    queryKey: ['cellSource', cell.documentId, cell.tableInstanceId, cell.rowKey, cell.columnDefId],
    queryFn: () => fetchCellSource(cell),
    staleTime: 60_000,
    retry: false,
  });

  // ⚠ Помилка й завантаження — без шуму: секція допоміжна, а відмова доступу тут означає «не показувати».
  if (query.data === undefined) return null;

  // D-465: рядок події PI (`EF-…`) — подія й атрибут цієї колонки; мапінг збору — тег і згортка.
  return (
    <>
      {query.data.sources.length > 0 && <Sources cell={cell} view={query.data} />}
      {query.data.eventSources.length > 0 && <EventSources items={query.data.eventSources} />}
    </>
  );
}

/**
 * «Звідки це значення» для комірки рядка події PI (`D-465`): подія (ID, назва, час у поясі проєкту з UTC), шаблон,
 * атрибут, прочитане з PI востаннє, що записав би синк, що в комірці, порівняння з причиною, стан зв'язку й
 * останній синк. Доступ — рівно доступ до комірки (сервер).
 */
function EventSources({ items }: { readonly items: readonly CellEventSourceItem[] }): JSX.Element {
  return (
    <Box mt="md" data-cell-event-source="">
      <Title order={3} size="h5" mb="xs">
        {t('cellSource.title')}
      </Title>
      <Text size="xs" c="dimmed" mb="xs">
        {t('cellSource.eventHint')}
      </Text>
      <Stack gap="md">
        {items.map((item) => (
          <KeyValue key={`${String(item.mapId)}:${String(item.event.linkId)}`} items={eventItems(item)} />
        ))}
      </Stack>
    </Box>
  );
}

function eventItems(item: CellEventSourceItem): KeyValueItem[] {
  const { event, attribute } = item;

  return [
    {
      label: t('cellSource.event'),
      value: (
        <Stack gap="xs">
          <Text size="sm">{event.eventName ?? event.eventId}</Text>
          <Text size="xs" c="dimmed" ff="monospace" data-cell-event-id="">
            {event.eventId}
          </Text>
        </Stack>
      ),
    },
    {
      label: t('cellSource.template'),
      value: (
        <Group gap="xs" wrap="nowrap">
          <Text size="sm">{item.sourceEntityName ?? item.sourceEntityCode}</Text>
          {!item.mapIsActive && (
            <Badge size="xs" color="statusWarning" variant="light">
              {t('cellSource.paused')}
            </Badge>
          )}
        </Group>
      ),
    },
    {
      label: t('cellSource.eventTime'),
      value: (
        <Stack gap="xs">
          <Text size="sm" title={formatUtcMoment(event.startUtc)}>
            {formatProjectTime(event.startUtc, event.timeZoneId, event.startLocal)}
            {event.endUtc === null
              ? ` · ${t('piEvents.stillOpen')}`
              : ` → ${formatProjectTime(event.endUtc, event.timeZoneId, event.endLocal)}`}
          </Text>
          <Text size="xs" c="dimmed">
            {formatUtcMoment(event.startUtc)}
          </Text>
        </Stack>
      ),
    },
    { label: t('cellSource.element'), value: event.primaryElement, mono: true },
    { label: t('cellSource.attribute'), value: attribute.attribute, mono: true },
    {
      label: t('piEvents.piValue'),
      value: `${piValueText(attribute)}${attribute.piUnit === null ? '' : ` ${attribute.piUnit}`}`,
      mono: true,
    },
    { label: t('piEvents.expected'), value: attribute.expectedValue ?? '—', mono: true },
    { label: t('cellSource.storedValue'), value: attribute.cellValue ?? '—', mono: true },
    { label: t('piEvents.comparison'), value: <ComparisonCell attribute={attribute} /> },
    { label: t('cellSource.linkStatus'), value: event.status, mono: true },
    { label: t('piEvents.modified'), value: <Timestamp value={event.sourceModifiedUtc} /> },
    { label: t('piEvents.lastSync'), value: <Timestamp value={event.lastSyncAt} /> },
  ];
}

function Sources({ cell, view }: { readonly cell: CellRef; readonly view: CellSourceView }): JSX.Element {
  const [opened, setOpened] = useState<CellSourceItem | null>(null);

  return (
    <Box mt="md" data-cell-source="">
      <Title order={3} size="h5" mb="xs">
        {t('cellSource.title')}
      </Title>
      <Text size="xs" c="dimmed" mb="xs">
        {t('cellSource.hint')}
      </Text>
      <Stack gap="md">
        {view.sources.map((source) => (
          <Stack key={source.mapId} gap="xs">
            <KeyValue items={items(view, source)} />
            {source.events.length > 0 && (
              <Stack gap="xs" data-cell-source-events="">
                <Text size="xs" fw={500}>
                  {t('cellSource.events')}
                </Text>
                {source.events.map((event) => (
                  <Group key={event.id} gap="xs" wrap="nowrap" align="flex-start">
                    <StatusBadge kind="coverage" state={event.status} />
                    <Text size="xs" style={{ whiteSpace: 'pre-wrap' }}>
                      {event.details == null ? '—' : collectionRunErrorText(event.details)}
                    </Text>
                  </Group>
                ))}
              </Stack>
            )}
            <Group>
              <Button size="xs" variant="light" onClick={() => setOpened(source)} data-cell-source-open="">
                {t('cellSource.showPoints', { count: source.pointCount })}
              </Button>
            </Group>
          </Stack>
        ))}
      </Stack>
      {opened !== null && (
        <Suspense fallback={null}>
          <CellSourceModal cell={cell} view={view} source={opened} onClose={() => setOpened(null)} />
        </Suspense>
      )}
    </Box>
  );
}

function items(view: CellSourceView, source: CellSourceItem): KeyValueItem[] {
  return [
    { label: t('cellSource.tag'), value: source.sourcePath, mono: true },
    { label: t('cellSource.entity'), value: source.sourceEntityName ?? source.sourceEntityCode },
    {
      label: t('cellSource.aggregation'),
      value: (
        <Group gap="xs" wrap="nowrap">
          <Text size="sm">{source.aggregation ?? '—'}</Text>
          {!source.isActive && (
            <Badge size="xs" color="statusWarning" variant="light">
              {t('cellSource.paused')}
            </Badge>
          )}
        </Group>
      ),
    },
    {
      label: t('cellSource.units'),
      value:
        source.sourceUnitCode == null && source.targetUnitCode == null
          ? null
          : `${source.sourceUnitCode ?? '—'} → ${source.targetUnitCode ?? '—'}`,
      mono: true,
    },
    { label: t('cellSource.window'), value: <WindowLabel view={view} /> },
    { label: t('cellSource.points'), value: String(source.pointCount), mono: true },
    ...foldItems(source),
    ...lastCollectionItems(source.lastCollection),
  ];
}

/**
 * Вікно згортки: для колонки місяця — назва місяця (D-462: колонка M3 річного періоду згортає лише березень), далі
 * точні межі в UTC-моментах; без вікна — «не комірка цього періоду».
 */
function WindowLabel({ view }: { readonly view: CellSourceView }): JSX.Element {
  if (view.periodFromUtc == null || view.periodToUtc == null) {
    return <Text size="sm">{t('cellSource.noWindow')}</Text>;
  }

  return (
    <Stack gap="xs" data-cell-source-window={view.windowMonth ?? 'period'}>
      {view.windowMonth != null && (
        <Text size="sm" fw={500}>
          {t('cellSource.windowMonth', {
            month: formatMonthYear(Math.floor(view.windowPeriodKey / 100), view.windowMonth),
          })}
        </Text>
      )}
      <Text size="sm">
        <Timestamp value={view.periodFromUtc} /> — <Timestamp value={view.periodToUtc} />
      </Text>
    </Stack>
  );
}

/** «Значення за правилом згортки» і що в комірці — лише те, що відомо з журналу змін самої комірки. */
function foldItems(source: CellSourceItem): KeyValueItem[] {
  const fold = source.fold;
  if (fold == null) return [];

  const unit = source.targetUnitCode == null ? '' : ` ${source.targetUnitCode}`;

  return [
    {
      label: t('cellSource.foldValue'),
      value:
        fold.state === 'Value' && fold.value != null ? (
          <Text size="sm" ff="monospace" data-cell-source-fold="">
            {`${exactDecimal(fold.value)}${unit}`}
          </Text>
        ) : (
          <Text size="sm" c="dimmed" data-cell-source-fold={fold.state}>
            {foldStateText(fold.state)}
          </Text>
        ),
    },
    {
      label: t('cellSource.storedValue'),
      value: (
        <Group gap="xs" wrap="nowrap">
          <Text size="sm" ff="monospace">
            {fold.storedValue == null ? '—' : exactDecimal(fold.storedValue)}
          </Text>
          <ComparisonBadge fold={fold} />
        </Group>
      ),
    },
  ];
}

function ComparisonBadge({ fold }: { readonly fold: CellFoldView }): JSX.Element | null {
  const text = comparisonText(fold.comparison);
  if (text === null) return null;

  return (
    <Badge
      size="xs"
      variant="light"
      color={fold.comparison === 'Match' ? 'statusSuccess' : 'statusWarning'}
      data-cell-source-comparison={fold.comparison}
    >
      {text}
    </Badge>
  );
}

/** Літеральні `t(...)`: ключ зі змінної вимагав би запису в `EndpointCoverageTests.DynamicKeySites`. */
function comparisonText(comparison: string): string | null {
  switch (comparison) {
    case 'Match':
      return t('cellSource.compareMatch');
    case 'ManualEdit':
      return t('cellSource.compareManualEdit');
    case 'NotUpdated':
      return t('cellSource.compareNotUpdated');
    case 'NotWritten':
      return t('cellSource.compareNotWritten');
    default:
      return null;
  }
}

function foldStateText(state: string): string {
  switch (state) {
    case 'NoData':
      return t('cellSource.foldNoData');
    case 'Cleared':
      return t('cellSource.foldCleared');
    case 'MixedUnits':
      return t('cellSource.foldMixedUnits');
    case 'UnitFailure':
      return t('cellSource.foldUnitFailure');
    case 'TooManyPoints':
      return t('cellSource.foldTooManyPoints');
    case 'NoAggregation':
      return t('cellSource.foldNoAggregation');
    default:
      return state;
  }
}

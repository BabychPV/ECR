import { Suspense, lazy, useState, type JSX } from 'react';
import { Badge, Box, Button, Group, Stack, Text, Title } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { collectionRunErrorText } from '@/features/integration/collectionRunMessage';
import { KeyValue, type KeyValueItem } from '@/shared/ui/KeyValue';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { Timestamp } from '@/shared/ui/Timestamp';
import { t } from '@/shared/i18n';
import { fetchCellSource, type CellRef, type CellSourceItem, type CellSourceView } from './api';
import { lastCollectionItems } from './lastCollectionItems';

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
  if (query.data === undefined || query.data.sources.length === 0) return null;

  return <Sources cell={cell} view={query.data} />;
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
    {
      label: t('cellSource.window'),
      value: (
        <>
          <Timestamp value={view.periodFromUtc} /> — <Timestamp value={view.periodToUtc} />
        </>
      ),
    },
    { label: t('cellSource.points'), value: String(source.pointCount), mono: true },
    ...lastCollectionItems(source.lastCollection),
  ];
}

import { useMemo, useState, type JSX } from 'react';
import { Alert, Button, Group, Stack, Text } from '@mantine/core';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router';
import type { MappingPreview, SourceEntityStatus } from '@/api/types';
import { CollectionRunStateBadge } from '@/features/integration/CollectionRunStateBadge';
import { CollectionScheduleTab } from '@/features/integration/CollectionScheduleTab';
import { listCollectionSchedules } from '@/features/integration/scheduleApi';
import { collectionSchedulesKey } from '@/features/integration/useCollectionSchedules';
import { WindowDays, fetchMappingPreview, windowFrom } from '@/features/mapping/api';
import { CreateMappingModal } from '@/features/mapping/CreateMappingModal';
import { MappingGaps } from '@/features/mapping/MappingGaps';
import { MappingRows } from '@/features/mapping/MappingRows';
import { PipelineStepCard } from '@/features/pipeline/PipelineStepCard';
import { pipelineSteps, scheduleOf, type PipelineStep } from '@/features/pipeline/pipelineSteps';
import { formatNumber } from '@/shared/format/number';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { KeyValue } from '@/shared/ui/KeyValue';
import { Timestamp } from '@/shared/ui/Timestamp';
import { t } from '@/shared/i18n';

/**
 * Конвеєр однієї сутності джерела: п'ять кроків (`pipelineSteps.ts`) із
 * реальними точками після кожного й редагуванням наявними компонентами.
 *
 * ⚠ Ключі запитів ті самі, що й на `/admin/mapping` і в шухляді з'єднання:
 * кеш спільний, і дія на одному екрані не лишає іншого застарілим.
 */
export function EntityPipeline({
  entity,
  allowed,
  sourcesHref,
}: {
  readonly entity: SourceEntityStatus;
  readonly allowed: boolean;

  /** Адреса екрана з'єднань; приходить від сторінки — `features` не імпортує `app/routes`. */
  readonly sourcesHref: string;
}): JSX.Element {
  const queryClient = useQueryClient();
  const [createOpened, setCreateOpened] = useState(false);

  // ⚠ Той самий ключ і ті самі опції, що й `useCollectionSchedules` —
  // вкладка розкладу нижче читає той самий запис кешу, а не робить другий
  // запит.
  const schedules = useQuery({
    queryKey: collectionSchedulesKey(entity.dataSourceCode),
    queryFn: () => listCollectionSchedules({ dataSource: entity.dataSourceCode }),
    refetchOnWindowFocus: false,
  });

  // ⚠ Вікно — один раз на монтування: інакше ключ зсувався б щорендеру.
  const window = useMemo(() => windowFrom(new Date()), []);

  const preview = useQuery({
    queryKey: ['mapping-preview', entity.id, window.fromUtc],
    queryFn: () => fetchMappingPreview(entity.id, window),
    // Неактивну сутність сервер не переглядає (404 `sourceEntity`): кроки 3–5
    // тоді `idle` без запиту, а не картка помилки.
    enabled: entity.isActive,
  });

  const head = pipelineSteps({
    entity,
    schedule: scheduleOf(schedules.data, entity.id),
    preview: undefined,
  });

  return (
    <Stack gap="md">
      <Text size="sm" c="dimmed">
        {t('pipeline.intro', { days: WindowDays })}
      </Text>

      <Stack gap="md">
        <SourceStep step={head[0] as PipelineStep} entity={entity} sourcesHref={sourcesHref} />

        <PipelineStepCard
          step={head[1] as PipelineStep}
          index={2}
          title={t('pipeline.step.schedule')}
          hint={t('pipeline.stepHint.schedule')}
        >
          <CollectionScheduleTab sourceEntityId={entity.id} dataSource={entity.dataSourceCode} />
        </PipelineStepCard>

        {!entity.isActive && <IdleSteps head={head} />}

        {entity.isActive && (
          <AsyncBoundary<MappingPreview>
            isPending={preview.isPending}
            error={preview.error}
            data={preview.data}
            skeleton="table"
            onRetry={() => void preview.refetch()}
          >
            {(data) => {
              const [, , collect, map, emit] = pipelineSteps({ entity, schedule: undefined, preview: data });

              return (
                <>
                  <PipelineStepCard
                    step={collect as PipelineStep}
                    index={3}
                    title={t('pipeline.step.collect')}
                    hint={t('pipeline.stepHint.collect')}
                    zeroHint={t('pipeline.zero.collect')}
                  >
                    {/* ⛔ Урізана серія дає правдоподібне число — мовчати не можна. */}
                    {data.isTruncated && (
                      <Alert color="statusWarning" title={t('mapping.truncated')}>
                        {t('mapping.truncatedHint')}
                      </Alert>
                    )}
                  </PipelineStepCard>

                  <PipelineStepCard
                    step={map as PipelineStep}
                    index={4}
                    title={t('pipeline.step.map')}
                    hint={t('pipeline.stepHint.map')}
                    zeroHint={t('pipeline.zero.map')}
                  >
                    {allowed && (
                      <Group>
                        <Button size="xs" variant="default" onClick={() => setCreateOpened(true)}>
                          {t('mapping.create')}
                        </Button>
                      </Group>
                    )}
                    <MappingRows preview={data} allowed={allowed} />
                  </PipelineStepCard>

                  <PipelineStepCard
                    step={emit as PipelineStep}
                    index={5}
                    title={t('pipeline.step.emit')}
                    hint={t('pipeline.stepHint.emit')}
                    zeroHint={t('pipeline.zero.emit')}
                  >
                    <MappingGaps preview={data} />
                  </PipelineStepCard>
                </>
              );
            }}
          </AsyncBoundary>
        )}
      </Stack>

      <CreateMappingModal
        sourceEntityId={entity.id}
        dataSourceId={entity.dataSourceId}
        opened={createOpened}
        onClose={() => setCreateOpened(false)}
        onCreated={() => void queryClient.invalidateQueries({ queryKey: ['mapping-preview', entity.id] })}
      />
    </Stack>
  );
}

/** Кроки 3–5 неактивної сутності: даних до них не доходить, причина — на кроці 1. */
function IdleSteps({ head }: { readonly head: readonly PipelineStep[] }): JSX.Element {
  return (
    <>
      <PipelineStepCard
        step={head[2] as PipelineStep}
        index={3}
        title={t('pipeline.step.collect')}
        hint={t('pipeline.stepHint.collect')}
      />
      <PipelineStepCard
        step={head[3] as PipelineStep}
        index={4}
        title={t('pipeline.step.map')}
        hint={t('pipeline.stepHint.map')}
      />
      <PipelineStepCard
        step={head[4] as PipelineStep}
        index={5}
        title={t('pipeline.step.emit')}
        hint={t('pipeline.stepHint.emit')}
      />
    </>
  );
}

function SourceStep({
  step,
  entity,
  sourcesHref,
}: {
  readonly step: PipelineStep;
  readonly entity: SourceEntityStatus;
  readonly sourcesHref: string;
}): JSX.Element {
  const run = entity.lastRun;

  return (
    <PipelineStepCard step={step} index={1} title={t('pipeline.step.source')} hint={t('pipeline.stepHint.source')}>
      <KeyValue
        items={[
          { label: t('pipeline.connection'), value: entity.dataSourceCode, mono: true },
          { label: t('pipeline.transport'), value: entity.transport, mono: true },
          { label: t('mapping.entity'), value: entity.entityPath ?? entity.code, mono: true },
          {
            label: t('pipeline.collection'),
            value: entity.isActive ? t('pipeline.collectionOn') : t('pipeline.collectionOff'),
          },
          {
            label: t('pipeline.lastRun'),
            value:
              run === null ? (
                t('pipeline.noRun')
              ) : (
                <Group gap="xs">
                  <CollectionRunStateBadge state={run.status} />
                  <Timestamp value={run.finishedAt} />
                  <Text size="sm">{t('pipeline.retrieved', { points: formatNumber(run.pointsRetrieved) })}</Text>
                </Group>
              ),
          },
        ]}
      />
      <Group>
        <Button size="xs" variant="default" component={Link} to={sourcesHref}>
          {t('pipeline.openSources')}
        </Button>
      </Group>
    </PipelineStepCard>
  );
}

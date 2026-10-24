import { Suspense, lazy, useEffect, type JSX } from 'react';
import { Select, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { SourceEntityStatus } from '@/api/types';
import { routes } from '@/app/routes';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { useUrlNumber } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';

/**
 * Конвеєр сутності — окремим чанком (`D-132`): без обраної сутності екран показує лише вибір, а конвеєр тягнув у
 * маршрут редактор розкладу (`NumberInput`/`Switch`), рядки й прогалини мапінгу, діалог нового мапінгу з каталогом
 * PI AF. Вантажиться заздалегідь — щойно в адресі є `?entity=` або приїхав перелік сутностей (див. `useEffect`
 * нижче), тобто паралельно із запитом переліку, а не після нього. Стиль — `loadX`/`lazy(async () => ...)`, як
 * `GroupsPage.tsx`.
 */
const loadEntityPipeline = () => import('@/features/pipeline/EntityPipeline');
const EntityPipeline = lazy(async () => ({ default: (await loadEntityPipeline()).EntityPipeline }));

/**
 * Редактор конвеєра даних (`ФВ-14.3`, область 9; `B21` §7).
 *
 * ⚠ Конвеєр — той, що система СПРАВДІ виконує для сутності джерела:
 * з'єднання → розклад → збір → мапінг → запис у документ. Декларативного
 * конвеєра `B22` §2 (`join`/`group`/`compute`/`script`) на сервері немає
 * (`BE-21c` не визначено, `D-235`), і вигадувати його екраном без ендпоінтів
 * не можна: такий редактор нічого б не зберігав.
 *
 * ⛔ Кожен крок показує реальні точки вікна ПІСЛЯ себе (`B21` §7: «попередній
 * перегляд даних після кожного кроку на реальній вибірці»), а крок, що
 * звужує набір до нуля, підсвічено. Редагування — наявними компонентами на
 * наявних ендпоінтах: розклад (`/api/v1/collection-schedules`), мапінги
 * (`/api/v1/entity-field-maps`: завести, призупинити, відновити, прибрати).
 */
export function PipelinePage(): JSX.Element {
  const [entityId, setEntityId] = useUrlNumber('entity');
  const session = useSession();
  // ⛔ L9-18: під симуляцією сервер відхиляє КОЖЕН не-GET (`ECR-SIM-0403`); `can()` бачить права ЦІЛІ.
  const mayManage = can(session.data, 'Integration.Manage') && session.data?.isSimulation !== true;

  const sources = useQuery({
    queryKey: ['sources'],
    queryFn: () => apiFetch<SourceEntityStatus[]>('/api/v1/sources'),
  });

  const entity = (sources.data ?? []).find((source) => source.id === entityId);

  // Чанк конвеєра — заздалегідь: сутність уже обрана в адресі або перелік для вибору приїхав.
  const likely = entityId !== null || sources.data !== undefined;
  useEffect(() => {
    if (likely) void loadEntityPipeline();
  }, [likely]);

  return (
    <>
      <PageHeader
        title={t('pipeline.title')}
        actions={
          <Select
            size="xs"
            miw={260}
            label={t('mapping.entity')}
            placeholder={t('mapping.pick')}
            value={entityId === null ? null : String(entityId)}
            onChange={(value) => setEntityId(value === null ? null : Number(value))}
            data={(sources.data ?? []).map((source) => ({
              value: String(source.id),
              label: source.displayName ?? source.code,
            }))}
          />
        }
      />

      {/* ⛔ Відмова переліку сутностей — окремо: інакше порожній вибір читався
          б як «джерел не налаштовано» (`ФВ-14.22`). */}
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

      {entity === undefined ? (
        <Text c="dimmed">{t('pipeline.pickHint')}</Text>
      ) : (
        <Suspense fallback={null}>
          <EntityPipeline
            key={entity.id}
            entity={entity}
            allowed={mayManage}
            sourcesHref={routes.adminSources.path}
          />
        </Suspense>
      )}
    </>
  );
}

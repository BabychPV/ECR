import type { JSX } from 'react';
import { Select, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { SourceEntityStatus } from '@/api/types';
import { EntityPipeline } from '@/features/pipeline/EntityPipeline';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { useUrlNumber } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';

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

  const sources = useQuery({
    queryKey: ['sources'],
    queryFn: () => apiFetch<SourceEntityStatus[]>('/api/v1/sources'),
  });

  const entity = (sources.data ?? []).find((source) => source.id === entityId);

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
        <EntityPipeline
          key={entity.id}
          entity={entity}
          allowed={can(session.data, 'Integration.Manage')}
        />
      )}
    </>
  );
}

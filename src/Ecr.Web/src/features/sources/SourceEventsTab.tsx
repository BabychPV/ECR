import { lazy, Suspense, useMemo, useState, type JSX } from 'react';
import { Loader, Select, Stack, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { SourceEntityStatus } from '@/api/types';
import { t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { problemText } from '@/shared/ui/problemText';
import type { DataSource } from '@/features/integration/dataSourceApi';
import { SourceEntitiesQueryKey } from '@/features/integration/sourceEntityApi';
import { fetchEventTemplates, fetchSourceEventMaps, type SourceEventMap } from './sourceEventsApi';
import { fetchDocuments, isEventsNotConfigured, SourceEventsKeys } from './sourceEventsData';
import { SourceEventMapsPanel } from './SourceEventMapsPanel';
import { SourceEventsTable } from './SourceEventsTable';

/**
 * Форма мапінгу — за `import()`: сітка «колонка ↔ атрибут», редактор відповідностей і проба потрібні лише тому,
 * хто натиснув «Створити» чи «Змінити», а не кожному, хто відкрив вкладку.
 */
const SourceEventMapModal = lazy(() =>
  import('./SourceEventMapModal').then((module) => ({ default: module.SourceEventMapModal })),
);

/**
 * Розділ «Прив'язки PI за вікном рядка» (HSE301 A1) — за `import()`: форма з сіткою колонок і перелік джерел
 * потрібні лише тому, хто відкрив вкладку; сторінка джерел, а тим паче документа, їх не обчислюють.
 */
const RowWindowMapsPanel = lazy(() =>
  import('./RowWindowMapsPanel').then((module) => ({ default: module.RowWindowMapsPanel })),
);

type Editing ={ readonly mode: 'create' } | { readonly mode: 'edit'; readonly map: SourceEventMap };

/**
 * Вкладка «Події з PI» шухляди з'єднання (HSE301 A6, FEATURE-HSE301-VIEW §10.6): сутність-шаблон подій цього
 * з'єднання → її мапінги подій і таблиця подій із PI.
 *
 * ⚠ Сутність = шаблон подій (`SourceEntity.Code` — ім'я шаблону з каталогу). Перелік сутностей — той самий запит
 * `['sources']`, що вкладка Entities.
 *
 * ⛔ `canManage = false` — без створення, зміни, паузи, видалення й «Отримати з PI зараз»: кнопок НЕМАЄ, а не
 * вимкнені. Сама вкладка в шухляді зараз показується лише з `Integration.Manage`, бо перелік сутностей
 * (`GET /api/v1/sources`) вимагає саме його.
 */
export function SourceEventsTab({
  source,
  canManage,
}: {
  readonly source: DataSource;
  readonly canManage: boolean;
}): JSX.Element {
  const [chosen, setChosen] = useState<number | null>(null);
  const [editing, setEditing] = useState<Editing | null>(null);

  const entities = useQuery({
    queryKey: SourceEntitiesQueryKey,
    queryFn: () => apiFetch<SourceEntityStatus[]>('/api/v1/sources'),
  });

  const own = useMemo(
    () => (entities.data ?? []).filter((entity) => entity.dataSourceId === source.id),
    [entities.data, source.id],
  );

  const entity = own.find((item) => item.id === chosen) ?? own[0];

  const templates = useQuery({
    queryKey: SourceEventsKeys.templates(source.id),
    queryFn: () => fetchEventTemplates(source.id),
    retry: false,
  });

  const maps = useQuery({
    queryKey: SourceEventsKeys.maps(entity?.id ?? 0),
    queryFn: () => fetchSourceEventMaps(entity?.id),
    enabled: entity !== undefined,
  });

  const documents = useQuery({ queryKey: SourceEventsKeys.documents, queryFn: fetchDocuments });

  if (entities.isError) {
    return <ErrorAlert error={entities.error} onRetry={() => void entities.refetch()} />;
  }

  if (entities.isPending) return <Loader size="sm" />;

  if (entity === undefined) {
    return (
      <Text size="sm" c="dimmed" data-source-events-no-entities="">
        {t('sourceEvents.noEntities')}
      </Text>
    );
  }

  const notConfigured = templates.isError && isEventsNotConfigured(templates.error);

  return (
    <Stack gap="sm" data-source-events-tab={source.code}>
      <Select
        label={t('sourceEvents.entity')}
        description={t('sourceEvents.entityHint')}
        data={own.map((item) => ({ value: String(item.id), label: item.displayName ?? item.code }))}
        value={String(entity.id)}
        allowDeselect={false}
        onChange={(value) => setChosen(value === null ? null : Number(value))}
        data-source-events-entity=""
      />

      {/* ⛔ «Не налаштовано» — інформаційний стан із текстом сервера (назва ключа налаштування), а не помилка
          і не порожній перелік; таблиця подій, уже синхронізованих раніше, лишається видимою. */}
      {notConfigured && (
        <Banner
          tone="info"
          title={t('sourceEvents.notConfiguredTitle')}
          text={problemText(templates.error).detail ?? t('sourceEvents.notConfiguredText')}
          testId="source-events-not-configured"
        />
      )}
      {templates.isError && !notConfigured && (
        <ErrorAlert error={templates.error} onRetry={() => void templates.refetch()} />
      )}

      <SourceEventMapsPanel
        maps={maps}
        documents={documents.data ?? []}
        canManage={canManage}
        onCreate={() => setEditing({ mode: 'create' })}
        onEdit={(map) => setEditing({ mode: 'edit', map })}
        sourceEntityId={entity.id}
      />

      <SourceEventsTable
        sourceEntityId={entity.id}
        maps={maps.data ?? []}
        documents={documents.data ?? []}
        canManage={canManage}
        onCreateMap={() => setEditing({ mode: 'create' })}
      />

      <Suspense fallback={<Loader size="sm" />}>
        <RowWindowMapsPanel
          sourceEntityId={entity.id}
          entities={own}
          documents={documents.data ?? []}
          canManage={canManage}
        />
      </Suspense>

      {canManage && editing !== null && (
        <Suspense fallback={<Loader size="sm" />}>
          <SourceEventMapModal
            key={editing.mode === 'edit' ? `edit-${editing.map.id}` : 'create'}
            dataSourceId={source.id}
            sourceEntityId={entity.id}
            template={entity.code}
            map={editing.mode === 'edit' ? editing.map : null}
            documents={documents.data ?? []}
            onClose={() => setEditing(null)}
          />
        </Suspense>
      )}
    </Stack>
  );
}

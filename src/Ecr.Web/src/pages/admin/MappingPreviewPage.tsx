import { useMemo, type JSX } from 'react';
import { Alert, Group, Select, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { MappingPreview, SourceEntityStatus } from '@/api/types';
import { WindowDays, fetchMappingPreview, windowFrom } from '@/features/mapping/api';
import { MappingGaps } from '@/features/mapping/MappingGaps';
import { MappingRows } from '@/features/mapping/MappingRows';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { useUrlNumber } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';

/**
 * Попередній перегляд мапінгу на реальних рядках джерела (`ФВ-13.14`).
 *
 * ⛔ До цього екрана мапінг перевірявся тим, що збір або відпрацював, або ні.
 * Мапінг без перегляду — це нічна задача, яка одного ранку принесе не ті
 * числа, і дізнаються про це зі звіту.
 *
 * ⚠ «Реальні рядки» — це вже зібране (`ext.RawDataPoint`), а не читання з
 * джерела наживо. Перегляд, який ходить у чужу систему, показував би помилку
 * мережі рівно тоді, коли на нього дивляться.
 *
 * ⚠ Обрана сутність живе **в адресі** (`ФВ-14.29`): посилання на конкретний
 * розрив можна надіслати тому, хто його виправлятиме.
 */
export function MappingPreviewPage(): JSX.Element {
  const [entityId, setEntityId] = useUrlNumber('entity');

  const sources = useQuery({
    queryKey: ['sources'],
    queryFn: () => apiFetch<SourceEntityStatus[]>('/api/v1/sources'),
  });

  // ⚠ Вікно рахується ОДИН раз на монтування. Інакше кожен рендер зсував би
  // межі на мілісекунди, `queryKey` змінювався б і сторінка перезапитувала б
  // сервер нескінченно.
  const window = useMemo(() => windowFrom(new Date()), []);

  const preview = useQuery({
    queryKey: ['mapping-preview', entityId, window.fromUtc],
    queryFn: () => fetchMappingPreview(entityId ?? 0, window),
    enabled: entityId !== null,
  });

  return (
    <>
      <PageHeader
        title={t('mapping.title')}
        actions={
          <Group gap="xs" align="end">
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
          </Group>
        }
      />

      {/*
       * ⛔ Помилка переліку сутностей показується ОКРЕМО від помилки перегляду:
       * недоступний перелік лишає порожнім сам вибір, і мовчазна порожнеча в
       * ньому читалася б як «джерел не налаштовано» (`ФВ-14.22`).
       */}
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
        <Text c="dimmed">{t('mapping.pickHint')}</Text>
      ) : (
        <AsyncBoundary<MappingPreview>
          isPending={preview.isPending}
          error={preview.error}
          data={preview.data}
          skeleton="table"
          onRetry={() => void preview.refetch()}
        >
          {(data) => (
            <>
              <Text size="sm" c="dimmed" mb="xs">
                {t('mapping.window', { days: WindowDays, points: data.pointsSeen })}
              </Text>

              {/* ⛔ Урізана серія дає правильне НА ВИГЛЯД число: `Sum` просто
                  менша, `Last` просто інша. Мовчати про це означало б, що
                  перегляд бреше рівно там, де на нього дивляться. */}
              {data.isTruncated && (
                <Alert color="statusWarning" mb="md" title={t('mapping.truncated')}>
                  {t('mapping.truncatedHint')}
                </Alert>
              )}

              <MappingGaps preview={data} />
              <MappingRows preview={data} />
            </>
          )}
        </AsyncBoundary>
      )}
    </>
  );
}

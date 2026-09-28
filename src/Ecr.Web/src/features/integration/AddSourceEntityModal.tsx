import { useState, type JSX } from 'react';
import { Anchor, Badge, Button, Group, Loader, Modal, Stack, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQueries, useQueryClient } from '@tanstack/react-query';
import {
  catalogLabel,
  fetchSourceCatalog,
  isAttribute,
  type SourceCatalogItem,
} from '@/features/mapping/piafCatalogApi';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { DataSourcesQueryKey } from './dataSourcesKey';
import { createSourceEntity, SourceEntitiesQueryKey } from './sourceEntityApi';

/**
 * Форма «Додати сутність збору» (`ФВ-13.11`, `ФВ-13.13`).
 *
 * ⛔ Імені не вводять руками: сутність обирається з каталогу джерела. Код,
 * вписаний руками, відрізняється від справжнього одним символом рівно тоді,
 * коли це найважче помітити, — і збір за ним «успішно» віддає нуль точок.
 *
 * ⚠ Не `PiAfCatalogPicker` із мапінгу: той завершує вибір лише АТРИБУТОМ (поле
 * мапінгу), а сутність збору — це найчастіше ЕЛЕМЕНТ AF. Тут обрати можна будь-
 * яку позицію, а в елемент — ще й зайти. Кеш сторінок каталогу спільний із
 * тим переглядачем (той самий ключ і та сама форма даних).
 */
export function AddSourceEntityModal({
  dataSourceId,
  sourceName,
  opened,
  onClose,
}: {
  readonly dataSourceId: number;
  readonly sourceName: string;
  readonly opened: boolean;
  readonly onClose: () => void;
}): JSX.Element {
  const queryClient = useQueryClient();

  /** Шлях поточного рівня; порожній стек — корінь каталогу. */
  const [trail, setTrail] = useState<readonly string[]>([]);
  const [chosen, setChosen] = useState<SourceCatalogItem | null>(null);

  const path = trail.length === 0 ? null : (trail[trail.length - 1] ?? null);

  const create = useMutation({
    mutationFn: (item: SourceCatalogItem) =>
      createSourceEntity({
        dataSourceId,
        code: item.code,
        displayName: item.displayName,
        entityPath: item.path,
        sourceKind: null,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: SourceEntitiesQueryKey });
      void queryClient.invalidateQueries({ queryKey: DataSourcesQueryKey });
      notifications.show({ message: t('sources.entityCreated') });
      setChosen(null);
      onClose();
    },
  });

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={t('sources.addEntityTitle', { name: sourceName })}
      size="lg"
    >
      <Stack gap="sm" data-add-entity={dataSourceId}>
        <Text size="sm" c="dimmed">
          {t('sources.addEntityHint')}
        </Text>

        <Group gap="xs">
          <Button
            size="compact-xs"
            variant="default"
            disabled={trail.length === 0}
            onClick={() => setTrail((prev) => prev.slice(0, -1))}
          >
            {t('sources.catalogUp')}
          </Button>
          <Text size="xs" ff="monospace" data-catalog-path="">
            {path ?? t('sources.catalogRoot')}
          </Text>
        </Group>

        {/* ⚠ `key` — рівень: сторінки попереднього рівня тут не значать нічого. */}
        {opened && (
          <CatalogLevel
            key={path ?? ''}
            dataSourceId={dataSourceId}
            path={path}
            onOpen={(next) => setTrail((prev) => [...prev, next])}
            onChoose={setChosen}
          />
        )}

        {chosen !== null && (
          <Text size="sm" data-entity-chosen={chosen.code}>
            {t('sources.entityChosen', { code: chosen.path ?? chosen.code })}
          </Text>
        )}

        {create.error !== null && <ErrorAlert error={create.error} />}

        <Group justify="flex-end" gap="xs">
          <Button variant="default" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button
            disabled={chosen === null}
            loading={create.isPending}
            onClick={() => {
              if (chosen !== null) create.mutate(chosen);
            }}
            data-add-entity-save=""
          >
            {t('sources.addEntity')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/**
 * Один рівень каталогу: сторінки, дочитані курсором.
 *
 * ⚠ Сторінки — список курсорів у стані, позиції — похідні від `useQueries`
 * (той самий прийом, що в `PiAfCatalogPicker`): друга копія кешу в стані
 * розійшлася б із ним при першому ж перечитуванні.
 */
function CatalogLevel({
  dataSourceId,
  path,
  onOpen,
  onChoose,
}: {
  readonly dataSourceId: number;
  readonly path: string | null;
  readonly onOpen: (path: string) => void;
  readonly onChoose: (item: SourceCatalogItem) => void;
}): JSX.Element {
  const [cursors, setCursors] = useState<readonly (string | null)[]>([null]);

  const pages = useQueries({
    queries: cursors.map((cursor) => ({
      queryKey: ['piaf-catalog', dataSourceId, path ?? '', '', cursor] as const,
      queryFn: () => fetchSourceCatalog(dataSourceId, { path, search: '', cursor }),
    })),
  });

  const failed = pages.find((page) => page.isError);
  if (failed !== undefined) {
    return <ErrorAlert error={failed.error} onRetry={() => void failed.refetch()} />;
  }

  const items = pages.flatMap((page) => page.data?.items ?? []);
  const last = pages[pages.length - 1];
  const next = last?.data?.nextCursor ?? null;

  if (pages[0]?.isPending === true) return <Loader size="sm" />;

  if (items.length === 0) {
    return (
      <Text size="sm" c="dimmed" data-catalog-empty="">
        {t('sources.catalogEmpty')}
      </Text>
    );
  }

  return (
    <Stack gap="xs" data-catalog-level={path ?? ''}>
      {items.map((item) => (
        <Group key={`${item.path ?? ''}|${item.code}`} gap="xs" wrap="nowrap" data-catalog-item={item.code}>
          <Text size="sm">{catalogLabel(item)}</Text>
          <Badge variant="light" color="gray">
            {isAttribute(item) ? t('mapping.catalogAttribute') : t('mapping.catalogElement')}
          </Badge>

          {!isAttribute(item) && item.path !== null && item.path.length > 0 && (
            <Anchor component="button" type="button" size="sm" onClick={() => onOpen(item.path ?? '')}>
              {t('sources.catalogOpenLevel')}
            </Anchor>
          )}

          <Anchor component="button" type="button" size="sm" onClick={() => onChoose(item)}>
            {t('sources.catalogChoose')}
          </Anchor>
        </Group>
      ))}

      {next !== null && (
        <Button
          size="compact-xs"
          variant="subtle"
          loading={last?.isFetching === true}
          onClick={() => setCursors((prev) => [...prev, next])}
        >
          {t('sources.catalogMore')}
        </Button>
      )}
    </Stack>
  );
}

import { useState, type JSX } from 'react';
import { Badge, Button, Group, Loader, Stack, Table, Text, Title } from '@mantine/core';
import { useMutation, useQueryClient, type UseQueryResult } from '@tanstack/react-query';
import type { DocumentSummary } from '@/api/types';
import { formatNumber } from '@/shared/format';
import { useFocusAfterBusy } from '@/shared/a11y/focus';
import { t } from '@/shared/i18n';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showDone } from '@/shared/ui/notify';
import { toggleActiveRequest, type VolumeMode } from './sourceEventMapForm';
import { deleteSourceEventMap, updateSourceEventMap, type SourceEventMap } from './sourceEventsApi';
import { SourceEventsKeys } from './sourceEventsData';

/** Назва режиму об'єму — літералами, щоб сторож каталогу бачив кожен ключ. */
export function volumeModeLabel(mode: VolumeMode): string {
  switch (mode) {
    case 'None':
      return t('sourceEvents.volumeNone');
    case 'EventAttribute':
      return t('sourceEvents.volumeEventAttribute');
    case 'RowWindow':
      return t('sourceEvents.volumeRowWindow');
  }
}

/** Бізнес-ключ документа мапінгу; документа немає серед видимих — його номер. */
export function documentLabel(documents: readonly DocumentSummary[], documentId: number): string {
  return documents.find((document) => document.id === documentId)?.businessKey ?? `#${documentId}`;
}

/**
 * Мапінги подій сутності: документ, режим об'єму, кількість полів, стан; для `Integration.Manage` — створення,
 * зміна, пауза/відновлення і видалення.
 *
 * ⛔ Видалення мапінгу, за яким уже синхронізовано події, сервер відхиляє (`409 eventMapHasLinks`): причина
 * показується тут текстом сервера, а вихід — пауза, кнопка якої стоїть поруч.
 */
export function SourceEventMapsPanel({
  maps,
  documents,
  canManage,
  sourceEntityId,
  onCreate,
  onEdit,
}: {
  readonly maps: UseQueryResult<SourceEventMap[]>;
  readonly documents: readonly DocumentSummary[];
  readonly canManage: boolean;
  readonly sourceEntityId: number;
  readonly onCreate: () => void;
  readonly onEdit: (map: SourceEventMap) => void;
}): JSX.Element {
  const queryClient = useQueryClient();
  const [deleting, setDeleting] = useState<SourceEventMap | null>(null);

  const refresh = (): void => {
    void queryClient.invalidateQueries({ queryKey: SourceEventsKeys.maps(sourceEntityId) });
    void queryClient.invalidateQueries({ queryKey: SourceEventsKeys.eventsOf(sourceEntityId) });
  };

  const toggle = useMutation({
    mutationFn: (map: SourceEventMap) => updateSourceEventMap(map.id, toggleActiveRequest(map)),
    onSuccess: (map) => {
      refresh();
      showDone(map.isActive ? t('sourceEvents.mapResumed') : t('sourceEvents.mapPaused'));
    },
  });

  const remove = useMutation({
    mutationFn: (map: SourceEventMap) => deleteSourceEventMap(map.id),
    onSuccess: () => {
      setDeleting(null);
      refresh();
      showDone(t('sourceEvents.mapDeleted'));
    },
    onError: () => setDeleting(null),
  });

  // ⚠ «Пауза»/«Відновити» на час запиту `loading` (= `disabled`) — фокус повертається саме на натиснуту кнопку.
  const toggleFocus = useFocusAfterBusy(toggle.isPending);

  return (
    <Stack gap="xs" data-source-event-maps="">
      <Group justify="space-between">
        <Title order={4}>{t('sourceEvents.mapsTitle')}</Title>
        {canManage && (
          <Button size="xs" onClick={onCreate} data-source-event-map-create="">
            {t('sourceEvents.mapCreate')}
          </Button>
        )}
      </Group>

      {maps.isError && <ErrorAlert error={maps.error} onRetry={() => void maps.refetch()} />}
      {toggle.error !== null && <ErrorAlert error={toggle.error} />}
      {remove.error !== null && <ErrorAlert error={remove.error} />}

      {maps.isPending && maps.fetchStatus !== 'idle' && <Loader size="sm" />}

      {maps.isSuccess && maps.data.length === 0 && (
        <Text size="sm" c="dimmed" data-source-event-maps-empty="">
          {t('sourceEvents.mapsEmpty')}
        </Text>
      )}

      {maps.isSuccess && maps.data.length > 0 && (
        <Table data-source-event-map-list="" aria-label={t('sourceEvents.mapsTitle')}>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('sourceEvents.document')}</Table.Th>
              <Table.Th>{t('sourceEvents.volumeMode')}</Table.Th>
              <Table.Th>{t('sourceEvents.fieldCount')}</Table.Th>
              <Table.Th>{t('sourceEvents.mapState')}</Table.Th>
              {canManage && <Table.Th>{t('sourceEvents.actions')}</Table.Th>}
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {maps.data.map((map) => {
              // ⚠ Кнопки рядка однакові в кожному рядку — доступне ім'я несе документ мапінгу (видимий текст
              // лишається початком імені, WCAG 2.5.3).
              const documentName = documentLabel(documents, map.documentId);

              return (
                <Table.Tr key={map.id} data-source-event-map={map.id}>
                  <Table.Td>{documentName}</Table.Td>
                  <Table.Td>{volumeModeLabel(map.volumeMode)}</Table.Td>
                  <Table.Td>{formatNumber(map.fields.length)}</Table.Td>
                  <Table.Td>
                    <Badge variant="light" color={map.isActive ? 'statusSuccess' : 'gray'}>
                      {map.isActive ? t('sourceEvents.mapActive') : t('sourceEvents.mapPausedState')}
                    </Badge>
                  </Table.Td>
                  {canManage && (
                    <Table.Td>
                      <Group gap="xs" wrap="nowrap">
                        <Button
                          size="xs"
                          variant="default"
                          aria-label={`${t('sourceEvents.mapEdit')}: ${documentName}`}
                          onClick={() => onEdit(map)}
                          data-source-event-map-edit={map.id}
                        >
                          {t('sourceEvents.mapEdit')}
                        </Button>
                        <Button
                          size="xs"
                          variant="default"
                          loading={toggle.isPending && toggle.variables.id === map.id}
                          aria-label={`${map.isActive ? t('sourceEvents.mapPause') : t('sourceEvents.mapResume')}: ${documentName}`}
                          onClick={(event) => {
                            toggleFocus.arm(event.currentTarget);
                            toggle.mutate(map);
                          }}
                          data-source-event-map-toggle={map.id}
                        >
                          {map.isActive ? t('sourceEvents.mapPause') : t('sourceEvents.mapResume')}
                        </Button>
                        <Button
                          size="xs"
                          variant="subtle"
                          color="statusError"
                          aria-label={`${t('sourceEvents.mapDelete')}: ${documentName}`}
                          onClick={() => setDeleting(map)}
                          data-source-event-map-delete={map.id}
                        >
                          {t('sourceEvents.mapDelete')}
                        </Button>
                      </Group>
                    </Table.Td>
                  )}
                </Table.Tr>
              );
            })}
          </Table.Tbody>
        </Table>
      )}

      {canManage && (
        <ConfirmModal
          opened={deleting !== null}
          title={t('sourceEvents.mapDeleteTitle', {
            document: deleting === null ? '' : documentLabel(documents, deleting.documentId),
          })}
          text={t('sourceEvents.mapDeleteText')}
          verb={t('sourceEvents.mapDelete')}
          danger
          isPending={remove.isPending}
          onConfirm={() => {
            if (deleting !== null) remove.mutate(deleting);
          }}
          onClose={() => setDeleting(null)}
        />
      )}
    </Stack>
  );
}

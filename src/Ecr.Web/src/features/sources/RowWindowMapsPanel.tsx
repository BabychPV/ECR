import { useState, type JSX } from 'react';
import { Badge, Button, Group, Loader, Stack, Table, Text, Title } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { DocumentSummary, SourceEntityStatus } from '@/api/types';
import { formatNumber } from '@/shared/format';
import { t } from '@/shared/i18n';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showDone } from '@/shared/ui/notify';
import { deleteRowWindowMap, fetchRowWindowMaps, updateRowWindowMap, type RowWindowMap } from './rowWindowApi';
import { toggleActiveRequest } from './rowWindowForm';
import { RowWindowMapModal, summaryLabel } from './RowWindowMapModal';
import { SourceEventsKeys } from './sourceEventsData';

type Editing = { readonly mode: 'create' } | { readonly mode: 'edit'; readonly map: RowWindowMap };

/**
 * Прив'язки PI за вікном рядка сутності джерела (HSE301 A1, FEATURE-HSE301-VIEW §4.4): колонка-ціль, вікно,
 * селектор, згортка, кількість джерел і стан; для `Integration.Manage` — створення, зміна, пауза/відновлення й
 * видалення.
 *
 * ⚠ Розділ вкладки «Події з PI» і вантажиться ліниво (`import()` у `SourceEventsTab`): сторінка джерел і тим
 * паче сторінка документа його не обчислюють, доки вкладку не відкрито.
 *
 * ⛔ `canManage = false` — кнопок НЕМАЄ, а не вимкнені. Видалення прив'язки з уже підтягнутими значеннями сервер
 * відхиляє (`409 rowWindowMapHasValues`): причина показується текстом сервера, а вихід — пауза поруч.
 */
export function RowWindowMapsPanel({
  sourceEntityId,
  entities,
  documents,
  canManage,
}: {
  readonly sourceEntityId: number;
  /** Сутності цього з'єднання — адресати джерел у формі. */
  readonly entities: readonly SourceEntityStatus[];
  readonly documents: readonly DocumentSummary[];
  readonly canManage: boolean;
}): JSX.Element {
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState<Editing | null>(null);
  const [deleting, setDeleting] = useState<RowWindowMap | null>(null);

  const maps = useQuery({
    queryKey: SourceEventsKeys.rowWindowMaps(sourceEntityId),
    queryFn: () => fetchRowWindowMaps(sourceEntityId),
  });

  const refresh = (): void => {
    void queryClient.invalidateQueries({ queryKey: SourceEventsKeys.rowWindowMapsAll });
  };

  const toggle = useMutation({
    mutationFn: (map: RowWindowMap) => updateRowWindowMap(map.id, toggleActiveRequest(map)),
    onSuccess: (map) => {
      refresh();
      showDone(map.isActive ? t('rowWindow.resumed') : t('rowWindow.paused'));
    },
  });

  const remove = useMutation({
    mutationFn: (map: RowWindowMap) => deleteRowWindowMap(map.id),
    onSuccess: () => {
      setDeleting(null);
      refresh();
      showDone(t('rowWindow.deleted'));
    },
    onError: () => setDeleting(null),
  });

  return (
    <Stack gap="xs" data-row-window-maps="">
      <Group justify="space-between" align="start">
        <Stack gap="xs">
          <Title order={4}>{t('rowWindow.title')}</Title>
          <Text size="xs" c="dimmed">
            {t('rowWindow.hint')}
          </Text>
        </Stack>
        {canManage && (
          <Button size="xs" onClick={() => setEditing({ mode: 'create' })} data-row-window-create="">
            {t('rowWindow.create')}
          </Button>
        )}
      </Group>

      {maps.isError && <ErrorAlert error={maps.error} onRetry={() => void maps.refetch()} />}
      {toggle.error !== null && <ErrorAlert error={toggle.error} />}
      {remove.error !== null && <ErrorAlert error={remove.error} />}
      {maps.isPending && <Loader size="sm" />}

      {maps.isSuccess && maps.data.length === 0 && (
        <Text size="sm" c="dimmed" data-row-window-empty="">
          {t('rowWindow.empty')}
        </Text>
      )}

      {maps.isSuccess && maps.data.length > 0 && (
        <Table.ScrollContainer minWidth={720}>
          <Table data-row-window-list="">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('rowWindow.colTarget')}</Table.Th>
                <Table.Th>{t('rowWindow.colWindow')}</Table.Th>
                <Table.Th>{t('rowWindow.colSelector')}</Table.Th>
                <Table.Th>{t('rowWindow.colSummary')}</Table.Th>
                <Table.Th>{t('rowWindow.colSources')}</Table.Th>
                <Table.Th>{t('rowWindow.colState')}</Table.Th>
                {canManage && <Table.Th>{t('rowWindow.colActions')}</Table.Th>}
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {maps.data.map((map) => (
                <Table.Tr key={map.id} data-row-window-map={map.id}>
                  <Table.Td>{map.targetColumnCode}</Table.Td>
                  <Table.Td>{`${map.startColumnCode} → ${map.endColumnCode}`}</Table.Td>
                  <Table.Td>{map.selectorColumnCode ?? t('rowWindow.noSelector')}</Table.Td>
                  <Table.Td>{summaryLabel(map.summary)}</Table.Td>
                  <Table.Td>{formatNumber(map.sources.length)}</Table.Td>
                  <Table.Td>
                    <Badge variant="light" color={map.isActive ? 'statusSuccess' : 'gray'}>
                      {map.isActive ? t('rowWindow.stateActive') : t('rowWindow.statePaused')}
                    </Badge>
                  </Table.Td>
                  {canManage && (
                    <Table.Td>
                      <Group gap="xs" wrap="nowrap">
                        <Button size="xs" variant="default" onClick={() => setEditing({ mode: 'edit', map })} data-row-window-edit={map.id}>
                          {t('rowWindow.edit')}
                        </Button>
                        <Button
                          size="xs"
                          variant="default"
                          loading={toggle.isPending && toggle.variables.id === map.id}
                          onClick={() => toggle.mutate(map)}
                          data-row-window-toggle={map.id}
                        >
                          {map.isActive ? t('rowWindow.pause') : t('rowWindow.resume')}
                        </Button>
                        <Button size="xs" variant="subtle" color="statusError" onClick={() => setDeleting(map)} data-row-window-delete={map.id}>
                          {t('rowWindow.delete')}
                        </Button>
                      </Group>
                    </Table.Td>
                  )}
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}

      {canManage && (
        <ConfirmModal
          opened={deleting !== null}
          title={t('rowWindow.deleteTitle', { target: deleting?.targetColumnCode ?? '' })}
          text={t('rowWindow.deleteText')}
          verb={t('rowWindow.delete')}
          danger
          isPending={remove.isPending}
          onConfirm={() => {
            if (deleting !== null) remove.mutate(deleting);
          }}
          onClose={() => setDeleting(null)}
        />
      )}

      {canManage && editing !== null && (
        <RowWindowMapModal
          key={editing.mode === 'edit' ? `edit-${editing.map.id}` : 'create'}
          map={editing.mode === 'edit' ? editing.map : null}
          entities={entities}
          defaultEntityId={sourceEntityId}
          documents={documents}
          onClose={() => setEditing(null)}
        />
      )}
    </Stack>
  );
}

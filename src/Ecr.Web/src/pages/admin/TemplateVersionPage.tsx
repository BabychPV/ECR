import type { JSX } from 'react';
import { Accordion, Badge, Button, Group, Loader, Table, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useParams } from 'react-router-dom';
import { EcrApiError, apiFetch } from '@/api/client';
import type { TemplateStructureDto } from '@/api/types';
import { localized } from '@/shared/i18n/localized';
import { can, useSession } from '@/shared/session/useSession';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PageHeader } from '@/shared/ui/PageHeader';
import { t } from '@/shared/i18n';

/**
 * Редактор структури версії.
 *
 * ⛔ Опублікована версія структурно незмінна — це тримає тригер у базі, а не
 * лише інтерфейс. Кнопка публікації ховається не «щоб не заплутати», а тому
 * що сервер однаково відхилить: показана й непрацездатна кнопка гірша за
 * відсутню.
 */
export function TemplateVersionPage(): JSX.Element {
  const { versionId } = useParams();
  const id = Number(versionId);
  const queryClient = useQueryClient();
  const session = useSession();

  const structure = useQuery({
    queryKey: ['template-version', id],
    queryFn: () => apiFetch<TemplateStructureDto>(`/api/v1/template-versions/${id}/structure`),
  });

  const publish = useMutation({
    mutationFn: () => apiFetch(`/api/v1/template-versions/${id}/publish`, { method: 'POST' }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['template-version', id] });
      notifications.show({ color: 'green', message: t('version.published') });
    },
    onError: (error) => {
      // ⚠ Публікація падає з переліком проблем структури: показуємо саме його,
      // а не «не вдалося опублікувати».
      notifications.show({
        color: 'red',
        message: error instanceof EcrApiError ? error.message : String(error),
      });
    },
  });

  if (structure.isPending) return <Loader />;
  if (structure.isError || structure.data === undefined) {
    return <ErrorAlert error={structure.error} />;
  }

  const version = structure.data;

  // ⚠ Структура не несе статусу версії: його віддає перелік версій шаблону.
  // Тому кнопка публікації тут показується за правом, а сервер лишається
  // єдиним, хто вирішує, чи можна публікувати саме цю версію.
  const editable = true;

  return (
    <>
      <PageHeader
        title={`${t('version.title')} ${version.templateVersionId}`}
        actions={
          <Group gap="xs">
            <Text size="xs" c="dimmed">
              r{version.presentationRevision}
            </Text>
            {editable && can(session.data, 'Template.Publish') && (
              <Button size="xs" loading={publish.isPending} onClick={() => publish.mutate()}>
                {t('version.publish')}
              </Button>
            )}
          </Group>
        }
      />

      <Accordion multiple>
        {[...version.sheets]
          .sort((a, b) => a.ordinal - b.ordinal)
          .map((sheet) => (
            <Accordion.Item key={sheet.id} value={sheet.code}>
              <Accordion.Control>
                {localized(sheet.nameL10n)}{' '}
                <Text span c="dimmed">
                  ({sheet.code})
                </Text>
              </Accordion.Control>
              <Accordion.Panel>
                {sheet.tables.map((table) => (
                  <div key={table.id}>
                    <Text fw={600} mt="sm">
                      {table.code} · {table.rowMode}
                    </Text>
                    <Table striped withTableBorder mt={4}>
                      <Table.Thead>
                        <Table.Tr>
                          <Table.Th>{t('version.column')}</Table.Th>
                          <Table.Th>{t('version.type')}</Table.Th>
                          <Table.Th>{t('version.unit')}</Table.Th>
                        </Table.Tr>
                      </Table.Thead>
                      <Table.Tbody>
                        {table.columns.map((column) => (
                          <Table.Tr key={column.id}>
                            <Table.Td>
                              {column.header}{' '}
                              <Text span c="dimmed">
                                ({column.code})
                              </Text>
                            </Table.Td>
                            <Table.Td>
                              {column.dataType}
                              {column.isReadOnly && (
                                <Badge ml={4} size="xs" variant="light">
                                  {t('version.readOnly')}
                                </Badge>
                              )}
                            </Table.Td>
                            <Table.Td>{column.unitSymbol ?? '—'}</Table.Td>
                          </Table.Tr>
                        ))}
                      </Table.Tbody>
                    </Table>
                  </div>
                ))}
              </Accordion.Panel>
            </Accordion.Item>
          ))}
      </Accordion>
    </>
  );
}

import { useState, type JSX } from 'react';
import { Badge, Button, Group, Loader, Modal, Table, Text, TextInput, Textarea } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { EcrApiError, apiFetch } from '@/api/client';
import type { MethodologyDto, PublishMethodologyRequest } from '@/api/types';
import { localized } from '@/shared/i18n/localized';
import { can, useSession } from '@/shared/session/useSession';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PageHeader } from '@/shared/ui/PageHeader';
import { t } from '@/shared/i18n';

/**
 * Конфігуратор методологій: версії, публікація, симуляція.
 *
 * ⛔ Публікація потребує **причини**, **дати набуття чинності** і **зеленого
 * тесту** (ФВ-9.12), а автор останньої правки опублікувати не може (D-40).
 * Форма вимагає причини не з ввічливості: без неї журнал змін методології
 * показує «щось змінилося», і через рік ніхто не пояснить, чому число за
 * минулий рік перерахувалося.
 *
 * ⛔ Поле дати з'явилося після `A7-11`. Діалог збирав саму лише причину, а
 * сервер відхиляє публікацію без дати (`ECR-CALC-0422`) ПЕРШОЮ ж перевіркою —
 * тобто кнопка «Опублікувати» не спрацьовувала жодного разу. Дата не має
 * значення за замовчуванням саме тому, що вона визначає, ЯКІ ПЕРІОДИ
 * перерахуються: підставити «сьогодні» мовчки означало б обрати межу
 * перерахунку за користувача.
 */
export function MethodologiesPage(): JSX.Element {
  const queryClient = useQueryClient();
  const session = useSession();
  const [publishing, setPublishing] = useState<{ id: number; versionId: number } | null>(null);
  const [reason, setReason] = useState('');
  const [effectiveFrom, setEffectiveFrom] = useState('');

  const methodologies = useQuery({
    queryKey: ['methodologies'],
    queryFn: () => apiFetch<MethodologyDto[]>('/api/v1/methodologies'),
  });

  const publish = useMutation({
    mutationFn: (target: { id: number; versionId: number; reason: string; from: string }) =>
      apiFetch(`/api/v1/methodologies/${target.id}/versions/${target.versionId}/publish`, {
        method: 'POST',
        body: JSON.stringify({
          changeReason: target.reason,
          // ⚠ `date` дає рівно `YYYY-MM-DD` — форму `DateOnly` сервера. Через
          // `Date` тут проходити не можна: `toISOString()` переводить у UTC і
          // ввечері зсуває дату на добу назад.
          effectiveFrom: target.from,
        } satisfies PublishMethodologyRequest),
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['methodologies'] });
      setPublishing(null);
      setReason('');
      setEffectiveFrom('');
      notifications.show({ color: 'green', message: t('methodologies.published') });
    },
    onError: (error) => {
      notifications.show({
        color: 'red',
        message: error instanceof EcrApiError ? error.message : String(error),
      });
    },
  });

  return (
    <>
      <PageHeader title={t('methodologies.title')} />
      <ErrorAlert error={methodologies.error} />

      {methodologies.isPending ? (
        <Loader />
      ) : (
        <Table striped>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('methodologies.code')}</Table.Th>
              <Table.Th>{t('methodologies.versions')}</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {(methodologies.data ?? []).map((methodology) => (
              <Table.Tr key={methodology.id}>
                <Table.Td>
                  {localized(methodology.nameL10n)}{' '}
                  <Text span c="dimmed">
                    ({methodology.code})
                  </Text>
                </Table.Td>
                <Table.Td>
                  <Group gap={4}>
                    {methodology.versions.map((version) => (
                      <Group key={version.id} gap={4}>
                        <Badge variant={version.status === 'Published' ? 'filled' : 'light'}>
                          {version.versionNumber} · {version.level}
                          {version.effectiveFrom === null ? '' : ` · ${version.effectiveFrom}`}
                        </Badge>

                        {/* ⚠ Режим і рівень трасування видно поруч із версією:
                            саме вони визначають, чи зміняться числа при
                            публікації, і саме їх порівнюють у diff публікації. */}
                        <Badge variant="outline" size="sm">
                          {version.numericMode} · {version.traceLevel}
                        </Badge>

                        {version.status !== 'Published' &&
                          can(session.data, 'Calculation.Publish') && (
                            <Button
                              size="compact-xs"
                              variant="default"
                              onClick={() =>
                                setPublishing({ id: methodology.id, versionId: version.id })
                              }
                            >
                              {t('methodologies.publish')}
                            </Button>
                          )}
                      </Group>
                    ))}
                  </Group>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}

      <Modal
        opened={publishing !== null}
        onClose={() => setPublishing(null)}
        title={t('methodologies.publishTitle')}
      >
        <Textarea
          label={t('methodologies.reason')}
          description={t('methodologies.reasonHint')}
          value={reason}
          onChange={(event) => setReason(event.currentTarget.value)}
          minRows={3}
          autosize
        />

        <TextInput
          mt="sm"
          type="date"
          label={t('methodologies.effectiveFrom')}
          description={t('methodologies.effectiveFromHint')}
          value={effectiveFrom}
          onChange={(event) => setEffectiveFrom(event.currentTarget.value)}
        />

        <Button
          mt="md"
          disabled={reason.trim().length === 0 || effectiveFrom.length === 0}
          loading={publish.isPending}
          onClick={() => {
            if (publishing !== null) {
              publish.mutate({ ...publishing, reason, from: effectiveFrom });
            }
          }}
        >
          {t('methodologies.publish')}
        </Button>
      </Modal>
    </>
  );
}

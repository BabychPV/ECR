import { useState, type JSX } from 'react';
import { Alert, Badge, Button, Group, Modal, NumberInput, Table, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { TemplateDiffDto } from '@/api/types';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { t } from '@/shared/i18n';

/**
 * Порівняння двох версій шаблону (`ФВ-7.3`, `ФВ-7.4`).
 *
 * ⛔ `GET /template-versions/{id}/diff/{otherId}` не мав у клієнті жодного
 * споживача. Тринадцятий сторож цього не бачив за побудовою: він перевіряє
 * лише дії ЗАПИСУ. А відповідь на питання «що саме зміниться, якщо перевести
 * документи на нову версію» існувала і була недосяжна — тобто рішення про
 * міграцію ухвалювали наосліп.
 *
 * ⛔ Головне тут — **клас зміни**, а не перелік. `Breaking` у версії з
 * документами відхиляється (`ECR-SCHM-0409`), `Guarded` вимагає стратегії
 * міграції; побачити це ДО спроби, а не з відмови — і є сенс екрана.
 *
 * ⚠ Кількість зачеплених документів показується поруч: та сама зміна на версії
 * без документів безпечна, а на версії з тисячею — подія.
 */
export function VersionDiff({ templateVersionId }: { templateVersionId: number }): JSX.Element {
  const [opened, setOpened] = useState(false);
  const [otherId, setOtherId] = useState<number | null>(null);

  const diff = useQuery({
    queryKey: queryKeys.templates.versionDiff(templateVersionId, otherId),
    queryFn: () =>
      apiFetch<TemplateDiffDto>(`/api/v1/template-versions/${templateVersionId}/diff/${otherId ?? 0}`),
    enabled: opened && otherId !== null,
  });

  return (
    <>
      <Button size="xs" variant="default" onClick={() => setOpened(true)}>
        {t('version.diff')}
      </Button>

      <Modal opened={opened} onClose={() => setOpened(false)} title={t('version.diff')} size="xl">
        <NumberInput
          label={t('version.diffOther')}
          description={t('version.diffOtherHint')}
          value={otherId ?? ''}
          onChange={(value) => setOtherId(typeof value === 'number' ? value : null)}
          data-autofocus
        />

        {/*
         * ⚠ Доки друга версія не задана, `data` — `undefined`: запиту ще не
         * було, і обгортка каже саме це, а не «розбіжностей немає».
         */}
        <AsyncBoundary<TemplateDiffDto>
          isPending={otherId !== null && diff.isPending}
          error={diff.error}
          data={otherId === null ? undefined : diff.data}
          isEmpty={(result) => result.changes.length === 0}
          emptyTitle={otherId === null ? t('version.diffPick') : t('version.diffSame')}
          emptyHint={otherId === null ? undefined : t('version.diffSameHint')}
          onRetry={() => void diff.refetch()}
        >
          {(result) => (
            <>
              {/* ⛔ Ця смуга — головне, що екран має сказати. Зміна класу
                  `Breaking` на версії з документами не пройде взагалі, і
                  дізнатися про це з відмови публікації означає витратити на
                  правку час, який можна було не витрачати. */}
              {result.affectedDocumentCount > 0 && (
                <Alert mt="md" color="statusWarning" title={t('version.diffAffected')}>
                  {t('version.diffAffectedHint', { count: result.affectedDocumentCount })}
                </Alert>
              )}

              <Table striped withTableBorder mt="md" className="ecr-sticky-head">
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t('version.diffElement')}</Table.Th>
                    <Table.Th>{t('version.diffKind')}</Table.Th>
                    <Table.Th>{t('version.diffClass')}</Table.Th>
                    <Table.Th>{t('import.was')}</Table.Th>
                    <Table.Th>{t('import.becomes')}</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {result.changes.map((change) => (
                    <Table.Tr key={`${change.elementPath}:${change.kind}`}>
                      <Table.Td>
                        <Text size="xs">{change.elementPath}</Text>
                      </Table.Td>
                      <Table.Td>{change.kind}</Table.Td>
                      <Table.Td>
                        <Badge size="sm" color={classColor(change.changeClass)} variant="light">
                          {change.changeClass}
                        </Badge>
                      </Table.Td>
                      <Table.Td>{change.oldValue ?? '—'}</Table.Td>
                      <Table.Td>{change.newValue ?? '—'}</Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </>
          )}
        </AsyncBoundary>

        <Group justify="flex-end" mt="md">
          <Button variant="default" onClick={() => setOpened(false)}>
            {t('common.cancel')}
          </Button>
        </Group>
      </Modal>
    </>
  );
}

/**
 * Колір класу зміни.
 *
 * ⚠ Не декорація: `Breaking` у версії з документами — відмова операції
 * (`ФВ-7.4`), `Guarded` — потреба в стратегії міграції, `Presentation` —
 * дозволено навіть після публікації. Три різні наслідки мають виглядати
 * по-різному з першого погляду.
 */
function classColor(changeClass: string): string {
  switch (changeClass) {
    case 'Breaking':
      return 'statusError';
    case 'Guarded':
      return 'statusWarning';
    case 'Presentation':
      return 'blue';
    default:
      return 'gray';
  }
}

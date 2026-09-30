import { useMemo, useState, type JSX } from 'react';
import { Alert, Badge, Button, Group, Modal, Select, Table, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { TemplateDiffDto, TemplateVersionPage } from '@/api/types';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { t } from '@/shared/i18n';
import { changeClassLabel, diffKindLabel } from './enumLabels';

type VersionSummary = TemplateVersionPage['items'][number];

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
export function VersionDiff({
  templateVersionId,
  versions,
}: {
  templateVersionId: number;

  /**
   * Версії ТОГО САМОГО шаблону — з переліку, який сторінка вже читає.
   *
   * ⛔ R-09/R-10: друга версія вводилася числом у `NumberInput`: будь-який id
   * (зокрема версії ЧУЖОГО шаблону) ішов у запит, і запит летів на кожну
   * клавішу («1» → «12» → «123»). Вибір зі списку версій свого шаблону знімає
   * обидва: чужої версії в ньому немає, а запит іде один — на вибір.
   */
  versions: readonly VersionSummary[] | undefined;
}): JSX.Element {
  const [opened, setOpened] = useState(false);
  const [otherId, setOtherId] = useState<number | null>(null);

  const options = useMemo(
    () =>
      (versions ?? [])
        .filter((version) => version.id !== templateVersionId)
        .map((version) => ({ value: String(version.id), label: `v${version.version}` })),
    [versions, templateVersionId],
  );

  const numberOf = (versionId: number): string =>
    versions?.find((version) => version.id === versionId)?.version ?? String(versionId);

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
        <Select
          label={t('version.diffOther')}
          description={t('version.diffOtherHint')}
          data={options}
          value={otherId === null ? null : String(otherId)}
          onChange={(value) => setOtherId(value === null ? null : Number(value))}
          nothingFoundMessage={t('version.diffNoOther')}
          searchable
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
              {/* ⛔ R-08: напрям — завжди від старшої версії до новішої (його
                  задає сервер), і його видно: «v1.0 → v2.0». Доти стовпці
                  «Was / Becomes» не казали, котра версія де. */}
              <Text mt="md" fw={600} data-diff-direction>
                {t('version.diffDirection', {
                  from: numberOf(result.fromVersionId),
                  to: numberOf(result.toVersionId),
                })}
              </Text>

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
                      <Table.Td>{diffKindLabel(change.kind)}</Table.Td>
                      <Table.Td>
                        <Badge size="sm" color={classColor(change.changeClass)} variant="light">
                          {changeClassLabel(change.changeClass)}
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
          {/* ⛔ X-18: діалог лише для читання — скасовувати нічого, тож «Close». */}
          <Button variant="default" onClick={() => setOpened(false)}>
            {t('common.close')}
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

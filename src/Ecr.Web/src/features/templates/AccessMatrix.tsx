import { useState, type JSX } from 'react';
import { Alert, Badge, Button, Group, Modal, Table, Text, Tooltip } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { AccessMatrixCellDto, AccessMatrixDto } from '@/api/types';
import { localized } from '@/shared/i18n/localized';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { t } from '@/shared/i18n';

/**
 * Попередній перегляд матриці доступу `період × аркуш` (`ФВ-2.18`).
 *
 * ⛔ Сенс екрана — побачити помилку в правилах **до публікації**. Після неї
 * структура заморожена, і єдиний спосіб виправити правило — клонувати версію
 * і перевести на неї проєкти. Тому матриця показує не «як задумано», а те,
 * що поверне сервер: обидва боки рахує один і той самий обчислювач.
 *
 * ⚠ Два види правил залежать від даних документа, яких у шаблоні ще немає
 * (`SourceWindow`, `Expression`). Такий аркуш позначається прямо — мовчазне
 * «доступно» на місці майбутнього замка гірше за відсутність перегляду.
 */
export function AccessMatrix({ templateVersionId }: { templateVersionId: number }): JSX.Element {
  const [opened, setOpened] = useState(false);

  const matrix = useQuery({
    queryKey: queryKeys.templates.accessMatrix(templateVersionId),
    queryFn: () =>
      apiFetch<AccessMatrixDto>(`/api/v1/template-versions/${templateVersionId}/access-matrix`),
    enabled: opened,
  });

  return (
    <>
      <Button size="xs" variant="default" onClick={() => setOpened(true)}>
        {t('version.accessMatrix')}
      </Button>

      <Modal
        opened={opened}
        onClose={() => setOpened(false)}
        title={t('version.accessMatrix')}
        size="xl"
      >
        <AsyncBoundary<AccessMatrixDto>
          isPending={matrix.isPending}
          error={matrix.error}
          data={matrix.data}
          isEmpty={(result) => result.sheets.length === 0}
          emptyTitle={t('version.empty')}
          emptyHint={t('version.emptyHint')}
          onRetry={() => void matrix.refetch()}
        >
          {(result) => (
            <>
              <Alert mt="md" color="gray" title={t('version.accessMatrixHint')}>
                {t('version.accessMatrixLegend')}
              </Alert>

              <Table striped withTableBorder mt="md" className="ecr-sticky-head">
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t('version.accessMatrixSheet')}</Table.Th>
                    {Array.from({ length: result.periodCount }, (_, index) => (
                      <Table.Th key={index + 1} style={{ textAlign: 'center' }}>
                        {index + 1}
                      </Table.Th>
                    ))}
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {result.sheets.map((sheet) => (
                    <Table.Tr key={sheet.sheetDefId}>
                      <Table.Td>
                        {localized(sheet.nameL10n) || sheet.code}{' '}
                        <Text span c="dimmed" size="xs">
                          ({sheet.code})
                        </Text>
                        {/* ⛔ Позначка стоїть на АРКУШІ, а не на клітинці:
                            правило залежить від даних у кожному періоді
                            однаково, і дванадцять однакових значків читалися б
                            як дванадцять різних застережень. */}
                        {sheet.dependsOnData && (
                          <Tooltip label={t('version.accessMatrixDataHint')} multiline w={280}>
                            <Badge ml="xs" size="xs" variant="outline" color="statusWarning">
                              {t('version.accessMatrixData')}
                            </Badge>
                          </Tooltip>
                        )}
                      </Table.Td>
                      {sheet.cells.map((cell) => (
                        <Table.Td key={cell.periodSequence} style={{ textAlign: 'center' }}>
                          <Cell cell={cell} />
                        </Table.Td>
                      ))}
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
 * Одна клітинка матриці.
 *
 * ⛔ Стан несе **знак**, а не лише колір (`ФВ-14.18`): близько 8 % чоловіків
 * не розрізняють частину кольорів, а матриця з дванадцяти стовпців — саме те
 * місце, де око шукає різницю швидко й помиляється.
 */
function Cell({ cell }: { cell: AccessMatrixCellDto }): JSX.Element {
  const label =
    cell.state === 'Editable'
      ? t('version.accessEditable')
      : cell.state === 'Partial'
        ? t('version.accessPartial')
        : t('version.accessBlocked');

  const sign = cell.state === 'Editable' ? '·' : cell.state === 'Partial' ? '◑' : '✕';

  return (
    <Tooltip label={cell.detail ?? label} multiline w={280}>
      <Text
        span
        aria-label={label}
        c={
          cell.state === 'Editable' ? 'dimmed' : cell.state === 'Partial' ? 'statusWarning' : 'statusError'
        }
        fw={cell.state === 'Editable' ? 400 : 700}
      >
        {sign}
      </Text>
    </Tooltip>
  );
}

import { useState, type JSX } from 'react';
import { Button, Group, NumberInput, Table, Text, TextInput } from '@mantine/core';
import { useStructureChanges, type StructureChangePage } from '@/features/audit/api';
import { FilterHints, readerOnlyDescription } from '@/features/audit/FilterHints';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { Timestamp } from '@/shared/ui/Timestamp';
import { useDebouncedFilter } from '@/shared/ui/useDebouncedFilter';
import { useUrlNumber, useUrlState } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';

/**
 * Журнал структурних змін (`BE-16`) — друга вкладка екрана аудиту.
 *
 * ⛔ Вікно приходить ЗЗОВНІ, від сторінки: це те саме поле дат, що й у журналі
 * комірок. Дві пари дат на одному екрані розійшлися б першого ж дня, і людина
 * порівнювала б «зміни чисел за тиждень» зі «змінами структури за місяць».
 *
 * ⚠ Сторінка ставить панелі `key` з вікна, тож зміна дат скидає курсор
 * перемонтуванням: курсор позначає позицію в КОНКРЕТНІЙ видачі.
 */
export function StructureChangesPanel({ from, to }: { from: string; to: string }): JSX.Element {
  const [entityType, setEntityType] = useUrlState('entityType');
  const [changedBy, setChangedBy] = useUrlNumber('changedBy');
  const [cursor, setCursor] = useState<string | null>(null);

  // ⛔ Набір у полях — у запит після паузи, як у журналі комірок (`AuditPage`).
  const appliedEntityType = useDebouncedFilter(entityType);
  const appliedChangedBy = useDebouncedFilter(changedBy);

  const changes = useStructureChanges({
    from,
    to,
    entityType: appliedEntityType,
    changedByUserId: appliedChangedBy,
    limit: 100,
    cursor,
  });

  return (
    <>
      <Group gap="xs" align="end" mb="xs" wrap="wrap" data-audit-filter-row="structure">
        <TextInput
          size="xs"
          miw={200}
          label={t('audit.entityType')}
          value={entityType ?? ''}
          onChange={(event) => {
            setEntityType(event.currentTarget.value);
            setCursor(null);
          }}
        />
        <NumberInput
          size="xs"
          miw={140}
          label={t('audit.author')}
          description={t('audit.authorHint')}
          styles={readerOnlyDescription}
          value={changedBy ?? ''}
          onChange={(value) => {
            setChangedBy(typeof value === 'number' ? value : null);
            setCursor(null);
          }}
        />
      </Group>

      {/* ⚠ `U-21`: та сама будова ряду, що в журналі комірок, — див. `FilterHints`. */}
      <FilterHints texts={[t('audit.authorHint')]} />

      <AsyncBoundary<StructureChangePage>
        isPending={changes.isPending}
        error={changes.error}
        data={changes.data}
        isEmpty={(page) => page.items.length === 0}
        emptyTitle={t('audit.structureEmpty')}
        emptyHint={t('audit.emptyHint')}
        skeleton="table"
        onRetry={() => void changes.refetch()}
      >
        {(page) => (
          <>
            <Table striped className="ecr-sticky-head">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t('audit.when')}</Table.Th>
                  <Table.Th>{t('audit.who')}</Table.Th>
                  <Table.Th>{t('audit.entity')}</Table.Th>
                  <Table.Th>{t('audit.operation')}</Table.Th>
                  <Table.Th>{t('audit.reason')}</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {page.items.map((change, index) => (
                  <Table.Tr key={`${change.changedAt}:${change.entityType}:${change.entityId}:${index}`}>
                    {/* ⛔ Останнє з сімнадцяти місць, де дата з сервера
                        друкувалася сирим рядком. Той самий аргумент, що й у
                        журналі комірок (`AuditPage`): журнал — доказ, і доказ
                        мусить лишатися однозначним. Він не порушений —
                        точне значення лишається в `dateTime`/`title`,
                        читабельним стає лише те, що бачить око.
                        ⚠ `key` рядка й далі бере СИРИЙ `changedAt`: ключ — для
                        React, а не для ока. */}
                    <Table.Td>
                      <Timestamp value={change.changedAt} />
                    </Table.Td>
                    <Table.Td>{change.changedByUserId}</Table.Td>
                    <Table.Td>
                      <Text size="xs">
                        {change.entityType} · {change.entityId}
                      </Text>
                    </Table.Td>
                    <Table.Td>{change.operation}</Table.Td>
                    <Table.Td>{change.changeReason ?? '—'}</Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>

            {page.nextCursor !== null && (
              <Button mt="md" variant="default" onClick={() => setCursor(page.nextCursor)}>
                {t('documents.more')}
              </Button>
            )}
          </>
        )}
      </AsyncBoundary>
    </>
  );
}

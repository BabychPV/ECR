import type { JSX } from 'react';
import { Button, Group, Select, Table, Text, TextInput } from '@mantine/core';
import { securityEventsQuery, useSecurityEvents, type SecurityEventPage } from '@/features/audit/api';
import { authorName, useAuthorOptions } from '@/features/audit/authorOptions';
import { FilterHints, readerOnlyDescription } from '@/features/audit/FilterHints';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { Timestamp } from '@/shared/ui/Timestamp';
import { useDebouncedFilter, useFilterCursor } from '@/shared/ui/useDebouncedFilter';
import { useFieldDraft } from '@/shared/ui/useFieldDraft';
import { useUrlNumber, useUrlState } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';

/**
 * Журнал подій безпеки (ФВ-5.24, ФВ-6.11) — третя вкладка екрана аудиту: зміни прав і ролей та
 * відмови в доступі (`AccessDenied`). До цього події лежали в `aud.SecurityEvent` без читача.
 *
 * ⚠ Будова — як у `StructureChangesPanel`: вікно приходить від сторінки (одне поле дат на всі
 * вкладки), панель отримує `key` з вікна, тож зміна дат скидає курсор перемонтуванням.
 */
export function SecurityEventsPanel({ from, to }: { from: string; to: string }): JSX.Element {
  const [eventType, setEventType] = useUrlState('eventType');
  const [changedBy, setChangedBy] = useUrlNumber('changedBy');

  const appliedEventType = useDebouncedFilter(eventType);
  const appliedChangedBy = useDebouncedFilter(changedBy);
  const eventTypeField = useFieldDraft(eventType ?? '');

  const applied = { from, to, eventType: appliedEventType, changedByUserId: appliedChangedBy, limit: 100 };
  const [cursor, setCursor] = useFilterCursor(securityEventsQuery(applied));

  const events = useSecurityEvents({ ...applied, cursor });
  const authorOptions = useAuthorOptions(events.data?.items, changedBy);

  return (
    <>
      <Group gap="xs" align="end" mb="xs" wrap="wrap" data-audit-filter-row="security">
        <TextInput
          size="xs"
          miw={200}
          label={t('audit.eventType')}
          value={eventTypeField.value}
          onFocus={eventTypeField.onFocus}
          onBlur={eventTypeField.onBlur}
          onChange={(event) => {
            eventTypeField.setValue(event.currentTarget.value);
            setEventType(event.currentTarget.value);
          }}
        />
        <Select
          size="xs"
          miw={180}
          searchable
          clearable
          label={t('audit.author')}
          description={t('audit.authorHint')}
          styles={readerOnlyDescription}
          placeholder={t('audit.authorAny')}
          data={authorOptions}
          value={changedBy === null ? null : String(changedBy)}
          onChange={(value) => {
            setChangedBy(value === null ? null : Number(value));
          }}
        />
      </Group>

      <FilterHints texts={[t('audit.authorHint')]} />

      <AsyncBoundary<SecurityEventPage>
        isPending={events.isPending}
        error={events.error}
        data={events.data}
        isEmpty={(page) => page.items.length === 0}
        emptyTitle={t('audit.securityEmpty')}
        emptyHint={t('audit.emptyHint')}
        skeleton="table"
        onRetry={() => void events.refetch()}
      >
        {(page) => (
          <>
            <Table striped className="ecr-sticky-head">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t('audit.when')}</Table.Th>
                  <Table.Th>{t('audit.who')}</Table.Th>
                  <Table.Th>{t('audit.eventType')}</Table.Th>
                  <Table.Th>{t('audit.details')}</Table.Th>
                  <Table.Th>{t('audit.correlation')}</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {page.items.map((event, index) => (
                  <Table.Tr key={`${event.changedAt}:${event.eventType}:${index}`}>
                    <Table.Td>
                      <Timestamp value={event.changedAt} />
                    </Table.Td>
                    {/* `R-18`: ім'я з сервера; номер — у підказці. */}
                    <Table.Td title={`#${String(event.changedByUserId)}`}>{authorName(event)}</Table.Td>
                    <Table.Td>{event.eventType}</Table.Td>
                    <Table.Td>
                      <Text size="xs" ff="monospace" style={{ wordBreak: 'break-all' }}>
                        {event.detailsJson ?? '—'}
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      <Text size="xs" ff="monospace">
                        {event.correlationId ?? '—'}
                      </Text>
                    </Table.Td>
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

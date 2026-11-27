import { useMemo, type JSX } from 'react';
import { Select, Table, Text } from '@mantine/core';
import {
  fetchSecurityExport,
  securityEventsQuery,
  useSecurityEventTypes,
  type SecurityEventPage,
} from '@/features/audit/api';
import { useSecurityEventPages } from '@/features/audit/journalPages';
import { InfiniteListFooter } from '@/features/common/infiniteList/InfiniteListFooter';
import { mergePages } from '@/features/common/infiniteList/mergePages';
import { AuditExportButton } from '@/features/audit/AuditExportButton';
import { authorName, useAuthorOptions } from '@/features/audit/authorOptions';
import { useAuditSort } from '@/features/audit/auditSort';
import { SortableTh } from '@/features/audit/SortableTh';
import { FilterHints, FilterRow, readerOnlyDescription } from '@/shared/ui/FilterBar';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { useDebouncedFilter } from '@/shared/ui/useDebouncedFilter';
import { useUrlNumber, useUrlState } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';
import { securityEventLabel } from './auditLabels';
import { auditDetails } from './auditDetails';
import { AuditChangedAt } from './AuditChangedAt';
import './auditTable.css';

/**
 * «Details» події: читабельний текст першим рядком, технічні ідентифікатори (задача, GUID) — другим, приглушеним
 * (✎ P-1124-4); повний JSON — у підказці.
 */
function SecurityDetails({ raw }: { raw: string | null | undefined }): JSX.Element {
  const view = auditDetails(raw);

  return (
    <>
      {/* ✎ debt-1109 x10: читабельні пари замість сирого JSON; точне значення — у підказці. */}
      <Text size="xs" style={{ overflowWrap: 'anywhere' }} title={raw ?? undefined} data-audit-details="">
        {view.text}
      </Text>
      {view.technical.length > 0 && (
        <Text size="xs" ff="monospace" c="var(--ecr-muted)" data-audit-technical="">
          {view.technical.join(' · ')}
        </Text>
      )}
    </>
  );
}

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

  // ✎ P-1124-4: тип події — ВИБОРОМ (дискретне значення, без паузи), автор — теж вибором.
  const appliedChangedBy = useDebouncedFilter(changedBy);
  const [sort, setSort] = useAuditSort();

  const applied = { from, to, eventType, changedByUserId: appliedChangedBy, sort, limit: 100 };
  // ✎ W-3d: курсор — параметр сторінки `useInfiniteQuery`; ключ — застосований фільтр без курсора.
  const listKey = securityEventsQuery(applied);
  const events = useSecurityEventPages(applied);
  const eventPage = useMemo(() => mergePages(events.data?.pages), [events.data]);
  const authorOptions = useAuthorOptions(eventPage?.items, changedBy);
  const eventTypes = useSecurityEventTypes(from, to);
  const eventTypeOptions = useMemo(() => {
    const codes = new Set(Array.isArray(eventTypes.data) ? eventTypes.data : []);
    for (const item of eventPage?.items ?? []) codes.add(item.eventType);
    // Обраний тип лишається в переліку завжди — інакше поле показувало б порожнечу при чинному фільтрі.
    if (eventType !== null && eventType !== '') codes.add(eventType);

    return [...codes]
      .map((value) => ({ value, label: securityEventLabel(value) }))
      .sort((a, b) => a.label.localeCompare(b.label));
  }, [eventTypes.data, eventPage, eventType]);

  return (
    <>
      <FilterRow mb="xs" data-audit-filter-row="security">
        {/* ⛔ Було: вільний текст із точним збігом КОДУ (`AccessDenied`) на сервері — «Access denied» чи «access»
            давали порожній журнал. Тепер — типи, що є у вікні (`/audit/security/event-types`), підписами мовою
            інтерфейсу; значення — код сервера. */}
        <Select
          size="xs"
          miw={220}
          searchable
          clearable
          label={t('audit.eventType')}
          placeholder={t('audit.eventTypeAny')}
          nothingFoundMessage={t('audit.filterNothing')}
          data={eventTypeOptions}
          value={eventType === '' ? null : eventType}
          onChange={(value) => {
            setEventType(value);
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
          nothingFoundMessage={t('audit.filterNothing')}
          data={authorOptions}
          value={changedBy === null ? null : String(changedBy)}
          onChange={(value) => {
            setChangedBy(value === null ? null : Number(value));
          }}
        />
      </FilterRow>

      <FilterHints texts={[t('audit.authorHint')]} />

      <AuditExportButton
        tab="security"
        from={from}
        to={to}
        fallbackName="audit-security.csv"
        load={() => fetchSecurityExport(applied)}
      />

      <AsyncBoundary<SecurityEventPage>
        isPending={events.isPending}
        error={events.isFetchNextPageError ? null : events.error}
        data={eventPage}
        isEmpty={(page) => page.items.length === 0}
        emptyTitle={t('audit.securityEmpty')}
        emptyHint={t('audit.emptyHint')}
        skeleton="table"
        onRetry={() => void events.refetch()}
      >
        {(page) => (
          <>
            <Table striped className="ecr-sticky-head ecr-audit-table">
              <Table.Thead>
                <Table.Tr>
                  <SortableTh label={t('audit.when')} column="time" sort={sort} onSort={setSort} />
                  <SortableTh label={t('audit.who')} column="user" sort={sort} onSort={setSort} />
                  <Table.Th>{t('audit.eventType')}</Table.Th>
                  <Table.Th>{t('audit.details')}</Table.Th>
                  <Table.Th>{t('audit.correlation')}</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {page.items.map((event, index) => (
                  <Table.Tr key={`${event.changedAt}:${event.eventType}:${index}`}>
                    <Table.Td data-audit-time-cell="">
                      {/* ✎ P-1124-4: місцевий час читача; час майданчика (`siteTimeZoneId`) — у підказці. */}
                      <AuditChangedAt value={event.changedAt} zone={event.siteTimeZoneId} />
                    </Table.Td>
                    {/* `R-18`: ім'я з сервера; номер — у підказці. */}
                    <Table.Td title={`#${String(event.changedByUserId)}`} data-audit-author="">
                      {authorName(event)}
                    </Table.Td>
                    <Table.Td title={event.eventType} data-audit-event-type={event.eventType}>
                      {securityEventLabel(event.eventType)}
                    </Table.Td>
                    <Table.Td>
                      <SecurityDetails raw={event.detailsJson} />
                    </Table.Td>
                    <Table.Td>
                      <Text size="xs" ff="monospace" c="var(--ecr-muted)" data-audit-technical="">
                        {event.correlationId ?? '—'}
                      </Text>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>

            {/* ✎ W-3d: сторінки ДОПИСУЮТЬСЯ прокруткою (`InfiniteListFooter`). */}
            <InfiniteListFooter key={listKey} pager={events} shown={page.items.length} total={page.totalCount} />
          </>
        )}
      </AsyncBoundary>
    </>
  );
}

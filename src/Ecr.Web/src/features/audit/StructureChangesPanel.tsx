import { useMemo, type JSX } from 'react';
import { Select, Table, Text } from '@mantine/core';
import { structureChangesQuery, useStructureEntityTypes, type StructureChangePage } from '@/features/audit/api';
import { useStructureChangePages } from '@/features/audit/journalPages';
import { InfiniteListFooter } from '@/features/common/infiniteList/InfiniteListFooter';
import { mergePages } from '@/features/common/infiniteList/mergePages';
import { authorName, useAuthorOptions } from '@/features/audit/authorOptions';
import { useAuditSort } from '@/features/audit/auditSort';
import { SortableTh } from '@/features/audit/SortableTh';
import { FilterHints, FilterRow, readerOnlyDescription } from '@/shared/ui/FilterBar';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { useDebouncedFilter } from '@/shared/ui/useDebouncedFilter';
import { useUrlNumber, useUrlState } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';
import { entityTypeLabel, operationLabel } from './auditLabels';
import { structureChangeReasonText, structureChangeReasonZone } from './structureChangeReason';
import { AuditChangedAt } from './AuditChangedAt';
import './auditTable.css';

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

  // ✎ P-1124-4: тип сутності — ВИБОРОМ (дискретне значення, без паузи); автор — як у журналі комірок.
  const appliedChangedBy = useDebouncedFilter(changedBy);
  const [sort, setSort] = useAuditSort();

  const applied = {
    from,
    to,
    entityType,
    changedByUserId: appliedChangedBy,
    sort,
    limit: 100,
  };
  // ✎ W-3e: курсор — параметр сторінки `useInfiniteQuery`; ключ — застосований (відкладений) фільтр без курсора.
  const listKey = structureChangesQuery(applied);
  const changes = useStructureChangePages(applied);
  const changePage = useMemo(() => mergePages(changes.data?.pages), [changes.data]);
  const authorOptions = useAuthorOptions(changePage?.items, changedBy);
  const entityTypes = useStructureEntityTypes(from, to);
  const entityTypeOptions = useMemo(() => {
    const codes = new Set(Array.isArray(entityTypes.data) ? entityTypes.data : []);
    for (const item of changePage?.items ?? []) codes.add(item.entityType);
    if (entityType !== null && entityType !== '') codes.add(entityType);

    return [...codes]
      .map((value) => ({ value, label: entityTypeLabel(value) }))
      .sort((a, b) => a.label.localeCompare(b.label));
  }, [entityTypes.data, changePage, entityType]);

  return (
    <>
      <FilterRow mb="xs" data-audit-filter-row="structure">
        {/* ⛔ Було: вільний текст із точним збігом КОДУ (`cfg.RegistryDef`) на сервері. Тепер — типи, що є у вікні,
            підписами мовою інтерфейсу; значення — код сервера. */}
        <Select
          size="xs"
          miw={200}
          searchable
          clearable
          label={t('audit.entityType')}
          placeholder={t('audit.entityTypeAny')}
          nothingFoundMessage={t('audit.filterNothing')}
          data={entityTypeOptions}
          value={entityType === '' ? null : entityType}
          onChange={(value) => {
            setEntityType(value);
          }}
        />
        {/* ⛔ `R-18`: автор — вибором за іменем, а не номером `UserId`. */}
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

      {/* ⚠ `U-21`: та сама будова ряду, що в журналі комірок, — див. `FilterHints`. */}
      <FilterHints texts={[t('audit.authorHint')]} />

      <AsyncBoundary<StructureChangePage>
        isPending={changes.isPending}
        error={changes.isFetchNextPageError ? null : changes.error}
        data={changePage}
        isEmpty={(page) => page.items.length === 0}
        emptyTitle={t('audit.structureEmpty')}
        emptyHint={t('audit.emptyHint')}
        skeleton="table"
        onRetry={() => void changes.refetch()}
      >
        {(page) => (
          <>
            <Table striped className="ecr-sticky-head ecr-audit-table" aria-label={t('audit.viewStructure')}>
              <Table.Thead>
                <Table.Tr>
                  <SortableTh label={t('audit.when')} column="time" sort={sort} onSort={setSort} />
                  <SortableTh label={t('audit.who')} column="user" sort={sort} onSort={setSort} />
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
                    <Table.Td data-audit-time-cell="">
                      {/* ✎ P-1124-4: місцевий час читача; пояс майданчика з конверта «Reopen» — у підказці. */}
                      <AuditChangedAt value={change.changedAt} zone={structureChangeReasonZone(change.changeReason)} />
                    </Table.Td>
                    {/* ⛔ `R-18`: ім'я з сервера; номер — у підказці. */}
                    <Table.Td title={`#${String(change.changedByUserId)}`} data-audit-author="">
                      {authorName(change)}
                    </Table.Td>
                    <Table.Td>
                      {/* Читабельний код сутності (якщо сервер його визначив), номер — другим рядком: журнал є
                          доказом, номер лишається доступним. Видалена сутність — як і раніше номер. */}
                      <Text size="xs" title={`${change.entityType} #${String(change.entityId)}`}>
                        {entityTypeLabel(change.entityType)} · {change.entityLabel ?? change.entityId}
                      </Text>
                      {change.entityLabel !== null && change.entityLabel !== undefined && (
                        <Text size="xs" c="var(--ecr-muted)" ff="monospace" data-audit-entity-id="">
                          #{change.entityId}
                        </Text>
                      )}
                    </Table.Td>
                    <Table.Td title={change.operation}>{operationLabel(change.operation)}</Table.Td>
                    <Table.Td>
                      {change.changeReason === null ? '—' : structureChangeReasonText(change.changeReason)}
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>

            {/* ✎ W-3e: сторінки ДОПИСУЮТЬСЯ прокруткою (`InfiniteListFooter`). */}
            <InfiniteListFooter key={listKey} pager={changes} shown={page.items.length} total={page.totalCount} />
          </>
        )}
      </AsyncBoundary>
    </>
  );
}

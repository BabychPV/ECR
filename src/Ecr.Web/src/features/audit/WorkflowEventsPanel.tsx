import { useMemo, type JSX } from 'react';
import { Badge, Button, Select, Table, Text } from '@mantine/core';
import {
  fetchWorkflowExport,
  useWorkflowEvents,
  workflowEventsQuery,
  type WorkflowEventPage,
} from '@/features/audit/api';
import { AuditExportButton } from '@/features/audit/AuditExportButton';
import { useAuditSort } from '@/features/audit/auditSort';
import { authorName, useAuthorOptions } from '@/features/audit/authorOptions';
import { SortableTh } from '@/features/audit/SortableTh';
import { FilterHints, FilterRow, readerOnlyDescription } from '@/shared/ui/FilterBar';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { Timestamp } from '@/shared/ui/Timestamp';
import { useDebouncedFilter, useFilterCursor } from '@/shared/ui/useDebouncedFilter';
import { useUrlNumber, useUrlState } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';
import { humanizeIdentifier } from './auditLabels';
import './auditTable.css';

/** Дії журналу погодження — ті самі значення, що пише `wf.ApprovalEvent.Action`. */
const WorkflowActions = ['Submit', 'Approve', 'ApproveStep', 'Reject', 'Reopen', 'Recall'] as const;

/** Довжина коментаря в клітинці: решта — в `title` (повний текст) і в CSV. */
export const CommentCellMaxChars = 140;

/** Дія словом мовою інтерфейсу; нова дія, якої ще немає в каталозі, — читабельна форма ідентифікатора. */
export function workflowActionLabel(action: string): string {
  // Літерали, а не збирання ключа: сторож каталогу бачить кожен ключ (EndpointCoverageTests).
  switch (action) {
    case 'Submit':
      return t('audit.workflowAction.Submit');
    case 'Approve':
      return t('audit.workflowAction.Approve');
    case 'ApproveStep':
      return t('audit.workflowAction.ApproveStep');
    case 'Reject':
      return t('audit.workflowAction.Reject');
    case 'Reopen':
      return t('audit.workflowAction.Reopen');
    case 'Recall':
      return t('audit.workflowAction.Recall');
    default:
      return humanizeIdentifier(action);
  }
}

/** Коментар для клітинки: звичайний текст, обрізаний з «…»; повний — у `title`. */
export function clippedComment(comment: string): string {
  return comment.length > CommentCellMaxChars ? `${comment.slice(0, CommentCellMaxChars)}…` : comment;
}

/**
 * Журнал подій погодження аркушів (`wf.ApprovalEvent`) — четверта вкладка екрана аудиту: подання, затвердження,
 * відхилення, повернення, відкликання — з коментарем, автором, часом і аркушем/періодом.
 *
 * ⚠ Будова — як у `SecurityEventsPanel`: вікно приходить від сторінки, порядок — спільний `?sort=`, курсор скидається
 * разом із застосованим фільтром. Коментар — ЗВИЧАЙНИЙ ТЕКСТ (React екранує; жодного HTML), довгий — обрізається.
 */
export function WorkflowEventsPanel({ from, to }: { from: string; to: string }): JSX.Element {
  const [action, setAction] = useUrlState('action');
  const [changedBy, setChangedBy] = useUrlNumber('changedBy');
  const appliedAction = useDebouncedFilter(action);
  const appliedChangedBy = useDebouncedFilter(changedBy);
  const [sort, setSort] = useAuditSort();

  const applied = { from, to, action: appliedAction, changedByUserId: appliedChangedBy, sort, limit: 100 };
  const [cursor, setCursor] = useFilterCursor(workflowEventsQuery(applied));

  const events = useWorkflowEvents({ ...applied, cursor });
  const authored = useMemo(
    () =>
      (events.data?.items ?? [])
        .filter((e) => e.changedByUserId !== null)
        .map((e) => ({ changedByUserId: e.changedByUserId ?? 0, changedByDisplayName: e.changedByDisplayName })),
    [events.data],
  );
  const authorOptions = useAuthorOptions(authored, changedBy);

  return (
    <>
      <FilterRow mb="xs" data-audit-filter-row="workflow">
        <Select
          size="xs"
          miw={200}
          clearable
          label={t('audit.action')}
          placeholder={t('audit.authorAny')}
          data={WorkflowActions.map((value) => ({ value, label: workflowActionLabel(value) }))}
          value={action}
          onChange={(value) => {
            setAction(value);
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
      </FilterRow>

      <FilterHints texts={[t('audit.authorHint')]} />

      <AuditExportButton
        tab="workflow"
        from={from}
        to={to}
        fallbackName="audit-workflow.csv"
        load={() => fetchWorkflowExport(applied)}
      />

      <AsyncBoundary<WorkflowEventPage>
        isPending={events.isPending}
        error={events.error}
        data={events.data}
        isEmpty={(page) => page.items.length === 0}
        emptyTitle={t('audit.workflowEmpty')}
        emptyHint={t('audit.emptyHint')}
        skeleton="table"
        onRetry={() => void events.refetch()}
      >
        {(page) => (
          <>
            <Table striped className="ecr-sticky-head ecr-audit-table" aria-label={t('audit.viewWorkflow')}>
              <Table.Thead>
                <Table.Tr>
                  <SortableTh label={t('audit.when')} column="time" sort={sort} onSort={setSort} />
                  <SortableTh label={t('audit.who')} column="user" sort={sort} onSort={setSort} />
                  <Table.Th>{t('audit.action')}</Table.Th>
                  <Table.Th>{t('audit.documentCell')}</Table.Th>
                  <Table.Th>{t('audit.comment')}</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {page.items.map((event, index) => (
                  <Table.Tr key={`${event.changedAt}:${event.documentId}:${event.sheetDefId}:${index}`}>
                    <Table.Td>
                      <Timestamp value={event.changedAt} />
                    </Table.Td>
                    {/* Системний перехід (автора немає) — «Система»; видалений користувач — «користувач #id». */}
                    <Table.Td title={event.changedByUserId === null ? undefined : `#${String(event.changedByUserId)}`}>
                      {event.changedByUserId === null
                        ? t('audit.systemActor')
                        : authorName({
                            changedByUserId: event.changedByUserId,
                            changedByDisplayName: event.changedByDisplayName,
                          })}
                    </Table.Td>
                    <Table.Td title={event.action}>
                      <Badge size="sm" variant="light" color={event.action === 'Reject' ? 'statusWarning' : 'brand'}>
                        {workflowActionLabel(event.action)}
                      </Badge>
                      <Text size="xs" c="var(--ecr-muted)">
                        {event.fromStatus} → {event.toStatus}
                      </Text>
                    </Table.Td>
                    <Table.Td title={`#${String(event.documentId)} · #${String(event.sheetDefId)}`}>
                      <Text size="sm">
                        {event.documentBusinessKey ?? t('audit.documentGone', { id: event.documentId })}
                        {' · '}
                        {event.sheetCode ?? `#${String(event.sheetDefId)}`}
                      </Text>
                      <Text size="xs" c="var(--ecr-muted)">
                        {event.periodKey}
                      </Text>
                    </Table.Td>
                    <Table.Td title={event.comment ?? undefined} data-audit-comment="">
                      {event.comment === null || event.comment === undefined ? (
                        '—'
                      ) : (
                        <Text size="sm" style={{ wordBreak: 'break-word' }}>
                          {clippedComment(event.comment)}
                        </Text>
                      )}
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

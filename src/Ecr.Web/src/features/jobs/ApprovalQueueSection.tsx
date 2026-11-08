import { useState, type JSX } from 'react';
import { Anchor, Button, Card, Group, Stack, Text, UnstyledButton } from '@mantine/core';
import { Link } from 'react-router-dom';
import { formatDateTime } from '@/shared/format/datetime';
import { formatPeriodKey } from '@/shared/format/periodKey';
import { formatCount } from '@/shared/format/plural';
import { t } from '@/shared/i18n';
import { MyTasksSection } from './MyTasksSection';
import { approvalQueueHref, approvalQueueItems, useApprovalQueue, type ApprovalQueueItem } from './useApprovalQueue';

/**
 * Блок «До погодження» у шухляді «My tasks» (ФВ-12.5): подані аркуші, які я можу затвердити зараз.
 *
 * Прецедент - `EditAssignmentsSection`: порожній і завантажувальний стани мовчать, відмова видима з повтором.
 * Затвердження - на сторінці документа (кнопка там); блок лише веде на потрібний аркуш і період.
 * Ім'я того, хто подав, - екранований текст React; `null` (користувача вже немає) просто не показується.
 * ✎ UAT my-tasks-collapse: блок згортається, а місяці одного аркуша одного документа - одним рядком-групою.
 */
export function ApprovalQueueSection({
  opened,
  onNavigate,
}: {
  readonly opened: boolean;
  /** Перехід на аркуш: шухляда закривається, інакше лишається поверх документа. */
  readonly onNavigate?: () => void;
}): JSX.Element | null {
  const query = useApprovalQueue(opened);

  if (query.error !== null) {
    return (
      <Text size="sm" c="dimmed" mb="md" data-testid="my-tasks-approval-error">
        {t('myTasks.approval.error')}{' '}
        <Anchor component="button" type="button" size="sm" onClick={() => void query.refetch()}>
          {t('common.retry')}
        </Anchor>
      </Text>
    );
  }
  const items = approvalQueueItems(query.data);
  if (items.length === 0) return null;

  return (
    <MyTasksSection
      id="my-tasks-approval"
      title={t('myTasks.approval.title')}
      count={`${String(items.length)}${query.hasNextPage ? '+' : ''}`}
    >
      {groupApprovalItems(items).map((entry) =>
        entry.items.length === 1 ? (
          <ApprovalRow key={itemKey(entry.items[0] as ApprovalQueueItem)} item={entry.items[0] as ApprovalQueueItem} onNavigate={onNavigate} />
        ) : (
          <ApprovalGroup key={entry.key} entry={entry} onNavigate={onNavigate} />
        ),
      )}
      {query.hasNextPage && (
        <Button
          variant="subtle"
          size="xs"
          loading={query.isFetchingNextPage}
          onClick={() => void query.fetchNextPage()}
          data-testid="my-tasks-approval-more"
        >
          {t('myTasks.approval.more')}
        </Button>
      )}
    </MyTasksSection>
  );
}

/** Рядки одного документа й аркуша (різні місяці) у порядку сервера. */
export interface ApprovalGroupEntry {
  readonly key: string;
  readonly items: readonly ApprovalQueueItem[];
}

/**
 * ✎ UAT my-tasks-collapse: місяці одного аркуша одного документа - одна група, на місці свого ПЕРШОГО рядка
 * (порядок сервера не міняється); одиночний рядок - група з одного елемента.
 */
export function groupApprovalItems(items: readonly ApprovalQueueItem[]): readonly ApprovalGroupEntry[] {
  const groups = new Map<string, ApprovalQueueItem[]>();
  for (const item of items) {
    const key = `${String(item.documentId)}:${String(item.sheetDefId)}`;
    const list = groups.get(key);
    if (list === undefined) groups.set(key, [item]);
    else list.push(item);
  }

  return [...groups].map(([key, list]) => ({ key, items: list }));
}

function itemKey(item: ApprovalQueueItem): string {
  return `${String(item.documentId)}:${String(item.sheetDefId)}:${String(item.periodKey)}`;
}

function ApprovalGroup({
  entry,
  onNavigate,
}: {
  readonly entry: ApprovalGroupEntry;
  readonly onNavigate: (() => void) | undefined;
}): JSX.Element {
  const [expanded, setExpanded] = useState(false);
  const first = entry.items[0] as ApprovalQueueItem;
  const bodyId = `my-tasks-approval-group-${entry.key.replace(':', '-')}`;

  return (
    <Card withBorder padding="xs" data-approval-group={entry.key}>
      <UnstyledButton
        aria-expanded={expanded}
        aria-controls={bodyId}
        onClick={() => setExpanded((value) => !value)}
        data-action="toggle-approval-group"
      >
        <Text size="sm" fw={500}>
          {first.documentKey} · {first.sheetCode} — {formatCount(entry.items.length, 'myTasks.approval.months')}
        </Text>
        <Text size="xs" c="dimmed">
          {first.projectCode}
        </Text>
      </UnstyledButton>
      <div id={bodyId} hidden={!expanded}>
        <Stack gap="xs" mt="xs">
          {entry.items.map((item) => (
            <ApprovalRow key={itemKey(item)} item={item} onNavigate={onNavigate} />
          ))}
        </Stack>
      </div>
    </Card>
  );
}

function ApprovalRow({
  item,
  onNavigate,
}: {
  readonly item: ApprovalQueueItem;
  readonly onNavigate: (() => void) | undefined;
}): JSX.Element {
  return (
    <Card withBorder padding="xs" data-approval-document={item.documentId} data-approval-sheet={item.sheetCode}>
      <Stack gap="xs">
        <div>
          <Text size="sm" fw={500}>
            {item.documentKey} · {item.sheetCode}
          </Text>
          <Text size="xs" c="dimmed">
            {item.projectCode} · {formatPeriodKey(item.periodKey) || String(item.periodKey)}
          </Text>
          <Text size="xs" c="dimmed">
            {item.submittedByDisplayName === null
              ? t('myTasks.approval.submittedAt', { date: formatDateTime(item.submittedAt) })
              : t('myTasks.approval.submittedBy', {
                  name: item.submittedByDisplayName,
                  date: formatDateTime(item.submittedAt),
                })}
            {item.stepOrdinal !== null && item.stepCount !== null
              && ` · ${t('myTasks.approval.step', { n: String(item.stepOrdinal), count: String(item.stepCount) })}`}
          </Text>
        </div>
        <Group justify="flex-end">
          <Anchor component={Link} to={approvalQueueHref(item)} size="sm" data-action="review" onClick={onNavigate}>
            {t('myTasks.approval.open')}
          </Anchor>
        </Group>
      </Stack>
    </Card>
  );
}

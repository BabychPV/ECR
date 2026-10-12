import type { JSX } from 'react';
import { Anchor, Button, Card, Group, Stack, Text } from '@mantine/core';
import { Link } from 'react-router-dom';
import { formatDateTime } from '@/shared/format/datetime';
import { t } from '@/shared/i18n';
import { approvalQueueHref, approvalQueueItems, useApprovalQueue, type ApprovalQueueItem } from './useApprovalQueue';

/**
 * Блок «До погодження» у шухляді «My tasks» (ФВ-12.5): подані аркуші, які я можу затвердити зараз.
 *
 * Прецедент - `EditAssignmentsSection`: порожній і завантажувальний стани мовчать, відмова видима з повтором.
 * Затвердження - на сторінці документа (кнопка там); блок лише веде на потрібний аркуш і період.
 * Ім'я того, хто подав, - екранований текст React; `null` (користувача вже немає) просто не показується.
 */
export function ApprovalQueueSection({ opened }: { readonly opened: boolean }): JSX.Element | null {
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
    <Stack gap="xs" mb="md" component="section" aria-labelledby="my-tasks-approval-title" data-testid="my-tasks-approval">
      <Text id="my-tasks-approval-title" fw={600} size="sm">
        {t('myTasks.approval.title')}
      </Text>
      {items.map((item) => (
        <ApprovalRow key={`${String(item.documentId)}:${String(item.sheetDefId)}:${String(item.periodKey)}`} item={item} />
      ))}
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
    </Stack>
  );
}

function ApprovalRow({ item }: { readonly item: ApprovalQueueItem }): JSX.Element {
  return (
    <Card withBorder padding="xs" data-approval-document={item.documentId} data-approval-sheet={item.sheetCode}>
      <Stack gap="xs">
        <div>
          <Text size="sm" fw={500}>
            {item.documentKey} · {item.sheetCode}
          </Text>
          <Text size="xs" c="dimmed">
            {item.projectCode} · {String(item.periodKey)}
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
          <Anchor component={Link} to={approvalQueueHref(item)} size="sm" data-action="review">
            {t('myTasks.approval.open')}
          </Anchor>
        </Group>
      </Stack>
    </Card>
  );
}

import type { JSX } from 'react';
import { Anchor, Card, Group, Stack, Text } from '@mantine/core';
import { Link } from 'react-router-dom';
import { formatPeriodKey } from '@/shared/format/periodKey';
import { t } from '@/shared/i18n';
import { Timestamp } from '@/shared/ui/Timestamp';
import { isChatUnavailable, useMentionTasks } from './api';
import { DiscussionPanelValue } from './chatStore';
import type { MentionTask } from './types';

/**
 * Address of the discussion a mention leads to: the document, its period, the table and the open panel.
 *
 * ⛔ A literal, not `generatePath(routes.documentDetail.path)`: `features` do not import `app` (the same rule as in
 * `MyTasksDrawer.myTaskDocumentHref`). A divergence from the route registry is caught by
 * `__tests__/MentionTasksSection.test.tsx`.
 */
export function mentionTaskHref(task: Pick<MentionTask, 'documentId' | 'periodKey' | 'tableCode'>): string {
  const query = new URLSearchParams({
    periodKey: String(task.periodKey),
    table: task.tableCode,
    panel: DiscussionPanelValue,
  });

  return `/documents/${String(task.documentId)}?${query.toString()}`;
}

/**
 * The "mentions" block of the My tasks drawer: unread `@mentions` of the current user in table discussions.
 * A mention disappears from here once the chat is read (the server clears it), so the block needs no "done" action.
 *
 * ⚠ Same manners as the sibling sections: loading and empty are silent; a failure is visible with a retry. A server
 * that does not know the route yet (`404`) is an empty block, not "could not load" for everybody.
 */
export function MentionTasksSection({
  opened,
  onNavigate,
}: {
  readonly opened: boolean;
  /** Navigation to the document: the drawer closes, otherwise it stays above the panel. */
  readonly onNavigate?: (() => void) | undefined;
}): JSX.Element | null {
  const query = useMentionTasks(opened);

  if (query.error !== null) {
    if (isChatUnavailable(query.error)) return null;

    return (
      <Text size="sm" c="dimmed" mb="md" data-testid="my-tasks-mentions-error">
        {t('tableChat.tasksError')}{' '}
        <Anchor component="button" type="button" size="sm" onClick={() => void query.refetch()}>
          {t('common.retry')}
        </Anchor>
      </Text>
    );
  }

  const items = query.data?.items ?? [];
  if (items.length === 0) return null;

  return (
    <Stack gap="xs" mb="md" component="section" aria-labelledby="my-tasks-mentions-title" data-testid="my-tasks-mentions">
      <Text id="my-tasks-mentions-title" fw={600} size="sm">
        {t('tableChat.tasksTitle')}
      </Text>
      {items.map((task) => (
        <Card withBorder padding="xs" key={task.messageId} data-mention-task={task.messageId}>
          <Stack gap="xs">
            <Group justify="space-between" wrap="nowrap" gap="xs">
              <Text size="sm" fw={500}>
                {t('tableChat.taskLine', {
                  author: task.authorName,
                  table: task.tableCode,
                  period: formatPeriodKey(task.periodKey),
                })}
              </Text>
              <Text size="xs" c="dimmed">
                <Timestamp value={task.createdAt} />
              </Text>
            </Group>
            <Text size="sm" data-mention-snippet="" style={{ overflowWrap: 'anywhere' }}>
              {task.snippet}
            </Text>
            <Group justify="flex-end">
              <Anchor component={Link} to={mentionTaskHref(task)} size="sm" onClick={onNavigate} data-action="open">
                {t('tableChat.taskOpen')}
              </Anchor>
            </Group>
          </Stack>
        </Card>
      ))}
    </Stack>
  );
}

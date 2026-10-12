import type { JSX } from 'react';
import { Anchor, Card, Group, Stack, Text } from '@mantine/core';
import { Link } from 'react-router-dom';
import { formatDateTime } from '@/shared/format/datetime';
import { t } from '@/shared/i18n';
import { AssignmentBadge } from '@/features/editAssignments/AssignmentBadge';
import { useMyEditAssignments } from '@/features/editAssignments/api';
import { editAssignmentHref, sliceLabel } from '@/features/editAssignments/model';
import type { MyEditAssignment } from '@/features/editAssignments/types';

/**
 * Секції «Assigned to me» і «Awaiting my validation» у шухляді «My tasks» (DESIGN-OWNER-EDIT-GRANTS §7).
 *
 * Прецедент - `StaleDocumentsSection`: порожні/завантажувальні стани мовчать, відмова видима.
 * ⚠ `404`/`403` від сервера (передач ще немає на цьому стенді) - порожній стан, а не помилка (`getMyEditAssignments`):
 * інакше шухляда «My tasks» писала б «не вдалося завантажити» КОЖНОМУ користувачу. Дані - лише
 * рядки викликача. Статус - текстом (не лише кольором); коментар повернення - екранований текст React.
 */
export function EditAssignmentsSection({ opened }: { readonly opened: boolean }): JSX.Element | null {
  const query = useMyEditAssignments(opened);

  if (query.error !== null) {
    return (
      <Text size="sm" c="dimmed" mb="md" data-testid="my-tasks-edit-error">
        {t('myTasks.edit.error')}{' '}
        <Anchor component="button" type="button" size="sm" onClick={() => void query.refetch()}>
          {t('common.retry')}
        </Anchor>
      </Text>
    );
  }
  const data = query.data;
  if (data === undefined) return null;

  return (
    <>
      <AssignmentGroup
        id="my-tasks-assigned"
        title={t('myTasks.assignToFill')}
        items={data.assignedToMe}
        validate={false}
      />
      <AssignmentGroup
        id="my-tasks-validation"
        title={t('myTasks.awaitingValidation')}
        items={data.awaitingMyValidation}
        validate
      />
    </>
  );
}

function AssignmentGroup({
  id,
  title,
  items,
  validate,
}: {
  readonly id: string;
  readonly title: string;
  readonly items: readonly MyEditAssignment[];
  readonly validate: boolean;
}): JSX.Element | null {
  if (items.length === 0) return null;

  return (
    <Stack gap="xs" mb="md" component="section" aria-labelledby={`${id}-title`} data-testid={id}>
      <Text id={`${id}-title`} fw={600} size="sm">
        {title}
      </Text>
      {items.map((item) => (
        <Card withBorder padding="xs" key={item.assignmentId} data-edit-assignment={item.assignmentId}>
          <Stack gap="xs">
            <Group justify="space-between" wrap="nowrap" gap="xs">
              <div>
                <Text size="sm" fw={500}>
                  {item.documentKey}
                </Text>
                <Text size="xs" c="dimmed">
                  {sliceLabel(item.slice)}
                  {item.status === 'Filled' && item.statusChangedAt != null
                    && ` · ${t('myTasks.edit.filledAt', { date: formatDateTime(item.statusChangedAt) })}`}
                </Text>
              </div>
              {/* Токени статусу (контраст перевірено в обох темах), а не жовтий `filled` Mantine з білим текстом. */}
              <AssignmentBadge state={item.status} label={statusLabel(item.status)} />
            </Group>
            {item.status === 'Returned' && item.returnReason != null && item.returnReason !== '' && (
              <Text size="xs" data-testid="edit-assignment-return-reason">
                {t('myTasks.edit.returnedBy', { reason: item.returnReason })}
              </Text>
            )}
            {item.note != null && item.note !== '' && (
              <Text size="xs" c="dimmed">
                {item.note}
              </Text>
            )}
            <Group justify="flex-end" gap="md">
              {validate && (
                <Anchor
                  component={Link}
                  to={editAssignmentHref(item.documentId, true)}
                  size="sm"
                  data-action="validate"
                  aria-label={linkName(t('myTasks.edit.validate'), item)}
                >
                  {t('myTasks.edit.validate')}
                </Anchor>
              )}
              <Anchor
                component={Link}
                to={editAssignmentHref(item.documentId, false)}
                size="sm"
                data-action="open"
                aria-label={linkName(t('myTasks.edit.open'), item)}
              >
                {t('myTasks.edit.open')}
              </Anchor>
            </Group>
          </Stack>
        </Card>
      ))}
    </Stack>
  );
}

/** Літерали ключів, а не шаблон: сторожі каталогу шукають `t('…')` статично. */
function statusLabel(status: MyEditAssignment['status']): string {
  if (status === 'Returned') return t('myTasks.edit.status.Returned');
  if (status === 'Filled') return t('myTasks.edit.status.Filled');

  return t('myTasks.edit.status.Assigned');
}

/** WCAG 2.4.4: однакові «Open» / «Validate» у кожній картці розрізняються документом і місяцем (видимий текст - префікс назви, 2.5.3). */
function linkName(action: string, item: MyEditAssignment): string {
  return `${action}: ${item.documentKey}, ${sliceLabel(item.slice)}`;
}

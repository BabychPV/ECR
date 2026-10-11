import { useRef, useState, type JSX } from 'react';
import { Button, Group, Paper, Popover, Stack, Text } from '@mantine/core';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { t } from '@/shared/i18n';
import { showApiError } from '@/shared/ui/notify';
import { useFillEditAssignment, useMyEditAssignments } from './api';
import type { MyEditAssignment } from './types';

interface ExecutorStripProps {
  readonly documentId: number;
  /** Період, відкритий у документі: після «Заповнено» його зрізи перечитуються (`cellPermissions`). */
  readonly periodKey: number;
}

/**
 * Смуга виконавця над таблицею документа (EA-5b, §8 проєкту «Передача місяця»).
 *
 * Показує лише власні передачі цього документа: «Місяць передано вам: січень [Заповнено]», «Повернуто власником: <коментар>»,
 * «Заповнено - чекає валідації». Передач немає (звичайний користувач, власник) - смуги немає зовсім.
 *
 * ⛔ Смуга НІЧОГО не вирішує про права. Сірі комірки і підказка (`deny.AssignmentFilled`) приходять із
 * `cellPermissions` сервера; нового кольору й нового стану комірки тут немає - стан «заповнено» малює та сама
 * наявна сіра. Клієнт права не рахує.
 *
 * ⚠ `role="status"` на `Paper`, а не на `Alert`: Mantine `Alert` перебиває будь-яку роль на `alert` (читалка
 * озвучувала б зміну стану з перериванням). Перехід «Передано → Заповнено» озвучується ввічливо, без переривання читалки. Після
 * натискання кнопка зникає, тому фокус переходить на саму смугу (`tabIndex=-1`) - інакше він провалився б у `body`.
 * Текст власника і коментар повернення - звичайний текст (React екранує), довжина обмежена сервером.
 */
export function ExecutorStrip({ documentId, periodKey }: ExecutorStripProps): JSX.Element | null {
  const mine = useMyEditAssignments();
  const fill = useFillEditAssignment(documentId, periodKey);
  const fillLoading = usePendingLoading(fill.isPending);
  const host = useRef<HTMLDivElement>(null);
  const [confirming, setConfirming] = useState<number | null>(null);

  // Одна форма відповіді на всіх (шухляда «My tasks» читає той самий запит): документ вибирається тут, на клієнті.
  const items = (mine.data?.assignedToMe ?? []).filter((item) => item.documentId === documentId);
  if (items.length === 0) return null;

  const markFilled = (assignment: MyEditAssignment): void => {
    if (fill.isPending) return;
    fill.mutate(assignment.assignmentId, {
      onSuccess: () => {
        setConfirming(null);
        host.current?.focus();
      },
      onError: (error) => {
        setConfirming(null);
        showApiError(error);
      },
    });
  };

  return (
    <Paper
      ref={host}
      tabIndex={-1}
      p="sm"
      radius="sm"
      bg={items.some((item) => item.status !== 'Filled') ? 'var(--mantine-color-brand-light)' : 'var(--mantine-color-gray-light)'}
      role="status"
      data-testid="executor-strip"
    >
      <Stack gap="xs">
        {items.map((item) => {
          const label = item.slice;
          const canFill = item.status !== 'Filled';

          return (
            <Group
              key={item.assignmentId}
              justify="space-between"
              align="center"
              wrap="wrap"
              gap="xs"
              data-testid="executor-strip-row"
              data-status={item.status}
            >
              <Stack gap="xs">
                <Text size="sm" data-testid="executor-strip-text">
                  {textOf(item, label)}
                </Text>
                {item.status === 'Assigned' && hasText(item.note) && (
                  <Text size="xs" c="dimmed" data-testid="executor-strip-note">
                    {t('editAssign.bar.note', { note: item.note })}
                  </Text>
                )}
              </Stack>
              {canFill && (
                <Popover
                  opened={confirming === item.assignmentId}
                  onChange={(open) => setConfirming(open ? item.assignmentId : null)}
                  trapFocus
                  returnFocus
                  withArrow
                  position="bottom-end"
                >
                  <Popover.Target>
                    <Button
                      variant="default"
                      size="xs"
                      aria-haspopup="dialog"
                      aria-expanded={confirming === item.assignmentId}
                      onClick={() => setConfirming(confirming === item.assignmentId ? null : item.assignmentId)}
                      data-testid="executor-strip-fill"
                    >
                      {t('editAssign.bar.markFilled')}
                    </Button>
                  </Popover.Target>
                  <Popover.Dropdown role="dialog" aria-label={t('editAssign.bar.markFilled')}>
                    <Stack gap="xs" maw={320}>
                      <Text size="sm">{t('editAssign.bar.confirmFill', { slice: label })}</Text>
                      <Group justify="flex-end" gap="xs">
                        <Button variant="subtle" size="xs" onClick={() => setConfirming(null)}>
                          {t('common.cancel')}
                        </Button>
                        <Button
                          size="xs"
                          loading={fillLoading}
                          onClick={() => markFilled(item)}
                          data-testid="executor-strip-fill-confirm"
                        >
                          {t('editAssign.bar.markFilled')}
                        </Button>
                      </Group>
                    </Stack>
                  </Popover.Dropdown>
                </Popover>
              )}
            </Group>
          );
        })}
      </Stack>
    </Paper>
  );
}

function hasText(value: string | null | undefined): value is string {
  return typeof value === 'string' && value.trim() !== '';
}

function textOf(item: MyEditAssignment, slice: string): string {
  switch (item.status) {
    case 'Assigned':
      return t('editAssign.bar.assigned', { slice });
    case 'Returned':
      return hasText(item.returnReason)
        ? t('editAssign.bar.returned', { slice, reason: item.returnReason })
        : t('editAssign.bar.returnedNoReason', { slice });
    case 'Filled':
      return t('editAssign.bar.filled', { slice });
  }
}

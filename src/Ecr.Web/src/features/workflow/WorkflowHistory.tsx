import { useState, type JSX } from 'react';
import { Button, Group, Stack, Text } from '@mantine/core';
import { useWorkflowHistory } from '@/features/workflow/api';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { Timestamp } from '@/shared/ui/Timestamp';

/** Чию історію показувати. */
export interface WorkflowHistoryProps {
  /** Документ. */
  documentId: number;

  /** Період: стан затвердження існує окремо на кожен (`R-A6`). */
  periodKey: number;
}

/**
 * Хто й коли подав, погодив, відхилив або повернув аркуші (`BE-11b`).
 *
 * ⚠ Згорнутий за замовчуванням, і запит іде ЛИШЕ після розгортання: сторінка
 * документа вже робить чотири запити на відкриття, а історію читає одиниця з
 * тих, хто її відкрив.
 *
 * ⛔ Порожня історія — блок не малюється ЗОВСІМ (разом із кнопкою).
 * `wf.ApprovalEvent` починається порожньою, і для документа, старшого за
 * міграцію, порожній перелік під заголовком «History» читався б як «ніхто
 * нічого не подавав» — а це неправда. Знати, що вона порожня, можна лише
 * спитавши, тому до першого розгортання кнопка є завжди.
 */
export function WorkflowHistory({ documentId, periodKey }: WorkflowHistoryProps): JSX.Element | null {
  const [opened, setOpened] = useState(false);
  const history = useWorkflowHistory(documentId, periodKey, opened);

  if (history.data?.length === 0) {
    return null;
  }

  return (
    <Stack gap="xs" align="flex-start" data-testid="workflow-history">
      <Button
        size="xs"
        variant="subtle"
        aria-expanded={opened}
        loading={opened && history.isPending}
        onClick={() => setOpened((value) => !value)}
      >
        {t('workflow.history')}
      </Button>

      {opened && history.error !== null && (
        <ErrorAlert error={history.error} onRetry={() => void history.refetch()} />
      )}

      {opened && history.data !== undefined && (
        <Stack gap="xs" role="list">
          {history.data.map((event, index) => (
            // ⚠ Свого ідентифікатора подія не має; перелік лише читається й
            // замінюється цілком, тож позиція — чесний ключ.
            <Group key={index} gap="xs" role="listitem" data-testid="workflow-history-event">
              <Text size="sm" c="dimmed">
                <Timestamp value={event.at} />
              </Text>
              <Text size="sm" fw={600}>
                {event.sheetCode}
              </Text>
              <StatusBadge kind="sheet" state={event.fromState} quiet />
              <StatusBadge kind="sheet" state={event.toState} />
              <Text size="sm">{event.byDisplayName}</Text>
              {event.stepOrdinal !== null && (
                <Text size="sm" c="dimmed">
                  {t('workflow.historyStep', { step: event.stepOrdinal })}
                </Text>
              )}
              {event.reason !== null && <Text size="sm">{event.reason}</Text>}
            </Group>
          ))}
        </Stack>
      )}
    </Stack>
  );
}

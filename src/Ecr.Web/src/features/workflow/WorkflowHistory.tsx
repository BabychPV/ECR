import { useState, type JSX } from 'react';
import { Button, Group, Stack, Text, Title } from '@mantine/core';
import { useWorkflowHistory, type WorkflowEvent } from '@/features/workflow/api';
import { formatPeriodKey } from '@/shared/format/periodKey';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { Timestamp } from '@/shared/ui/Timestamp';

/** Один рядок журналу переходів. */
function EventRow({ event }: { event: WorkflowEvent }): JSX.Element {
  return (
    <Group gap="xs" role="listitem" data-testid="workflow-history-event">
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
      {/* D-440: чотири очі скасовано, але самопогодження лишається видимим у журналі. */}
      {event.approvedBySubmitter && (
        <Text size="sm" c="dimmed" data-testid="workflow-history-approved-by-submitter">
          {t('workflow.historyApprovedBySubmitter')}
        </Text>
      )}
      {event.reason !== null && <Text size="sm">{event.reason}</Text>}
    </Group>
  );
}

/**
 * ✎ LAND-YEAR L7b: події року групами за місяцем (`periodKey` події): заголовок — назва місяця, найновіший місяць першим.
 * Подія без `periodKey` (старіший сервер) — у групі `null` наприкінці, без заголовка-місяця.
 */
export function groupEventsByMonth(events: readonly WorkflowEvent[]): { periodKey: number | null; events: WorkflowEvent[] }[] {
  const groups = new Map<number | null, WorkflowEvent[]>();
  for (const event of events) {
    const key = event.periodKey ?? null;
    const group = groups.get(key);
    if (group === undefined) groups.set(key, [event]);
    else group.push(event);
  }

  return [...groups.entries()]
    .sort(([a], [b]) => (a === null ? 1 : b === null ? -1 : b - a))
    .map(([periodKey, grouped]) => ({ periodKey, events: grouped }));
}

function YearHistory({ events }: { events: readonly WorkflowEvent[] }): JSX.Element {
  return (
    <Stack gap="sm" data-testid="workflow-history-year">
      {groupEventsByMonth(events).map((group) => (
        <section
          key={group.periodKey ?? 'none'}
          aria-label={group.periodKey === null ? t('workflow.history') : formatPeriodKey(group.periodKey)}
          data-workflow-history-month={group.periodKey ?? ''}
        >
          {group.periodKey !== null && (
            <Title order={3} size="sm" mb="xs">
              {formatPeriodKey(group.periodKey) || String(group.periodKey)}
            </Title>
          )}
          <Stack gap="xs" role="list">
            {group.events.map((event, index) => (
              <EventRow key={index} event={event} />
            ))}
          </Stack>
        </section>
      ))}
    </Stack>
  );
}

/** Чию історію показувати. */
export interface WorkflowHistoryProps {
  /** Документ. */
  documentId: number;

  /** Період: стан затвердження існує окремо на кожен (`R-A6`). */
  periodKey: number;

  /**
   * Показ усередині діалогу «More → History» (макет не має окремого блока на
   * сторінці): без кнопки-перемикача, розгорнуто одразу; порожній журнал —
   * текстом, бо діалог без вмісту читався б як помилка.
   */
  embedded?: boolean;

  /**
   * ✎ LAND-YEAR L7b (п. 8 плану): документ «рік» (`YearWithMonthColumns`) — подання й погодження йдуть по місяцях, а
   * журнал одного періоду показував лише якір. `year` — події всіх місяців року (`?year=true`), групами за місяцем
   * (найновіший місяць першим, усередині — як віддав сервер, найновіші перші). Не задано — один період, як доти.
   */
  year?: boolean;
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
export function WorkflowHistory({ documentId, periodKey, embedded = false, year = false }: WorkflowHistoryProps): JSX.Element | null {
  const [toggled, setOpened] = useState(false);
  const opened = embedded || toggled;
  const history = useWorkflowHistory(documentId, periodKey, opened, null, year);

  if (history.data?.length === 0) {
    return embedded ? (
      <Text size="sm" c="dimmed" data-testid="workflow-history-empty">
        {t('state.emptyTitle')}
      </Text>
    ) : null;
  }

  return (
    <Stack gap="xs" align="flex-start" data-testid="workflow-history">
      {!embedded && (
        <Button
          variant="subtle"
          aria-expanded={opened}
          loading={opened && history.isPending}
          onClick={() => setOpened((value) => !value)}
        >
          {t('workflow.history')}
        </Button>
      )}

      {opened && history.error !== null && (
        <ErrorAlert error={history.error} onRetry={() => void history.refetch()} />
      )}

      {opened && history.data !== undefined && year && <YearHistory events={history.data} />}

      {opened && history.data !== undefined && !year && (
        <Stack gap="xs" role="list">
          {history.data.map((event, index) => (
            // ⚠ Свого ідентифікатора подія не має; перелік лише читається й
            // замінюється цілком, тож позиція — чесний ключ.
            <EventRow key={index} event={event} />
          ))}
        </Stack>
      )}
    </Stack>
  );
}

import { useState, type JSX } from 'react';
import { Anchor, Button, Card, Group, Stack, Text } from '@mantine/core';
import { Link } from 'react-router-dom';
import { formatDateTime } from '@/shared/format/datetime';
import { formatPeriodKey } from '@/shared/format/periodKey';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { approvalQueueHref } from './useApprovalQueue';
import { returnedSheetItems, useReturnedSheets, type ReturnedSheetItem } from './useReturnedSheets';

/** Причина довша - згортається до трьох рядків з «показати повністю» (довжина в символах або рядках). */
const ReasonClampChars = 180;
const ReasonClampLines = 3;

/**
 * ✎ D-449: блок «Повернуто на доопрацювання» у шухляді «My tasks» - аркуші, які погоджувач повернув мені
 * (власнику документа чи учаснику з правом писати), з причиною, хто й коли повернув.
 *
 * Прецедент - `ApprovalQueueSection`: порожній і завантажувальний стани мовчать, відмова видима з повтором.
 * Блок стоїть ПЕРШИМ у шухляді: повернута робота - найтерміновіше для автора. Повторне подання робиться на сторінці
 * аркуша; після нього рядок зникає сам (живий запит на сервері, ключ скидає `SheetActions`).
 * Ім'я й причина - екранований текст React (причину пише інша людина, може бути багаторядковою).
 */
export function ReturnedSheetsSection({
  opened,
  onNavigate,
}: {
  readonly opened: boolean;
  /** Перехід на аркуш: шухляда закривається, інакше лишається поверх документа. */
  readonly onNavigate?: () => void;
}): JSX.Element | null {
  const query = useReturnedSheets(opened);

  if (query.error !== null) {
    return (
      <Text size="sm" c="dimmed" mb="md" data-testid="my-tasks-returned-error">
        {t('myTasks.returned.error')}{' '}
        <Anchor component="button" type="button" size="sm" onClick={() => void query.refetch()}>
          {t('common.retry')}
        </Anchor>
      </Text>
    );
  }
  const items = returnedSheetItems(query.data);
  if (items.length === 0) return null;

  return (
    <Stack gap="xs" mb="md" component="section" aria-labelledby="my-tasks-returned-title" data-testid="my-tasks-returned">
      <Text id="my-tasks-returned-title" fw={600} size="sm">
        {t('myTasks.returned.title')}
      </Text>
      {items.map((item) => (
        <ReturnedRow key={`${String(item.documentId)}:${String(item.sheetDefId)}:${String(item.periodKey)}`} item={item} onNavigate={onNavigate} />
      ))}
      {query.hasNextPage && (
        <Button
          variant="subtle"
          size="xs"
          loading={query.isFetchingNextPage}
          onClick={() => void query.fetchNextPage()}
          data-testid="my-tasks-returned-more"
        >
          {t('myTasks.returned.more')}
        </Button>
      )}
    </Stack>
  );
}

function ReturnedRow({
  item,
  onNavigate,
}: {
  readonly item: ReturnedSheetItem;
  readonly onNavigate: (() => void) | undefined;
}): JSX.Element {
  const [expanded, setExpanded] = useState(false);
  const documentTitle = localized(item.documentNameL10n) || item.documentKey;
  const sheetTitle = localized(item.sheetNameL10n) || item.sheetCode;
  const long = item.reason.length > ReasonClampChars || item.reason.split('\n').length > ReasonClampLines;
  const reasonId = `my-tasks-returned-reason-${String(item.documentId)}-${String(item.sheetDefId)}-${String(item.periodKey)}`;

  return (
    <Card withBorder padding="xs" data-returned-document={item.documentId} data-returned-sheet={item.sheetCode}>
      <Stack gap="xs">
        <div>
          <Text size="sm" fw={500}>
            {documentTitle} · {sheetTitle}
          </Text>
          <Text size="xs" c="dimmed">
            {item.projectCode} · {formatPeriodKey(item.periodKey) || String(item.periodKey)}
          </Text>
          <Text size="xs" c="dimmed">
            {item.returnedByDisplayName === null
              ? t('myTasks.returned.returnedAt', { date: formatDateTime(item.returnedAt) })
              : t('myTasks.returned.returnedBy', {
                  name: item.returnedByDisplayName,
                  date: formatDateTime(item.returnedAt),
                })}
          </Text>
        </div>
        {item.reason.length > 0 && (
          <div>
            <Text size="xs" fw={500}>
              {t('myTasks.returned.reason')}
            </Text>
            <Text
              id={reasonId}
              size="sm"
              style={{ whiteSpace: 'pre-line', overflowWrap: 'anywhere' }}
              {...(long && !expanded ? { lineClamp: ReasonClampLines } : {})}
              data-returned-reason
            >
              {item.reason}
            </Text>
            {long && (
              <Anchor
                component="button"
                type="button"
                size="xs"
                aria-expanded={expanded}
                aria-controls={reasonId}
                onClick={() => setExpanded((value) => !value)}
                data-action="toggle-reason"
              >
                {expanded ? t('myTasks.returned.reasonLess') : t('myTasks.returned.reasonMore')}
              </Anchor>
            )}
          </div>
        )}
        <Group justify="flex-end">
          <Anchor component={Link} to={approvalQueueHref(item)} size="sm" data-action="open-returned" onClick={onNavigate}>
            {t('myTasks.returned.open')}
          </Anchor>
        </Group>
      </Stack>
    </Card>
  );
}

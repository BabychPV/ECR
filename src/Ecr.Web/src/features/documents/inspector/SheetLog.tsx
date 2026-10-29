import { useState, type JSX } from 'react';
import { Badge, Box, Button, Checkbox, Group, Text } from '@mantine/core';
import { useSheetActivity, type SheetActivityItem } from '@/features/workflow/api';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { Timestamp } from '@/shared/ui/Timestamp';
import { addressChip } from './inspectorModel';

/** Аркуш, чий журнал показує панель. */
export interface InspectorSheet {
  readonly sheetDefId: number;
  readonly code: string;
  readonly name: string;
}

/** Адреса комірки з рядка журналу - для переходу в сітку. */
export interface SheetLogTarget {
  readonly sheetCode: string;
  readonly tableCode: string;
  readonly rowKey: string;
  readonly columnCode: string;
}

/** Скільки символів коментаря процесу показується в рядку (повний текст - у `title`). */
export const SheetLogReasonMax = 300;

export interface SheetLogProps {
  readonly documentId: number;
  readonly periodKey: number;
  readonly sheet: InspectorSheet | null;
  /** Клік по рядку правки веде в комірку; `undefined` - рядки не клікабельні. */
  readonly onOpenCell?: ((target: SheetLogTarget) => void) | undefined;
}

/**
 * Журнал дій на аркуші (вкладка History без вибраної комірки / режим «Sheet log»): одна лінія на дію, найновіші
 * перші, без групування. Правки значень людьми (сітка, імпорт, правка погоджувача) і дії процесу з коментарем.
 *
 * ⛔ Що людина бачить, вирішує СЕРВЕР (право, межі читання аркуша й таблиць, період): клієнт нічого не відсіює і не
 * додумує. Системні зміни (перерахунок, інтеграція) - лише за прапорцем; імпорт - дія людини. Коментар і значення
 * рендеряться як текст (React екранує), довгий коментар обрізається.
 *
 * ⚠ Рядків «додано/видалено рядок» тут немає: сервер їх не веде в аудиті (немає джерела) - не вигадуємо.
 */
export function SheetLog({ documentId, periodKey, sheet, onOpenCell }: SheetLogProps): JSX.Element {
  const [includeSystem, setIncludeSystem] = useState(false);
  const log = useSheetActivity(documentId, sheet?.sheetDefId ?? 0, periodKey, includeSystem, sheet !== null);

  if (sheet === null) {
    return <Empty title={t('inspector.sheetHistoryTitle')} hint={t('inspector.sheetHistoryEmpty')} />;
  }

  const items = log.data?.pages.flatMap((page) => (Array.isArray(page.items) ? page.items : [])) ?? [];

  return (
    <div data-inspector-sheet-history="">
      <Box px="sm" py="xs">
        <Checkbox
          size="xs"
          checked={includeSystem}
          onChange={(event) => setIncludeSystem(event.currentTarget.checked)}
          label={t('inspector.history.includeSystem')}
          data-inspector-include-system=""
        />
      </Box>

      {log.error !== null && (
        <Box p="sm">
          <ErrorAlert error={log.error} onRetry={() => void log.refetch()} />
        </Box>
      )}

      {log.error === null && log.data === undefined && (
        <Text size="sm" c="dimmed" p="sm" role="status">
          {t('inspector.loading')}
        </Text>
      )}

      {log.error === null && log.data !== undefined && items.length === 0 && (
        <Empty title={t('inspector.sheetHistoryTitle')} hint={t('inspector.sheetHistoryEmpty')} />
      )}

      {items.length > 0 && (
        <div role="list" aria-label={t('inspector.sheetHistoryTitle')}>
          {items.map((item, index) => (
            // Рядок не має власного id: перелік лише читається й дочитується з кінця - позиція чесний ключ.
            <SheetLogRow key={index} item={item} sheetCode={sheet.code} onOpenCell={onOpenCell} />
          ))}
        </div>
      )}

      {log.hasNextPage && (
        <Box p="sm">
          <Button
            size="xs"
            variant="subtle"
            loading={log.isFetchingNextPage}
            onClick={() => void log.fetchNextPage()}
            data-inspector-load-more=""
          >
            {t('inspector.history.loadMore')}
          </Button>
        </Box>
      )}
    </div>
  );
}

function SheetLogRow({
  item,
  sheetCode,
  onOpenCell,
}: {
  item: SheetActivityItem;
  sheetCode: string;
  onOpenCell: ((target: SheetLogTarget) => void) | undefined;
}): JSX.Element {
  return item.kind === 'CellEdit'
    ? <CellEditRow item={item} sheetCode={sheetCode} onOpenCell={onOpenCell} />
    : <WorkflowRow item={item} />;
}

/** Імʼя виконавця: людина - імʼя, система - локалізований підпис. */
function actorOf(item: SheetActivityItem): string {
  return item.byDisplayName === 'system' ? t('audit.systemAuthor') : item.byDisplayName;
}

function CellEditRow({
  item,
  sheetCode,
  onOpenCell,
}: {
  item: SheetActivityItem;
  sheetCode: string;
  onOpenCell: ((target: SheetLogTarget) => void) | undefined;
}): JSX.Element {
  const rowKey = item.rowKey ?? '';
  const columnCode = item.columnCode ?? '';
  const tableCode = item.tableCode ?? '';
  const where = [
    localized(item.tableNameL10n),
    localized(item.rowLabelL10n) || rowKey,
    localized(item.columnHeaderL10n) || columnCode,
  ]
    .filter((part) => part.length > 0)
    .join(' · ');
  const canOpen = onOpenCell !== undefined && rowKey.length > 0 && columnCode.length > 0 && tableCode.length > 0;

  const body = (
    <>
      <Group justify="space-between" gap="xs" wrap="nowrap">
        <Text size="sm" fw={500} data-inspector-author="">
          {actorOf(item)}
        </Text>
        <Text size="xs" c="dimmed">
          <Timestamp value={item.at} />
        </Text>
      </Group>
      <Text size="xs" c="dimmed" style={{ overflowWrap: 'anywhere' }} data-inspector-cell-where="">
        {where}
      </Text>
      <div className="ecr-insp-diff">
        {item.oldValue === null ? <Text span c="dimmed">—</Text> : <s>{item.oldValue}</s>}
        <svg width={14} height={14} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={1.75} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" focusable="false">
          <path d="M5 12h14M13 6l6 6-6 6" />
        </svg>
        <span>{item.newValue ?? '—'}</span>
      </div>
      <Group gap="xs">
        <Badge size="xs" variant="default" radius="sm" tt="none">
          {`${t('audit.origin')}: ${item.origin ?? ''}`}
        </Badge>
      </Group>
    </>
  );

  if (!canOpen) {
    return (
      <div role="listitem" className="ecr-insp-hist" data-inspector-sheet-event="CellEdit">
        {body}
      </div>
    );
  }

  return (
    <div role="listitem" data-inspector-sheet-event="CellEdit">
      <button
        type="button"
        className="ecr-insp-hist ecr-insp-hist-open"
        title={`${t('document.validationGoTo')}: ${addressChip(rowKey, columnCode)}`}
        onClick={() => onOpenCell({ sheetCode, tableCode, rowKey, columnCode })}
      >
        {body}
      </button>
    </div>
  );
}

function WorkflowRow({ item }: { item: SheetActivityItem }): JSX.Element {
  const reason = item.reason ?? '';
  const clipped = reason.length > SheetLogReasonMax ? `${reason.slice(0, SheetLogReasonMax)}…` : reason;

  return (
    <div role="listitem" className="ecr-insp-hist" data-inspector-sheet-event={item.action ?? 'Workflow'}>
      <Group justify="space-between" gap="xs" wrap="nowrap">
        <Text size="sm" fw={500}>
          {eventActionLabel(item.action ?? '')}
        </Text>
        <Text size="xs" c="dimmed">
          <Timestamp value={item.at} />
        </Text>
      </Group>
      <Text size="xs" c="dimmed">
        {actorOf(item)}
      </Text>
      {clipped.length > 0 && (
        <Text size="sm" title={reason} style={{ whiteSpace: 'pre-line', overflowWrap: 'anywhere' }} data-inspector-sheet-reason="">
          {clipped}
        </Text>
      )}
    </div>
  );
}

/** Літеральні ключі (а не складені рядки): сторож каталогу бачить кожен ключ. Невідома дія - сирий код. */
function eventActionLabel(action: string): string {
  switch (action) {
    case 'Submit':
      return t('workflow.eventAction.Submit');
    case 'Approve':
      return t('workflow.eventAction.Approve');
    case 'ApproveStep':
      return t('workflow.eventAction.ApproveStep');
    case 'Reject':
      return t('workflow.eventAction.Reject');
    case 'Reopen':
      return t('workflow.eventAction.Reopen');
    case 'Recall':
      return t('workflow.eventAction.Recall');
    default:
      return action;
  }
}

function Empty({ title, hint }: { title: string; hint: string }): JSX.Element {
  return (
    <div className="ecr-insp-empty">
      <b>{title}</b>
      <span>{hint}</span>
    </div>
  );
}

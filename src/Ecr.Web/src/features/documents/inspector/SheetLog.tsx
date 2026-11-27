import { useState, type JSX } from 'react';
import { Badge, Box, Button, Checkbox, Group, Text } from '@mantine/core';
import { auditValueText, rowText } from '@/features/audit/auditValue';
import { formatPeriodKey } from '@/shared/format/periodKey';
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
  /**
   * ✎ LAND-YEAR L7b (п. 6 плану): документ «рік» (`YearWithMonthColumns`) — журнал УСІХ місяців року, злитий сервером
   * за часом (`?year=true`), і кожен рядок називає свій місяць: правка вересня в журналі «за рік» без назви місяця
   * читалася б як правка якоря. Копія на місяць — журнал одного періоду, як і раніше, без позначки.
   */
  readonly year?: boolean;
}

/**
 * Журнал дій на аркуші (вкладка History без вибраної комірки / режим «Sheet log»): одна лінія на дію, найновіші
 * перші, без групування. Правки значень людьми (сітка, імпорт, правка погоджувача) і дії процесу з коментарем.
 *
 * ⛔ Що людина бачить, вирішує СЕРВЕР (право, межі читання аркуша й таблиць, період): клієнт нічого не відсіює і не
 * додумує. Системні зміни (перерахунок, інтеграція) - лише за прапорцем; імпорт - дія людини. Коментар і значення
 * рендеряться як текст (React екранує), довгий коментар обрізається.
 *
 * Додавання й видалення рядків (`kind: RowChange`, `action: RowAdded | RowDeleted`) сервер веде в `aud.RowChange`
 * лише для таблиць, які читач бачить; клієнт лише показує їх (таблиця · ключ рядка), без значень комірок.
 */
export function SheetLog({ documentId, periodKey, sheet, onOpenCell, year = false }: SheetLogProps): JSX.Element {
  const [includeSystem, setIncludeSystem] = useState(false);
  const log = useSheetActivity(documentId, sheet?.sheetDefId ?? 0, periodKey, includeSystem, sheet !== null, year);

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
            <SheetLogRow key={index} item={item} sheetCode={sheet.code} onOpenCell={onOpenCell} year={year} />
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
  year,
}: {
  item: SheetActivityItem;
  sheetCode: string;
  onOpenCell: ((target: SheetLogTarget) => void) | undefined;
  year: boolean;
}): JSX.Element {
  const month = year ? <MonthTag periodKey={item.periodKey} /> : null;
  if (item.kind === 'CellEdit') {
    return <CellEditRow item={item} sheetCode={sheetCode} onOpenCell={onOpenCell} month={month} />;
  }

  return item.kind === 'RowChange' ? <RowChangeRow item={item} month={month} /> : <WorkflowRow item={item} month={month} />;
}

/** ✎ LAND-YEAR L7b: місяць рядка журналу року («September 2026») — назвою, як у чаті таблиці. */
function MonthTag({ periodKey }: { periodKey: number }): JSX.Element {
  return (
    <Text size="xs" c="dimmed" data-inspector-log-period={periodKey}>
      {formatPeriodKey(periodKey) || String(periodKey)}
    </Text>
  );
}

/** Додано / видалено рядок: дія, час, виконавець, таблиця · ключ рядка, походження. */
function RowChangeRow({ item, month }: { item: SheetActivityItem; month: JSX.Element | null }): JSX.Element {
  const where = [localized(item.tableNameL10n) || (item.tableCode ?? ''), item.rowKey ?? '']
    .filter((part) => part.length > 0)
    .join(' · ');

  return (
    <div role="listitem" className="ecr-insp-hist" data-inspector-sheet-event={item.action ?? 'RowChange'}>
      <Group justify="space-between" gap="xs" wrap="nowrap">
        <Text size="sm" fw={500}>
          {rowActionLabel(item.action ?? '')}
        </Text>
        <Text size="xs" c="dimmed">
          <Timestamp value={item.at} />
        </Text>
      </Group>
      {month}
      <Text size="xs" c="dimmed" data-inspector-author="">
        {actorOf(item)}
      </Text>
      <Text size="xs" c="dimmed" style={{ overflowWrap: 'anywhere' }} data-inspector-row-where="">
        {where}
      </Text>
      <Group gap="xs">
        <Badge size="xs" variant="default" radius="sm" tt="none">
          {`${t('audit.origin')}: ${item.origin ?? ''}`}
        </Badge>
      </Group>
    </div>
  );
}

/**
 * Літеральні ключі для сторожа каталогу. Наявні тексти «Row added» / «Row deleted» (ru/kz у каталозі) — ті самі
 * слова, що й тут; окремих ключів інспектора не заводимо. Невідома дія — сирий код.
 */
function rowActionLabel(action: string): string {
  switch (action) {
    case 'RowAdded':
      return t('permits.page.row.added');
    case 'RowDeleted':
      return t('permits.page.row.deleted');
    default:
      return action;
  }
}

/** Імʼя виконавця: людина - імʼя, система - локалізований підпис. */
function actorOf(item: SheetActivityItem): string {
  return item.byDisplayName === 'system' ? t('audit.systemAuthor') : item.byDisplayName;
}

function CellEditRow({
  item,
  sheetCode,
  onOpenCell,
  month,
}: {
  item: SheetActivityItem;
  sheetCode: string;
  onOpenCell: ((target: SheetLogTarget) => void) | undefined;
  month: JSX.Element | null;
}): JSX.Element {
  const rowKey = item.rowKey ?? '';
  const columnCode = item.columnCode ?? '';
  const tableCode = item.tableCode ?? '';
  const where = [
    localized(item.tableNameL10n),
    // ✎ DOCUX (огляд UI 27.11): рядок без мітки — «Row N» (як рядок формули), не ключ «p1-T3_00-0020».
    rowText({
      rowKey,
      rowLabelL10n: item.rowLabelL10n ?? null,
      rowOrdinal: item.rowOrdinal ?? null,
      rowAddedByUser: item.rowAddedByUser ?? null,
    }),
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
      {month}
      <Text size="xs" c="dimmed" style={{ overflowWrap: 'anywhere' }} data-inspector-cell-where="">
        {where}
      </Text>
      <div className="ecr-insp-diff">
        {item.oldValue === null ? (
          <Text span c="dimmed">—</Text>
        ) : (
          <s title={item.oldValue} data-inspector-old="">
            {auditValueText(item.oldValue, item.columnDataType, item.columnScale, item.oldValueLabelL10n)}
          </s>
        )}
        <svg width={14} height={14} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={1.75} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" focusable="false">
          <path d="M5 12h14M13 6l6 6-6 6" />
        </svg>
        {/* ⛔ Значення — за правилом показу колонки (`auditValueText`, як вкладка Audit і сітка), а не у форматі сховища
            (`437.6311000000000000`); точне значення лишається в `title`. Нечислові колонки — без змін. */}
        <span title={item.newValue ?? undefined} data-inspector-new="">
          {item.newValue === null ? '—' : auditValueText(item.newValue, item.columnDataType, item.columnScale, item.newValueLabelL10n)}
        </span>
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

function WorkflowRow({ item, month }: { item: SheetActivityItem; month: JSX.Element | null }): JSX.Element {
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
      {month}
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

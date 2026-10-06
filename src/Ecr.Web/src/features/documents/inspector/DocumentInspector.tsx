import { useEffect, useMemo, useRef, type JSX, type KeyboardEvent, type RefObject } from 'react';
import { ActionIcon, Badge, Box, Button, Group, Tabs, Text } from '@mantine/core';
import type { DocumentTableDto, ValidationFindingDto } from '@/api/types';
import { useCellChanges } from '@/features/audit/api';
import { useTableStatus } from '@/features/documents/api';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { KeyValue, type KeyValueItem } from '@/shared/ui/KeyValue';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { Timestamp } from '@/shared/ui/Timestamp';
import { useUrlState } from '@/shared/ui/useUrlState';
import { ruleLabel } from '@/features/documents/ruleLabel';
import { useInspectedCell, type InspectedCell } from './inspectedCell';
import {
  addressChip,
  countIssues,
  countOrDash,
  groupIssues,
  tableTitleOf,
  type InspectorIssueGroup,
} from './inspectorModel';
import './inspector.css';

/**
 * Інспектор документа справа: вкладки Issues / History / Info (`UI-25`).
 *
 * Макет: `docs/design/hybrid/screen-document.js` (`#doc-insp`, `renderInspector`,
 * `openInspector`/`closeInspector`), KIT.md §4 (`.aside` 320px, ЗАКРИТИЙ за
 * замовчуванням). Поведінка — `DIRECTIVE-15-FRONTEND.md`, `D15-16`.
 *
 * ⛔ Не `DetailDrawer`: Mantine `Drawer` зашиває `role="dialog"`/`aria-modal`,
 * а інспектор — немодальна колонка поруч із сіткою (правило `L2`:
 * закритий — `queryByRole('complementary') === null`, відкритий — `<aside>`).
 * Стан — у `?panel=issues|history|info` (та сама адреса, що в макеті), тож
 * відкритий інспектор переживає перезавантаження й надсилається посиланням.
 *
 * ⛔ Фокус: відкриття ставить його на активну вкладку, закриття (`Esc` або
 * хрестик) повертає тому, хто відкривав (кнопка «K issues», «History»,
 * «Validate»); якщо того вже немає в DOM — на кнопку зауважень.
 */
export type InspectorTab = 'issues' | 'history' | 'info';

const Tabs3: readonly InspectorTab[] = ['issues', 'history', 'info'];

/** Макет: на ≤ 1180px інспектор — шар поверх сітки й закривається після переходу. */
const NarrowQuery = '(max-width: 1180px)';

const PanelParam = 'panel';

function asTab(value: string | null): InspectorTab | null {
  return Tabs3.includes(value as InspectorTab) ? (value as InspectorTab) : null;
}

export interface DocumentInspectorProps {
  readonly documentId: number;
  readonly periodKey: number;
  /** Структура документа з `GET …/tables` — ЛИШЕ видимі людині таблиці. */
  readonly tables: readonly DocumentTableDto[];
  /** Зауваження останньої перевірки; `null` — не перевіряли. */
  readonly messages: readonly ValidationFindingDto[] | null;
  /** Перехід до адреси зауваження (`ФВ-5.6`) — та сама дія, що й у сторінки. */
  readonly onSelectFinding: (finding: ValidationFindingDto) => void;
  /**
   * Лічильник свіжих перевірок: щойно він зростає і зауваження є, Issues
   * відкривається сам (макет: `runValidate` → `openInspector('issues')`).
   */
  readonly validatedSeq?: number | undefined;
}

export function DocumentInspector({
  documentId,
  periodKey,
  tables,
  messages,
  onSelectFinding,
  validatedSeq,
}: DocumentInspectorProps): JSX.Element {
  const [panel, setPanel] = useUrlState(PanelParam);
  const tab = asTab(panel);
  const opened = tab !== null;

  const issuesButton = useRef<HTMLButtonElement>(null);
  const opener = useRef<HTMLElement | null>(null);
  const aside = useRef<HTMLElement>(null);
  const focusTabOnOpen = useRef(false);

  const groups = useMemo(() => (messages === null ? null : groupIssues(messages, tables)), [messages, tables]);
  const counts = groups === null ? null : countIssues(groups);

  const open = (next: InspectorTab): void => {
    const active = document.activeElement;
    if (active instanceof HTMLElement && aside.current?.contains(active) !== true) opener.current = active;
    focusTabOnOpen.current = true;
    setPanel(next);
  };

  const close = (refocus: boolean): void => {
    setPanel(null);
    if (!refocus) return;

    const target = opener.current?.isConnected === true ? opener.current : issuesButton.current;
    // ⚠ Після розмонтування `<aside>`: інакше фокус лишився б на зниклому вузлі.
    window.setTimeout(() => target?.focus(), 0);
  };

  const toggle = (next: InspectorTab): void => {
    if (tab === next) close(true);
    else open(next);
  };

  // Фокус на активну вкладку щойно інспектор відкрився з кнопки (не з адреси при
  // завантаженні: тоді фокус лишається там, куди його поставила навігація).
  useEffect(() => {
    if (!opened || !focusTabOnOpen.current) return;
    focusTabOnOpen.current = false;
    aside.current?.querySelector<HTMLElement>('[role="tab"][aria-selected="true"]')?.focus();
  }, [opened, tab]);

  // Макет: робоча область стискається на широкому екрані (див. `inspector.css`).
  useEffect(() => {
    if (!opened) return undefined;
    document.body.setAttribute('data-inspector-open', '');

    return () => {
      document.body.removeAttribute('data-inspector-open');
    };
  }, [opened]);

  // Свіжа перевірка з зауваженнями відкриває Issues (макет `runValidate`).
  const lastSeq = useRef(validatedSeq);
  useEffect(() => {
    if (validatedSeq === lastSeq.current) return;
    lastSeq.current = validatedSeq;
    if (counts !== null && counts.all > 0 && tab !== 'issues') open('issues');
  }, [validatedSeq, counts?.all]);

  const onKeyDown = (event: KeyboardEvent<HTMLElement>): void => {
    if (event.key === 'Escape') {
      event.stopPropagation();
      close(true);
    }
  };

  const cell = useInspectedCell();
  const cellTable =
    cell === null || cell.periodKey !== periodKey
      ? undefined
      : tables.find((table) => table.tableInstanceId === cell.tableInstanceId);
  // ⛔ Комірка іншого документа, періоду чи таблиці, якої тут немає, — не ця комірка.
  const shownCell = cellTable === undefined ? null : cell;

  return (
    <>
      <InspectorTriggers
        counts={counts}
        tab={tab}
        issuesButton={issuesButton}
        onIssues={() => toggle('issues')}
        onHistory={() => toggle('history')}
      />

      {opened && (
        <aside ref={aside} className="ecr-insp" aria-label={t('inspector.title')} onKeyDown={onKeyDown} data-inspector={tab}>
          <div className="ecr-insp-head">
            <h2>
              {t('inspector.title')}
              {shownCell !== null && (
                <span className="ecr-insp-chip" data-inspector-address="">
                  {addressChip(shownCell.rowKey, shownCell.columnCode)}
                </span>
              )}
            </h2>
            <ActionIcon size="sm" variant="subtle" color="gray" aria-label={t('inspector.close')} title={t('inspector.close')} onClick={() => close(true)}>
              <Glyph d="M6 6l12 12M18 6L6 18" />
            </ActionIcon>
          </div>

          <Tabs
            className="ecr-insp-tabs"
            value={tab}
            onChange={(value) => {
              const next = asTab(value);
              if (next !== null) setPanel(next);
            }}
            keepMounted={false}
          >
            <Tabs.List aria-label={t('inspector.tabsLabel')}>
              <Tabs.Tab value="issues">
                <Group gap="xs" wrap="nowrap">
                  {t('inspector.tabIssues')}
                  {counts !== null && counts.all > 0 && (
                    <Badge size="xs" variant="light" color={counts.errors > 0 ? 'statusError' : 'statusWarning'} data-inspector-count="">
                      {counts.all}
                    </Badge>
                  )}
                </Group>
              </Tabs.Tab>
              <Tabs.Tab value="history">{t('inspector.tabHistory')}</Tabs.Tab>
              <Tabs.Tab value="info">{t('inspector.tabInfo')}</Tabs.Tab>
            </Tabs.List>

            <Tabs.Panel value="issues" className="ecr-insp-body">
              <IssuesTab
                groups={groups}
                onSelect={(finding) => {
                  onSelectFinding(finding);
                  if (window.matchMedia?.(NarrowQuery).matches === true) close(false);
                }}
              />
            </Tabs.Panel>
            <Tabs.Panel value="history" className="ecr-insp-body">
              <HistoryTab documentId={documentId} periodKey={periodKey} cell={shownCell} />
            </Tabs.Panel>
            <Tabs.Panel value="info" className="ecr-insp-body">
              <InfoTab documentId={documentId} periodKey={periodKey} cell={shownCell} table={cellTable} />
            </Tabs.Panel>
          </Tabs>
        </aside>
      )}
    </>
  );
}

/** Кнопки над сітками: «K issues» і «History» (макет: `#doc-issues-btn`, `#doc-history-btn`). */
function InspectorTriggers({
  counts,
  tab,
  issuesButton,
  onIssues,
  onHistory,
}: {
  counts: ReturnType<typeof countIssues> | null;
  tab: InspectorTab | null;
  issuesButton: RefObject<HTMLButtonElement | null>;
  onIssues: () => void;
  onHistory: () => void;
}): JSX.Element {
  const tone = counts === null || counts.all === 0 ? 'gray' : counts.errors > 0 ? 'statusError' : 'statusWarning';
  const label =
    counts === null
      ? t('inspector.tabIssues')
      : counts.all === 0
        ? t('inspector.noIssues')
        : t('inspector.issuesCount', { count: counts.all });

  return (
    <Group gap="xs" justify="flex-end" data-inspector-triggers="">
      <Button
        ref={issuesButton}
        size="xs"
        variant={tab === 'issues' ? 'light' : 'subtle'}
        color={tone}
        aria-pressed={tab === 'issues'}
        title={
          counts === null
            ? t('inspector.notValidatedTitle')
            : t('inspector.issuesTitle', { errors: counts.errors, warnings: counts.warnings })
        }
        leftSection={
          <Glyph
            d={
              counts !== null && counts.all === 0
                ? 'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18zM8 12.5l3 3 5-6'
                : 'M12 4l9 16H3zM12 10v4M12 17v.5'
            }
          />
        }
        onClick={onIssues}
        data-inspector-issues={counts === null ? 'none' : String(counts.all)}
      >
        {label}
      </Button>
      <ActionIcon
        size="sm"
        variant={tab === 'history' ? 'light' : 'subtle'}
        color="gray"
        aria-pressed={tab === 'history'}
        aria-label={t('inspector.historyButton')}
        title={t('inspector.historyButton')}
        onClick={onHistory}
      >
        <Glyph d="M3.5 12a8.5 8.5 0 1 0 2.8-6.3M3 4v5h5M12 8v4l3 2" />
      </ActionIcon>
    </Group>
  );
}

function IssuesTab({
  groups,
  onSelect,
}: {
  groups: readonly InspectorIssueGroup[] | null;
  onSelect: (finding: ValidationFindingDto) => void;
}): JSX.Element {
  if (groups === null) {
    return <Empty title={t('inspector.notValidatedTitle')} hint={t('inspector.notValidatedHint')} />;
  }

  if (groups.length === 0) {
    return <Empty title={t('inspector.noIssuesTitle')} hint={t('inspector.noIssuesHint')} />;
  }

  return (
    <div data-inspector-issues-list="">
      {groups.map((group) => (
        <section key={group.tableDefId} aria-label={group.title} data-inspector-group={group.tableDefId}>
          <h3 className="ecr-insp-group">
            <span>{group.title}</span>
            <span>{group.issues.length}</span>
          </h3>
          {/* ⚠ Кожне зауваження тут — з видимої таблиці (`groupIssues`), тож
              кожне веде в клітинку: кнопка, а не рядок, — фокус із клавіатури. */}
          {group.issues.map(({ finding, index }) => (
              <button
                key={index}
                type="button"
                className="ecr-insp-issue"
                data-severity={finding.severity}
                title={t('document.validationGoTo')}
                onClick={() => onSelect(finding)}
              >
                <span className="ecr-insp-sev">
                  <Glyph
                    size={16}
                    d={
                      finding.severity === 'Error'
                        ? 'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18zM12 7.5v5.5M12 16v.5'
                        : 'M12 4l9 16H3zM12 10v4M12 17v.5'
                    }
                  />
                </span>
                <span>{finding.message}</span>
                <span className="ecr-insp-where">
                  <span className="ecr-insp-chip">{addressChip(finding.rowKey, finding.columnCode)}</span>
                  <span title={finding.ruleCode}>{finding.displayCode ?? ruleLabel(finding.ruleCode)}</span>
                  <StatusBadge kind="severity" state={finding.severity} quiet />
                </span>
              </button>
          ))}
        </section>
      ))}
    </div>
  );
}

/** Календарна дата `YYYY-MM-DD` у поясі браузера (вікно журналу — `features/audit/api.ts`). */
function dateOnly(at: Date): string {
  const pad = (value: number): string => String(value).padStart(2, '0');

  return `${String(at.getFullYear())}-${pad(at.getMonth() + 1)}-${pad(at.getDate())}`;
}

/**
 * Вікно історії однієї комірки: останні 12 місяців. ⚠ Сервер дозволяє для
 * адреси однієї комірки до 396 діб (`GetCellChangesHandler.MaxCellWindow`);
 * 365 + день «до» включно — з запасом.
 */
function historyWindow(): { from: string; to: string } {
  const today = new Date();
  const from = new Date(today.getFullYear() - 1, today.getMonth(), today.getDate());

  return { from: dateOnly(from), to: dateOnly(today) };
}

function HistoryTab({
  documentId,
  periodKey,
  cell,
}: {
  documentId: number;
  periodKey: number;
  cell: InspectedCell | null;
}): JSX.Element {
  const window12 = useMemo(historyWindow, []);
  const history = useCellChanges(
    {
      ...window12,
      documentId,
      rowKey: cell?.rowKey ?? null,
      columnDefId: cell?.columnDefId ?? null,
      limit: 50,
    },
    cell !== null,
  );

  if (cell === null) {
    return <Empty title={t('inspector.noCellTitle')} hint={t('inspector.noCellHint')} />;
  }

  if (history.error !== null) {
    return (
      <Box p="sm">
        <ErrorAlert error={history.error} onRetry={() => void history.refetch()} />
      </Box>
    );
  }

  if (history.data === undefined) {
    return (
      <Text size="sm" c="dimmed" p="sm" role="status">
        {t('inspector.loading')}
      </Text>
    );
  }

  // ⚠ Журнал адресує комірку ДОКУМЕНТА; звітний період — окрема вісь (`R-A6`).
  const items = history.data.items.filter((change) => change.periodKey === periodKey);

  if (items.length === 0) {
    return <Empty title={t('inspector.noHistoryTitle')} hint={t('inspector.noHistoryHint')} />;
  }

  return (
    <div role="list" data-inspector-history="">
      {items.map((change) => (
        <div key={`${change.changedAt}:${String(change.changedByUserId)}:${change.newValue ?? ''}`} role="listitem" className="ecr-insp-hist">
          <Group justify="space-between" gap="xs" wrap="nowrap">
            <Text size="sm" fw={500}>
              {change.changedByDisplayName ?? t('inspector.unknownAuthor', { id: change.changedByUserId })}
            </Text>
            <Text size="xs" c="dimmed">
              <Timestamp value={change.changedAt} />
            </Text>
          </Group>
          <div className="ecr-insp-diff">
            {change.oldValue === null ? <Text span c="dimmed">—</Text> : <s>{change.oldValue}</s>}
            <Glyph size={14} d="M5 12h14M13 6l6 6-6 6" />
            <span>{change.newValue ?? '—'}</span>
          </div>
          <Group gap="xs">
            <Badge size="xs" variant="default" radius="sm" tt="none">
              {`${t('audit.origin')}: ${change.origin}`}
            </Badge>
            {change.isLateEdit && (
              <Badge size="xs" variant="light" color="statusWarning" radius="sm" tt="none">
                {t('inspector.lateEdit')}
              </Badge>
            )}
          </Group>
        </div>
      ))}
    </div>
  );
}

function InfoTab({
  documentId,
  periodKey,
  cell,
  table,
}: {
  documentId: number;
  periodKey: number;
  cell: InspectedCell | null;
  table: DocumentTableDto | undefined;
}): JSX.Element {
  // ⚠ Той самий ключ запиту, що в `SheetFillSummary`: зайвого запиту немає.
  const status = useTableStatus(documentId, periodKey);

  if (cell === null || table === undefined) {
    return <Empty title={t('inspector.noCellTitle')} hint={t('inspector.noCellHint')} />;
  }

  const tableStatus = status.data?.find((row) => row.tableDefId === table.tableDefId);

  const items: KeyValueItem[] = [
    { label: t('inspector.info.cell'), value: addressChip(cell.rowKey, cell.columnCode), mono: true },
    { label: t('inspector.info.row'), value: cell.rowLabel },
    {
      label: t('inspector.info.column'),
      value: cell.unitSymbol === null ? cell.columnHeader : `${cell.columnHeader}, ${cell.unitSymbol}`,
    },
    {
      label: t('inspector.info.type'),
      value: cell.scale === null ? cell.dataType : `${cell.dataType} (${String(cell.scale)})`,
      mono: true,
    },
    {
      label: t('inspector.info.input'),
      value: cell.isCalculated
        ? t('inspector.info.inputCalculated')
        : cell.isReadOnly
          ? t('inspector.info.inputReadOnly')
          : t('inspector.info.inputManual'),
    },
    { label: t('inspector.info.value'), value: cell.value.length === 0 ? null : cell.value, mono: true },
    { label: t('inspector.info.table'), value: tableTitleOf(table) },
    {
      label: t('inspector.info.tableIssues'),
      // ⛔ `null` — «не перевіряли»/«приховано», і це «—», а не 0.
      value: t('inspector.info.tableIssuesValue', {
        errors: countOrDash(tableStatus?.errorCount),
        warnings: countOrDash(tableStatus?.warningCount),
      }),
    },
  ];

  return (
    <Box p="sm" data-inspector-info="">
      <KeyValue items={items} />
    </Box>
  );
}

function Empty({ title, hint }: { title: string; hint: string }): JSX.Element {
  return (
    <div className="ecr-insp-empty">
      <b>{title}</b>
      <span>{hint}</span>
    </div>
  );
}

/** Лінійна піктограма макета (`kit.js` `E.icon`); декоративна — підпис дає кнопка. */
function Glyph({ d, size = 16 }: { d: string; size?: number }): JSX.Element {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.75}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      <path d={d} />
    </svg>
  );
}

import { useMemo, useRef, useState, type JSX, type KeyboardEvent } from 'react';
import { Progress } from '@mantine/core';
import type { DocumentTableDto } from '@/api/types';
import { localized } from '@/shared/i18n/localized';
import { t } from '@/shared/i18n';
import {
  countText,
  filterTableTree,
  sheetProgress,
  type TableFillState,
  type TableTreeItem,
} from './tableTreeModel';
import './tableNavigator.css';

export interface TableNavigatorProps {
  /** `id` панелі — на нього посилається перемикач (`aria-controls`). */
  readonly id: string;
  /** Усі таблиці аркуша зі станами, у порядку шаблону. */
  readonly items: readonly TableTreeItem[];
  readonly selectedTableInstanceId: number | undefined;
  /** Людина вибрала таблицю (клік, Enter, пробіл). */
  readonly onSelect: (tableInstanceId: number) => void;
  readonly hidden?: boolean;
}

/** Назва таблиці мовою інтерфейсу; без перекладу — код. */
export function tableName(table: DocumentTableDto): string {
  return localized(table.tableNameL10n) || table.tableCode;
}

/**
 * Дерево таблиць аркуша (`UI-22`, макет `docs/design/hybrid/screen-document.js`, `renderTree`).
 *
 * ⚠ Дерево ПЛАСКЕ: груп «1 Stationary combustion → 1.1 …» у `DocumentTableDto` немає (D15-06),
 * тож рядок — таблиця: крапка стану, номер, назва, лічильник помилок.
 *
 * ⛔ Клавіатура — шаблон WAI-ARIA «tree view»: у порядку Tab дерево — ОДНА зупинка (рухомий
 * `tabIndex`), стрілки/Home/End ходять рядками, Enter/пробіл вибирають. Інакше 91 рядок став би
 * 91 зупинкою між шапкою документа і сіткою — і прохід клавіатурою (`e2e/keyboardPath.spec.ts`)
 * перетворився б на 91 натискання Tab.
 *
 * ⚠ Стан — не лише кольором: кожен рядок має текст стану в `aria-label` і в `title`.
 */
export function TableNavigator({
  id,
  items,
  selectedTableInstanceId,
  onSelect,
  hidden = false,
}: TableNavigatorProps): JSX.Element {
  const [query, setQuery] = useState('');
  const [errorsOnly, setErrorsOnly] = useState(false);
  // Рядок, який тримає зупинку Tab, коли людина походила стрілками (ще не вибрала).
  const [activeId, setActiveId] = useState<number | null>(null);
  const tree = useRef<HTMLDivElement>(null);

  const shown = useMemo(
    () => filterTableTree(items, { query, errorsOnly }, tableName),
    [items, query, errorsOnly],
  );
  const progress = useMemo(() => sheetProgress(items), [items]);
  const anyStatus = items.some((item) => item.state !== 'unknown');
  // ⚠ `null` — лічильників немає (не перевіряли / роль їх не бачить): рядка зауважень немає.
  const issues =
    progress.errors === null && progress.warnings === null ? null : (progress.errors ?? 0) + (progress.warnings ?? 0);

  // ⚠ Зупинка Tab: рядок, куди людина дійшла стрілками, інакше вибрана таблиця, інакше перша
  // показана. Вибрана, відсічена фільтром, зупинку не тримає — інакше Tab вів би в нікуди.
  const tabStop =
    shown.find((item) => item.table.tableInstanceId === activeId)?.table.tableInstanceId ??
    shown.find((item) => item.table.tableInstanceId === selectedTableInstanceId)?.table.tableInstanceId ??
    shown[0]?.table.tableInstanceId;

  const focusItem = (tableInstanceId: number): void => {
    setActiveId(tableInstanceId);
    tree.current?.querySelector<HTMLElement>(`[data-tree-table="${String(tableInstanceId)}"]`)?.focus();
  };

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>, index: number): void => {
    const move = (to: number): void => {
      const target = shown[Math.max(0, Math.min(shown.length - 1, to))];
      if (target === undefined) return;
      event.preventDefault();
      focusItem(target.table.tableInstanceId);
    };

    switch (event.key) {
      case 'ArrowDown':
        move(index + 1);
        break;
      case 'ArrowUp':
        move(index - 1);
        break;
      case 'Home':
        move(0);
        break;
      case 'End':
        move(shown.length - 1);
        break;
      case 'Enter':
      case ' ': {
        const item = shown[index];
        if (item === undefined) return;
        event.preventDefault();
        onSelect(item.table.tableInstanceId);
        break;
      }
      default:
        break;
    }
  };

  const clearFilter = (): void => {
    setQuery('');
    setErrorsOnly(false);
  };

  return (
    <nav id={id} className="ecr-tnav" aria-label={t('grid.tree.label')} hidden={hidden} data-testid="table-navigator">
      <div className="ecr-tnav-progress">
        {/* ⛔ Доки статусу немає, смуги й дробу немає: «0 із 91» про невідоме — неправда (`A7-28`). */}
        {anyStatus && progress.total > 0 && (
          <>
            <Progress
              size={4}
              radius="xl"
              value={(progress.filled / progress.total) * 100}
              aria-label={t('grid.tree.tablesFilled', { filled: progress.filled, total: progress.total })}
            />
            <div className="ecr-tnav-progress-line" data-testid="table-navigator-progress">
              <span>{t('grid.tree.tablesFilled', { filled: progress.filled, total: progress.total })}</span>
              {issues !== null && issues > 0 && (
                <>
                  <span aria-hidden="true">·</span>
                  <span className={(progress.errors ?? 0) > 0 ? 'ecr-tnav-bad' : 'ecr-tnav-warn'}>
                    {t('grid.tree.issues', { count: issues })}
                  </span>
                </>
              )}
            </div>
          </>
        )}
      </div>

      <div className="ecr-tnav-tools">
        <div className="ecr-tnav-search">
          <Icon path="M11 18a7 7 0 1 0 0-14 7 7 0 0 0 0 14zM20 20l-4-4" size={14} />
          <input
            type="search"
            value={query}
            placeholder={t('grid.tree.filter')}
            aria-label={t('grid.tree.filter')}
            onChange={(event) => {
              setQuery(event.currentTarget.value);
              setActiveId(null);
            }}
          />
        </div>
        <button
          type="button"
          className="ecr-icon-btn"
          aria-pressed={errorsOnly}
          aria-label={t('grid.tree.errorsOnly')}
          title={t('grid.tree.errorsOnly')}
          onClick={() => {
            setErrorsOnly((value) => !value);
            setActiveId(null);
          }}
        >
          <Icon path="M12 4l9 16H3zM12 10v4M12 17v.5" />
        </button>
      </div>

      {shown.length === 0 ? (
        <div className="ecr-tnav-empty" data-testid="table-navigator-empty">
          <span>{t('grid.tree.nothingFound')}</span>
          <button type="button" onClick={clearFilter}>
            {t('grid.tree.clearFilter')}
          </button>
        </div>
      ) : (
        <div ref={tree} className="ecr-tnav-tree" role="tree" aria-label={t('grid.tree.label')}>
          {shown.map((item, index) => {
            const tableId = item.table.tableInstanceId;
            const name = tableName(item.table);
            const state = stateText(item);
            const selected = tableId === selectedTableInstanceId;

            return (
              <div
                key={tableId}
                role="treeitem"
                aria-level={1}
                aria-selected={selected}
                aria-label={`${String(item.table.tableOrdinal)} ${name}, ${state}`}
                title={`${String(item.table.tableOrdinal)} ${name} · ${item.table.tableCode} — ${state}`}
                tabIndex={tableId === tabStop ? 0 : -1}
                className="ecr-tnav-item"
                data-tree-table={tableId}
                data-state={item.state}
                onClick={() => {
                  setActiveId(tableId);
                  onSelect(tableId);
                }}
                onKeyDown={(event) => {
                  onKeyDown(event, index);
                }}
              >
                <i className="ecr-dot" data-state={item.state} aria-hidden="true" />
                <span className="ecr-tnav-no" aria-hidden="true">
                  {item.table.tableOrdinal}
                </span>
                <span className="ecr-tnav-name" aria-hidden="true">
                  {name}
                </span>
                {item.errors !== null && item.errors > 0 ? (
                  <span className="ecr-tnav-count" aria-hidden="true">
                    {item.errors}
                  </span>
                ) : (
                  item.warnings !== null &&
                  item.warnings > 0 && (
                    <span className="ecr-tnav-count ecr-tnav-count-warn" aria-hidden="true">
                      {item.warnings}
                    </span>
                  )
                )}
              </div>
            );
          })}
        </div>
      )}
    </nav>
  );
}

/** Стан таблиці словами — для читалки й підказки; колір лише дублює його. */
export function stateText(item: TableTreeItem): string {
  const words: Record<TableFillState, () => string> = {
    unknown: () => t('grid.tree.state.unknown'),
    none: () => t('grid.tree.state.none'),
    empty: () => t('grid.tree.state.empty'),
    partial: () =>
      t('grid.tree.state.partial', { filled: item.filledCells ?? 0, total: item.inputCells ?? 0 }),
    filled: () => t('grid.tree.state.filled'),
    error: () => t('grid.tree.state.error', { count: item.errors ?? 0 }),
  };

  const base = words[item.state]();
  if (item.state === 'error') return base;

  // ⛔ Невідомий лічильник — «—», не «0» (роль зі звуженим доступом або документ не перевіряли).
  if (item.state !== 'unknown' && item.errors === null) {
    return `${base}, ${t('grid.tree.state.error', { count: countText(null) })}`;
  }

  return item.warnings !== null && item.warnings > 0
    ? `${base}, ${t('grid.tree.warnings', { count: item.warnings })}`
    : base;
}

/** Іконка контуром — набір макета (`kit.js`, `I`), без бібліотеки іконок у чанку. */
export function Icon({ path, size = 16 }: { readonly path: string; readonly size?: number }): JSX.Element {
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
      <path d={path} />
    </svg>
  );
}

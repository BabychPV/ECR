import { useCallback, useEffect, useMemo, useRef, useState, type JSX } from 'react';
import { useMediaQuery } from '@mantine/hooks';
import { useTableStatus } from '@/features/documents/api';
import { focusSoon } from '@/shared/a11y/focus';
import { t } from '@/shared/i18n';
import { useUrlState } from '@/shared/ui/useUrlState';
import { SheetTables, type SheetTablesProps } from './SheetTables';
import { Icon, TableNavigator } from './TableNavigator';
import { buildTableTree, resolveTable, tableUrlKey } from './tableTreeModel';

/** Ширина, з якої дерево — шар поверх таблиці (макет: `@media (max-width:900px)`). */
export const NarrowNavigatorQuery = '(max-width: 900px)';

/** Пам'ять «дерево сховане» між відкриттями документа (лише на широкому екрані). */
const NavigatorHiddenKey = 'ecr.tableNavigator.hidden';

const NavigatorId = 'ecr-table-navigator';

type SheetWorkspaceProps = Pick<SheetTablesProps, 'documentId' | 'periodKey' | 'readOnly' | 'tables'>;

/**
 * Робоче місце аркуша (`UI-22`): ліворуч дерево таблиць зі станами, праворуч ОДНА таблиця.
 *
 * Макет — `docs/design/hybrid/screen-document.js` (`.split > .side.doc-nav` + `.doc-work`,
 * `.tablebar` з перемикачем `panelL`), знімок `ui-adoption/mockup/02-doc-light.png`.
 *
 * ⚠ Вибрана таблиця — в адресі (`?table=<код>`, як `?sheet=`): посилання відновлює вибір.
 * `?view=all` — колишній стос усіх таблиць із лінивим монтуванням (`SheetTables`, `stack`):
 * у макеті його немає, лишений як запасний режим і для `e2e/lazyTables.spec.ts`.
 *
 * ⚠ Живе в чанку сітки (`DocumentPage` бере його через `import()`), тож до бюджету маршруту
 * (`D-132`) не додає нічого.
 */
export function SheetWorkspace({ documentId, periodKey, readOnly, tables }: SheetWorkspaceProps): JSX.Element {
  const [view] = useUrlState('view');
  const [tableKey, setTableKey] = useUrlState('table');
  const status = useTableStatus(documentId, periodKey);

  const narrow = useMediaQuery(NarrowNavigatorQuery, false, { getInitialValueInEffect: false }) === true;
  const [hiddenWide, setHiddenWide] = useState(readHidden);
  const [openNarrow, setOpenNarrow] = useState(false);
  const navigatorOpen = narrow ? openNarrow : !hiddenWide;

  const selected = resolveTable(tableKey, tables);
  const selectedId = selected?.tableInstanceId;
  const items = useMemo(() => buildTableTree(tables, status.data), [tables, status.data]);

  const work = useRef<HTMLDivElement>(null);
  // Таблиця, на заголовок якої перенести фокус після вибору в дереві (клік, Enter).
  const [focusFor, setFocusFor] = useState<number | null>(null);

  const select = useCallback(
    (tableInstanceId: number) => {
      const table = tables.find((candidate) => candidate.tableInstanceId === tableInstanceId);
      if (table === undefined) return;
      setTableKey(tableUrlKey(table, tables));
    },
    [tables, setTableKey],
  );

  useEffect(() => {
    if (focusFor === null || focusFor !== selectedId) return;
    setFocusFor(null);
    focusSoon(
      work.current?.querySelector<HTMLElement>(`[data-table-slot="${String(focusFor)}"] [data-table-title]`),
    );
  }, [focusFor, selectedId]);

  if (view === 'all') {
    return <SheetTables documentId={documentId} periodKey={periodKey} readOnly={readOnly} tables={tables} />;
  }

  const toggle = (
    <button
      type="button"
      className="ecr-icon-btn"
      aria-expanded={navigatorOpen}
      aria-controls={NavigatorId}
      aria-label={t('grid.tree.toggle')}
      title={t('grid.tree.toggle')}
      data-testid="table-navigator-toggle"
      onClick={() => {
        if (narrow) {
          setOpenNarrow((value) => !value);
          return;
        }

        setHiddenWide((value) => {
          writeHidden(!value);
          return !value;
        });
      }}
    >
      <Icon path="M4 5h16v14H4zM9 5v14" />
    </button>
  );

  return (
    <div className="ecr-doc-split">
      <TableNavigator
        id={NavigatorId}
        items={items}
        selectedTableInstanceId={selectedId}
        hidden={!navigatorOpen}
        onSelect={(tableInstanceId) => {
          select(tableInstanceId);
          setFocusFor(tableInstanceId);
          // ⚠ Вузький екран: дерево — шар поверх таблиці, після вибору він заважає.
          if (narrow) setOpenNarrow(false);
        }}
      />
      <div ref={work} className="ecr-doc-work">
        <SheetTables
          documentId={documentId}
          periodKey={periodKey}
          readOnly={readOnly}
          tables={tables}
          layout="single"
          selectedTableInstanceId={selectedId}
          onSelectTable={select}
          titleStart={toggle}
        />
      </div>
    </div>
  );
}

function readHidden(): boolean {
  try {
    return window.localStorage.getItem(NavigatorHiddenKey) === '1';
  } catch {
    return false;
  }
}

function writeHidden(hidden: boolean): void {
  try {
    if (hidden) window.localStorage.setItem(NavigatorHiddenKey, '1');
    else window.localStorage.removeItem(NavigatorHiddenKey);
  } catch {
    // Сховище недоступне (приватний режим) — дерево просто не запам'ятає стан.
  }
}

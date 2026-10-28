import { useCallback, useEffect, useMemo, useRef, useState, type JSX, type ReactNode } from 'react';
import { useMediaQuery } from '@mantine/hooks';
import { useTableStatus } from '@/features/documents/api';
import { focusSoon } from '@/shared/a11y/focus';
import { localized } from '@/shared/i18n/localized';
import { t } from '@/shared/i18n';
import { useUrlState } from '@/shared/ui/useUrlState';
import { GridHScroll } from './GridHScroll';
import { GridKeyHint } from './GridKeyHint';
import { SheetTables, type SheetTablesProps } from './SheetTables';
import { Icon, TableNavigator, tableName } from './TableNavigator';
import { buildTableTree, resolveTable, tableUrlKey } from './tableTreeModel';

/** Ширина, з якої дерево — шар поверх таблиці (макет: `@media (max-width:900px)`). */
export const NarrowNavigatorQuery = '(max-width: 900px)';

/** Пам'ять «дерево сховане» між відкриттями документа (лише на широкому екрані): `1` сховане, `0` показане, немає — дефолт. */
const NavigatorHiddenKey = 'ecr.tableNavigator.hidden';

const NavigatorId = 'ecr-table-navigator';

/** Параметр адреси режиму «на весь екран» (`&full=1`): переживає перезавантаження й посилання. */
export const FullParam = 'full';

const ExpandIcon = 'M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5';
const CollapseIcon = 'M9 4v5H4M15 4v5h5M9 20v-5H4M15 20v-5h5';

type SheetWorkspaceProps = Pick<
  SheetTablesProps,
  'documentId' | 'periodKey' | 'periodEnd' | 'readOnly' | 'sheetState' | 'tables'
> &
  BottomBarProps;

/** Дані для нижнього рядка (✎ ui-sheet-bottom): смуга аркушів + горизонтальна прокрутка таблиці в одному рядку. */
export type BottomBarRenderer = (scroll: ReactNode) => ReactNode;

interface BottomBarProps {
  /** Малює нижній рядок під таблицею; scroll — горизонтальна прокрутка таблиці для його правої частини. */
  readonly bottomBar?: BottomBarRenderer | undefined;
}

/**
 * Esc виходить з повного екрана ЛИШЕ коли нічого іншого його не очікує: редактор комірки (Esc скасовує
 * правку), відкритий шар (діалог, меню, список) і поля вводу мають власний Esc.
 */
export function shouldExitOnEscape(event: KeyboardEvent): boolean {
  if (event.key !== 'Escape' || event.defaultPrevented) return false;

  const target = event.target instanceof Element ? event.target : null;
  const own = 'input, textarea, select, [contenteditable="true"], [contenteditable=""], [data-mantine-stop-propagation]';
  if (target?.closest(own) != null) return false;

  const layers = 'revogr-edit, .edit-input-wrapper, [role="dialog"], [role="menu"], [role="listbox"]';
  return document.querySelector(layers) === null;
}

/** Ctrl+Shift+Enter (Cmd на macOS) — не використовується ні сіткою, ні браузером. */
export function isFullToggleKey(event: KeyboardEvent): boolean {
  return event.key === 'Enter' && event.shiftKey && (event.ctrlKey || event.metaKey) && !event.altKey;
}

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
 * ✎ ui-sheet-focus (A): панель має «‹ Сховати» у шапці і згортається у вузьку смужку з бейджем; на
 * аркуші з ОДНІЄЮ таблицею за замовчуванням згорнута. (B): `&full=1` — таблиця на весь екран (in-app:
 * `position: fixed`, решта сторінки `inert`; сітка НЕ перемонтовується, тож правки, Undo, виділення й
 * прокрутка зберігаються).
 *
 * ⚠ Живе в чанку сітки (`DocumentPage` бере його через `import()`), тож до бюджету маршруту
 * (`D-132`) не додає нічого.
 */
export function SheetWorkspace({
  documentId,
  periodKey,
  periodEnd = null,
  readOnly,
  sheetState,
  tables,
  bottomBar,
}: SheetWorkspaceProps): JSX.Element {
  const [view] = useUrlState('view');
  const [tableKey, setTableKey] = useUrlState('table');
  const [fullParam, setFullParam] = useUrlState(FullParam);
  const status = useTableStatus(documentId, periodKey);

  const narrow = useMediaQuery(NarrowNavigatorQuery, false, { getInitialValueInEffect: false }) === true;
  // `null` — людина стан не обирала: аркуш з однією таблицею згорнутий, решта — розгорнуті.
  const [choice, setChoice] = useState<boolean | null>(readHidden);
  const hiddenWide = choice ?? tables.length <= 1;
  const [openNarrow, setOpenNarrow] = useState(false);
  const navigatorOpen = narrow ? openNarrow : !hiddenWide;
  const full = fullParam === '1' && view !== 'all';

  const selected = resolveTable(tableKey, tables);
  const selectedId = selected?.tableInstanceId;
  const items = useMemo(() => buildTableTree(tables, status.data), [tables, status.data]);

  const work = useRef<HTMLDivElement>(null);
  // Рамка панелі таблиці (сітки + нижній рядок) і слот, на якому останнім був фокус (режим «усі таблиці»).
  const panel = useRef<HTMLDivElement>(null);
  const [focusedSlot, setFocusedSlot] = useState<string | null>(null);
  const split = useRef<HTMLDivElement>(null);
  const fullBtn = useRef<HTMLButtonElement>(null);
  // Таблиця, на заголовок якої перенести фокус після вибору в дереві (клік, Enter).
  const [focusFor, setFocusFor] = useState<number | null>(null);
  // Куди перенести фокус після згортання/розгортання панелі (кнопка, що натиснута, зникає).
  const focusAfterToggle = useRef<'rail' | 'hide' | null>(null);

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

  useEffect(() => {
    const where = focusAfterToggle.current;
    if (where === null) return;
    focusAfterToggle.current = null;
    const selector = where === 'rail' ? '[data-testid="table-navigator-rail-btn"]' : '[data-testid="table-navigator-hide"]';
    focusSoon(split.current?.querySelector<HTMLElement>(selector));
  }, [navigatorOpen]);

  const setFull = useCallback(
    (next: boolean, refocus: boolean) => {
      setFullParam(next ? '1' : null);
      if (refocus) focusSoon(fullBtn.current);
    },
    [setFullParam],
  );

  // Клавіші: вхід/вихід Ctrl+Shift+Enter (capture — до сітки), Esc — лише за `shouldExitOnEscape`.
  //
  // ⛔ Флейк e2e (Ctrl+Shift+Enter іноді «не вмикав» режим): обробник жив у ефекті із залежністю від
  // `full` і `setFull`, тож між зміною адреси й перереєстрацією слухача лишалося вікно з ЗАСТАРІЛИМ `full`,
  // а перемонтування сторінки після автозбереження давало два слухачі одночасно — другий (із протилежним
  // `full`) скасовував рішення першого в тій самій події. Тепер слухач ОДИН на `view`, стан береться з
  // актуальної адреси (не із замикання), `setFull` — через ref, а подію, яку вже обробили
  // (`defaultPrevented`), другий слухач пропускає.
  const setFullRef = useRef(setFull);
  setFullRef.current = setFull;
  const fullRef = useRef(full);
  fullRef.current = full;

  useEffect(() => {
    if (view === 'all') return;

    const fullNow = (): boolean => fullRef.current;

    const onKey = (event: KeyboardEvent): void => {
      if (event.defaultPrevented) return;

      if (isFullToggleKey(event)) {
        const target = event.target instanceof Element ? event.target : null;
        const inEditor = target?.closest('input, textarea, select, [contenteditable="true"]') != null;
        const inside = target === null || target === document.body || work.current?.contains(target) === true;
        if (inEditor || !inside) return;
        event.preventDefault();
        event.stopPropagation();
        const wasFull = fullNow();
        setFullRef.current(!wasFull, wasFull);
        return;
      }

      if (fullNow() && shouldExitOnEscape(event)) {
        event.preventDefault();
        setFullRef.current(false, true);
      }
    };

    document.addEventListener('keydown', onKey, true);
    return () => document.removeEventListener('keydown', onKey, true);
  }, [view]);

  // Повний екран: усе, що не веде до таблиці, — `inert` (клавіатура й читалка не потрапляють у приховане).
  useEffect(() => {
    if (!full) return;
    const touched: Element[] = [];
    let node: Element | null = work.current;
    while (node?.parentElement != null && node.parentElement !== document.body) {
      const parent: Element = node.parentElement;
      for (const sibling of Array.from(parent.children)) {
        if (sibling !== node && !sibling.hasAttribute('inert')) {
          sibling.setAttribute('inert', '');
          touched.push(sibling);
        }
      }
      node = parent;
    }
    document.body.setAttribute('data-sheet-full', '');

    return () => {
      for (const element of touched) element.removeAttribute('inert');
      document.body.removeAttribute('data-sheet-full');
    };
  }, [full]);

  const panelClass = bottomBar === undefined ? 'ecr-sheet-panel' : 'ecr-sheet-panel ecr-sheet-panel--bar';

  if (view === 'all') {
    // Кілька таблиць: нижній рядок один, під останньою; прокрутка належить таблиці, де останнім був фокус.
    const hostOf = (): HTMLElement | null =>
      panel.current?.querySelector<HTMLElement>(focusedSlot === null ? '[data-table-mounted="true"]' : `[data-table-slot="${focusedSlot}"]`) ?? null;
    return (
      <>
        <GridKeyHint readOnly={readOnly} />
        <div
          ref={panel}
          className={panelClass}
          onFocusCapture={(event) => {
            const slot = (event.target as HTMLElement).closest('[data-table-slot]')?.getAttribute('data-table-slot');
            if (slot !== null && slot !== undefined) setFocusedSlot(slot);
          }}
        >
          <SheetTables
            documentId={documentId}
            periodKey={periodKey}
            periodEnd={periodEnd}
            readOnly={readOnly}
            sheetState={sheetState}
            tables={tables}
          />
          {bottomBar?.(<GridHScroll resolveHost={hostOf} container={() => panel.current} watch={focusedSlot} />)}
        </div>
      </>
    );
  }

  const toggleNavigator = (): void => {
    if (narrow) {
      setOpenNarrow((value) => !value);
      return;
    }

    focusAfterToggle.current = hiddenWide ? 'hide' : 'rail';
    const nextHidden = !hiddenWide;
    writeHidden(nextHidden);
    setChoice(nextHidden);
  };

  const toggle = (
    <button
      type="button"
      className="ecr-icon-btn"
      aria-expanded={navigatorOpen}
      aria-controls={NavigatorId}
      aria-label={t('grid.tree.toggle')}
      title={t('grid.tree.toggle')}
      data-testid="table-navigator-toggle"
      onClick={toggleNavigator}
    >
      <Icon path="M4 5h16v14H4zM9 5v14" />
    </button>
  );

  const fullLabel = t(full ? 'grid.full.exit' : 'grid.full.enter');
  const fullButton = (
    <button
      ref={fullBtn}
      type="button"
      className="ecr-full-btn"
      aria-label={fullLabel}
      title={`${fullLabel} (Ctrl+Shift+Enter)`}
      data-testid="sheet-full-toggle"
      data-full={full ? 'true' : 'false'}
      onClick={() => {
        setFull(!full, false);
      }}
    >
      <Icon path={full ? CollapseIcon : ExpandIcon} size={16} />
      {full && <span>{fullLabel}</span>}
    </button>
  );

  const sheetName = localized(selected?.sheetNameL10n ?? tables[0]?.sheetNameL10n ?? { values: {} });
  const periodText = `${String(periodKey).slice(0, 4)}-${String(periodKey).slice(4)}`;

  return (
    <div ref={split} className="ecr-doc-split">
      <TableNavigator
        id={NavigatorId}
        items={items}
        selectedTableInstanceId={selectedId}
        hidden={!navigatorOpen || full}
        onCollapse={toggleNavigator}
        onSelect={(tableInstanceId) => {
          select(tableInstanceId);
          setFocusFor(tableInstanceId);
          // ⚠ Вузький екран: дерево — шар поверх таблиці, після вибору він заважає.
          if (narrow) setOpenNarrow(false);
        }}
      />
      {!navigatorOpen && !narrow && !full && (
        <div className="ecr-tnav-rail" data-testid="table-navigator-rail">
          <button
            type="button"
            className="ecr-tnav-rail-btn"
            aria-expanded={false}
            aria-controls={NavigatorId}
            aria-label={`${t('grid.tree.show')} (${String(tables.length)})`}
            title={t('grid.tree.show')}
            data-testid="table-navigator-rail-btn"
            onClick={toggleNavigator}
          >
            <Icon path="M9 6l6 6-6 6" size={16} />
          </button>
          <span className="ecr-tnav-rail-count" aria-hidden="true" data-testid="table-navigator-rail-count">
            {tables.length}
          </span>
        </div>
      )}
      <div
        ref={work}
        className={full ? 'ecr-doc-work ecr-doc-work--full' : 'ecr-doc-work'}
        role={full ? 'region' : undefined}
        aria-label={full && selected !== undefined ? tableName(selected) : undefined}
        data-sheet-full={full ? '' : undefined}
      >
        <div ref={panel} className={panelClass}>
          <SheetTables
            documentId={documentId}
            periodKey={periodKey}
            periodEnd={periodEnd}
            readOnly={readOnly}
            sheetState={sheetState}
            tables={tables}
            layout="single"
            fullScreen={full}
            selectedTableInstanceId={selectedId}
            onSelectTable={select}
            titleStart={full ? undefined : toggle}
            // `UI-41`: видима підказка клавіш праворуч у рядку таблиці (у макеті — `aria-label` сітки
            // і рядок стану внизу; під сіткою висотою 70vh її не було б видно без прокрутки).
            titleEnd={
              <>
                {full && (
                  <span className="ecr-full-context" data-testid="sheet-full-context">
                    {sheetName} · {periodText}
                  </span>
                )}
                <GridKeyHint readOnly={readOnly} />
                {fullButton}
              </>
            }
          />
          {bottomBar?.(
            <GridHScroll
              resolveHost={() => panel.current?.querySelector<HTMLElement>('[data-table-slot][data-table-selected="true"]') ?? null}
              container={() => panel.current}
              watch={selectedId}
            />,
          )}
        </div>
      </div>
    </div>
  );
}

function readHidden(): boolean | null {
  try {
    const value = window.localStorage.getItem(NavigatorHiddenKey);
    if (value === '1') return true;
    if (value === '0') return false;
    return null;
  } catch {
    return null;
  }
}

function writeHidden(hidden: boolean): void {
  try {
    window.localStorage.setItem(NavigatorHiddenKey, hidden ? '1' : '0');
  } catch {
    // Сховище недоступне (приватний режим) — дерево просто не запам'ятає стан.
  }
}

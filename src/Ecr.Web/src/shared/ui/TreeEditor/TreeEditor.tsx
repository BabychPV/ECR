import {
  useCallback,
  useEffect,
  useId,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type JSX,
  type KeyboardEvent,
  type ReactNode,
} from 'react';
import { t } from '@/shared/i18n';
import { ConfirmModal } from '../ConfirmModal';
import { useDebouncedFilter } from '../useDebouncedFilter';
import { TreeMoveDialog } from './TreeMoveDialog';
import {
  ancestorsOf,
  buildTreeIndex,
  childLevelsFor,
  isTypeaheadKey,
  resolveTreeKey,
  rootsOf,
  splitMatch,
  typeaheadTarget,
  visibleRows,
  type TreeLevel,
  type TreeNode,
  type TreeNodeId,
} from './treeModel';
import './treeEditor.css';

/** Запит на зміну місця вузла (джерело-агностичний; адаптер перекладає його у свій виклик). */
export interface TreeMoveRequest {
  readonly id: TreeNodeId;
  /** Новий батько; `null` - корінь. */
  readonly parentId: TreeNodeId | null;
  /** Стати перед цим сусідом; `null` - останнім. */
  readonly beforeId: TreeNodeId | null;
}

/** Дії над вибраним вузлом - для панелі вузла та інших кнопок поза деревом. */
export interface TreeNodeActions {
  readonly canMove: boolean;
  readonly canRetire: boolean;
  /** Рівні, на яких можна додати дитину (порожньо - додавання недоступне). */
  readonly childLevels: readonly TreeLevel[];
  readonly move: () => void;
  readonly retire: () => void;
  readonly addChild: (level: TreeLevel) => void;
}

export interface TreeEditorProps {
  readonly nodes: readonly TreeNode[];
  readonly levels: readonly TreeLevel[];
  readonly selectedId: TreeNodeId | null;
  readonly onSelect: (id: TreeNodeId | null) => void;

  /** Застосований (після паузи) пошуковий текст - для підсвічування й розгортання знайденого. */
  readonly query?: string | undefined;
  /** Викликається після паузи в наборі; джерело вирішує, чи фільтрувати на сервері. */
  readonly onSearch?: ((query: string) => void) | undefined;

  readonly showRetired?: boolean | undefined;
  readonly onShowRetiredChange?: ((value: boolean) => void) | undefined;

  /** Зміна місця. Відмова (reject) лишає діалог відкритим - повідомлення про помилку показує адаптер. */
  readonly onMove?: ((request: TreeMoveRequest) => Promise<void> | void) | undefined;
  /** М'яке виведення. `cascade` - у вузла є живі діти. */
  readonly onRetire?: ((node: TreeNode, cascade: boolean) => Promise<void> | void) | undefined;
  /** Додати дитину (`parent === null` - корінь) на рівні `level`; форму показує сторінка. */
  readonly onAddChild?: ((parent: TreeNode | null, level: TreeLevel) => void) | undefined;

  /** Є право змінювати. Без нього лишається перегляд і пошук. */
  readonly canEdit?: boolean | undefined;
  /** Зміна в польоті: дії вимкнено, діалоги показують очікування. */
  readonly busy?: boolean | undefined;
  readonly loading?: boolean | undefined;

  readonly label: string;
  readonly renderPanel?: ((node: TreeNode | null, actions: TreeNodeActions, path: readonly TreeNode[]) => ReactNode) | undefined;
  readonly renderMeta?: ((node: TreeNode) => ReactNode) | undefined;
  readonly footer?: ReactNode;
  readonly testId?: string | undefined;
}

/** Запас висоти рядка, якщо токен `--ecr-row-height` не прочитався (jsdom). */
const RowHeightFallback = 28;
/** Запас висоти вікна, якщо вимір недоступний (jsdom): рендериться достатньо рядків для тестів. */
const ViewportFallback = 480;
const Overscan = 8;
const TypeaheadResetMs = 600;

function Highlighted({ text, query, active }: { readonly text: string; readonly query: string; readonly active: boolean }): JSX.Element {
  if (!active) return <>{text}</>;

  return (
    <>
      {splitMatch(text, query).map((part, at) =>
        part.match ? (
          <mark key={at} className="ecr-tree-mark">
            {part.text}
          </mark>
        ) : (
          <span key={at}>{part.text}</span>
        ),
      )}
    </>
  );
}

/**
 * Загальний редактор дерева: пошук із підсвічуванням і розгортанням знайденого, ARIA tree з клавіатурою,
 * віртуалізація рядків, «Перенести до…»/Alt+↑↓, м'яке виведення з підтвердженням, додавання дитини за рівнями.
 *
 * ⛔ Не знає про мережу й про серверні DTO (див. `treeModel.ts`): дані приходять плоским списком, зміни виходять
 * колбеками. Підключення до контракту - тонкий адаптер (`features/registries/tree`).
 *
 * ⚠ ARIA: пласка модель дерева (`role=tree` > `treeitem` з `aria-level/posinset/setsize/expanded`), а не вкладені
 * `group`: вікно рендерить лише видимі рядки, і справжня вкладеність зламалася б віртуалізацією. Це дозволений
 * WAI-ARIA варіант, читалки оголошують рівень і позицію з атрибутів. Один рядок - один таб-стоп (roving tabindex);
 * активний рядок рендериться завжди, навіть коли він поза вікном, щоб у дерево можна було повернутися Tab'ом.
 */
export function TreeEditor({
  nodes,
  levels,
  selectedId,
  onSelect,
  query = '',
  onSearch,
  showRetired = false,
  onShowRetiredChange,
  onMove,
  onRetire,
  onAddChild,
  canEdit = false,
  busy = false,
  loading = false,
  label,
  renderPanel,
  renderMeta,
  footer,
  testId = 'tree-editor',
}: TreeEditorProps): JSX.Element {
  const hintId = useId();
  const index = useMemo(() => buildTreeIndex(nodes), [nodes]);
  const searching = query.trim() !== '';
  const editable = canEdit && !busy;

  // ── розгортання ──
  const [expanded, setExpanded] = useState<ReadonlySet<TreeNodeId>>(() => new Set());
  const seeded = useRef(false);
  useEffect(() => {
    // Перша поява даних: корені розгорнуті (макет). Далі розгортання - справа людини.
    if (seeded.current || nodes.length === 0) return;
    seeded.current = true;
    setExpanded(new Set(rootsOf(index).map((root) => root.id)));
  }, [index, nodes.length]);

  // Вибір ззовні (адреса, щойно перенесений вузол) - його предки розгорнуті, щоб вузол було видно.
  useEffect(() => {
    if (selectedId === null) return;
    const chain = ancestorsOf(index, selectedId);
    setExpanded((prev) => (chain.every((node) => prev.has(node.id)) ? prev : new Set([...prev, ...chain.map((node) => node.id)])));
  }, [index, selectedId]);

  const rows = useMemo(() => visibleRows(index, expanded, searching), [index, expanded, searching]);

  const toggle = useCallback((id: TreeNodeId, open: boolean) => {
    setExpanded((prev) => {
      if (prev.has(id) === open) return prev;
      const next = new Set(prev);
      if (open) next.add(id);
      else next.delete(id);

      return next;
    });
  }, []);

  // ── вікно прокрутки ──
  const hasRows = rows.length > 0;
  const viewportRef = useRef<HTMLDivElement>(null);
  const [rowHeight, setRowHeight] = useState(RowHeightFallback);
  const [viewportHeight, setViewportHeight] = useState(ViewportFallback);
  const [scrollTop, setScrollTop] = useState(0);
  useLayoutEffect(() => {
    const element = viewportRef.current;
    if (element === null) return undefined;
    const token = Number.parseFloat(getComputedStyle(element).getPropertyValue('--ecr-row-height'));
    if (Number.isFinite(token) && token > 0) setRowHeight(token);

    const measure = (): void => {
      if (element.clientHeight > 0) setViewportHeight(element.clientHeight);
    };
    measure();
    if (typeof ResizeObserver === 'undefined') return undefined;
    const observer = new ResizeObserver(measure);
    observer.observe(element);

    return () => observer.disconnect();
    // Вікно існує лише коли є рядки: ефект перезапускається, коли воно з'являється після завантаження.
  }, [hasRows]);

  // ── фокус ──
  const [activeId, setActiveId] = useState<TreeNodeId | null>(null);
  const [pendingFocus, setPendingFocus] = useState<TreeNodeId | null>(null);
  const tabId =
    rows.find((row) => row.node.id === activeId)?.node.id ??
    rows.find((row) => row.node.id === selectedId)?.node.id ??
    rows[0]?.node.id ??
    null;
  const tabAt = rows.findIndex((row) => row.node.id === tabId);

  const focusRow = useCallback(
    (id: TreeNodeId) => {
      const at = rows.findIndex((row) => row.node.id === id);
      if (at < 0) return;
      setActiveId(id);
      const element = viewportRef.current;
      if (element !== null) {
        const top = at * rowHeight;
        if (top < element.scrollTop) element.scrollTop = top;
        else if (top + rowHeight > element.scrollTop + viewportHeight) element.scrollTop = top + rowHeight - viewportHeight;
        setScrollTop(element.scrollTop);
      }
      setPendingFocus(id);
    },
    [rows, rowHeight, viewportHeight],
  );

  useEffect(() => {
    if (pendingFocus === null) return;
    const target = Array.from(viewportRef.current?.querySelectorAll<HTMLElement>('[role="treeitem"]') ?? []).find(
      (element) => element.dataset['treeId'] === String(pendingFocus),
    );
    if (target === undefined) return;
    target.focus();
    setPendingFocus(null);
  }, [pendingFocus, scrollTop, rows]);

  // ── пошук ──
  const [text, setText] = useState(query);
  const debounced = useDebouncedFilter(text);
  const lastSent = useRef(query);
  useEffect(() => {
    if (debounced === lastSent.current) return;
    lastSent.current = debounced;
    onSearch?.(debounced.trim());
  }, [debounced, onSearch]);

  const matchCount = useMemo(() => nodes.filter((node) => node.isMatch === true).length, [nodes]);
  const [announcement, setAnnouncement] = useState('');
  // Нове оголошення про результат пошуку важливіше за старе повідомлення про зміну.
  useEffect(() => setAnnouncement(''), [query]);
  const status = announcement !== '' ? announcement : searching ? t('treeEditor.matches', { count: matchCount }) : '';

  // ── діалоги ──
  const [moving, setMoving] = useState<TreeNode | null>(null);
  const [retiring, setRetiring] = useState<TreeNode | null>(null);
  const [working, setWorking] = useState(false);

  const run = async (action: () => Promise<void> | void): Promise<boolean> => {
    setWorking(true);
    try {
      await action();

      return true;
    } catch {
      // Повідомлення про відмову показує адаптер (handled mutation); діалог лишається відкритим для повтору.
      return false;
    } finally {
      setWorking(false);
    }
  };

  const addChild = (parent: TreeNode | null, level: TreeLevel): void => {
    onAddChild?.(parent, level);
  };

  const selectedNode = selectedId === null ? null : (index.byId.get(selectedId) ?? null);

  const actionsFor = (node: TreeNode | null): TreeNodeActions => {
    const live = node !== null && node.retired !== true;

    return {
      canMove: editable && live && onMove !== undefined,
      canRetire: editable && live && onRetire !== undefined,
      childLevels: editable && live && onAddChild !== undefined && node !== null ? childLevelsFor(levels, node.levelCode) : [],
      move: () => {
        if (node !== null) setMoving(node);
      },
      retire: () => {
        if (node !== null) setRetiring(node);
      },
      addChild: (level) => addChild(node, level),
    };
  };

  // ── клавіатура ──
  const typed = useRef('');
  const typedTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(
    () => () => {
      if (typedTimer.current !== null) clearTimeout(typedTimer.current);
    },
    [],
  );

  const onRowKeyDown = (event: KeyboardEvent<HTMLElement>, at: number): void => {
    if (isTypeaheadKey(event)) {
      typed.current += event.key;
      if (typedTimer.current !== null) clearTimeout(typedTimer.current);
      typedTimer.current = setTimeout(() => {
        typed.current = '';
      }, TypeaheadResetMs);
      const target = typeaheadTarget(rows, at, typed.current);
      if (target !== undefined) {
        event.preventDefault();
        focusRow(target.node.id);
      }
      return;
    }

    const action = resolveTreeKey(rows, index, at, event, { canEdit: editable, reorderable: !searching });
    if (action.type === 'none') return;
    event.preventDefault();

    const node = 'id' in action ? index.byId.get(action.id) : undefined;
    switch (action.type) {
      case 'focus':
        focusRow(action.id);
        break;
      case 'expand':
        toggle(action.id, true);
        break;
      case 'collapse':
        toggle(action.id, false);
        break;
      case 'select':
        onSelect(action.id);
        break;
      case 'retire':
        if (node !== undefined && onRetire !== undefined) setRetiring(node);
        break;
      case 'move':
        if (node !== undefined && onMove !== undefined) setMoving(node);
        break;
      case 'add': {
        const first = node === undefined ? undefined : childLevelsFor(levels, node.levelCode)[0];
        if (node !== undefined && first !== undefined) addChild(node, first);
        break;
      }
      case 'reorder':
        if (onMove !== undefined && node !== undefined) {
          const name = node.label;
          void run(() => onMove({ id: action.id, parentId: action.parentId, beforeId: action.beforeId })).then((ok) => {
            if (ok) setAnnouncement(t('treeEditor.reordered', { name, position: action.position, total: action.total }));
          });
        }
        break;
      default:
        break;
    }
  };

  // ── вікно: що рендерити ──
  const first = Math.max(0, Math.floor(scrollTop / rowHeight) - Overscan);
  const last = Math.min(rows.length, Math.ceil((scrollTop + viewportHeight) / rowHeight) + Overscan);
  const rendered: number[] = [];
  for (let at = first; at < last; at += 1) rendered.push(at);
  if (tabAt >= 0 && (tabAt < first || tabAt >= last)) {
    rendered.push(tabAt);
    rendered.sort((a, b) => a - b);
  }

  const levelName = (code: string | null | undefined): string | undefined =>
    code === null || code === undefined ? undefined : levels.find((level) => level.code === code)?.name;

  const rootLevel = onAddChild !== undefined && editable ? childLevelsFor(levels, null)[0] : undefined;
  const path = selectedNode === null ? [] : [...ancestorsOf(index, selectedNode.id), selectedNode];
  const clearSearch = (): void => {
    setText('');
    lastSent.current = '';
    onSearch?.('');
  };

  return (
    <div className="ecr-tree" data-testid={testId}>
      <section className="ecr-tree-pane" aria-label={label}>
        <div className="ecr-tree-tools">
          <div className="ecr-tree-search">
            <input
              type="search"
              value={text}
              aria-label={t('treeEditor.searchLabel')}
              placeholder={t('treeEditor.searchPlaceholder')}
              data-testid="tree-search"
              onChange={(event) => setText(event.currentTarget.value)}
              onKeyDown={(event) => {
                if (event.key === 'Escape' && text !== '') {
                  event.preventDefault();
                  clearSearch();
                }
              }}
            />
            {text !== '' && (
              <button type="button" aria-label={t('treeEditor.clearSearch')} onClick={clearSearch} data-testid="tree-search-clear">
                ×
              </button>
            )}
          </div>

          {onShowRetiredChange !== undefined && (
            <button
              type="button"
              className="ecr-tree-btn"
              aria-pressed={showRetired}
              onClick={() => onShowRetiredChange(!showRetired)}
              data-testid="tree-show-retired"
            >
              {t('treeEditor.showRetired')}
            </button>
          )}

          {rootLevel !== undefined && (
            <button type="button" className="ecr-tree-btn" onClick={() => addChild(null, rootLevel)} data-testid="tree-add-root">
              {t('treeEditor.addRoot')}
            </button>
          )}
        </div>

        <div className="ecr-tree-sr" id={hintId}>
          {t('treeEditor.keyboardHint')}
        </div>
        <div className="ecr-tree-sr" role="status" aria-live="polite" data-testid="tree-status">
          {status}
        </div>

        {rows.length === 0 ? (
          <div className="ecr-tree-empty" data-testid="tree-empty" role={loading ? 'status' : undefined}>
            <span>{loading ? t('treeEditor.loading') : searching ? t('treeEditor.nothingFound') : t('treeEditor.empty')}</span>
            {!loading && searching && (
              <button type="button" onClick={clearSearch}>
                {t('treeEditor.clearSearch')}
              </button>
            )}
          </div>
        ) : (
          <div
            ref={viewportRef}
            className="ecr-tree-viewport"
            onScroll={(event) => setScrollTop(event.currentTarget.scrollTop)}
            data-testid="tree-viewport"
          >
            <div
              className="ecr-tree-list"
              role="tree"
              aria-label={label}
              aria-describedby={hintId}
              aria-busy={loading || undefined}
              style={{ height: rows.length * rowHeight }}
            >
              {rendered.map((at) => {
                const row = rows[at];
                if (row === undefined) return null;
                const { node } = row;
                const kind = levelName(node.levelCode);
                const highlight = searching && node.isMatch === true;
                const selected = node.id === selectedId;

                return (
                  <div
                    key={String(node.id)}
                    role="treeitem"
                    aria-level={row.level}
                    aria-posinset={row.posInSet}
                    aria-setsize={row.setSize}
                    aria-selected={selected}
                    {...(row.hasChildren ? { 'aria-expanded': row.expanded } : {})}
                    tabIndex={node.id === tabId ? 0 : -1}
                    className="ecr-tree-row"
                    data-tree-id={String(node.id)}
                    data-retired={node.retired === true ? 'true' : undefined}
                    style={{ top: at * rowHeight, height: rowHeight, '--ecr-tree-depth': row.level - 1 } as CSSProperties}
                    onClick={() => {
                      setActiveId(node.id);
                      onSelect(node.id);
                    }}
                    onKeyDown={(event) => {
                      // Події вкладених елементів (кнопки в renderMeta) дереву не належать.
                      if (event.target === event.currentTarget) onRowKeyDown(event, at);
                    }}
                  >
                    <span
                      className="ecr-tree-caret"
                      aria-hidden="true"
                      data-has-children={row.hasChildren ? 'true' : undefined}
                      data-open={row.expanded ? 'true' : undefined}
                      onClick={(event) => {
                        if (!row.hasChildren || searching) return;
                        event.stopPropagation();
                        toggle(node.id, !row.expanded);
                      }}
                    />
                    {kind !== undefined && <span className="ecr-tree-kind">{kind}</span>}
                    <span className="ecr-tree-name">
                      <Highlighted text={node.label} query={query} active={highlight} />
                    </span>
                    {node.code !== undefined && node.code !== '' && (
                      <span className="ecr-tree-code">
                        <Highlighted text={node.code} query={query} active={highlight} />
                      </span>
                    )}
                    {node.retired === true && <span className="ecr-tree-retired">{t('treeEditor.retiredTag')}</span>}
                    {(node.childCount ?? 0) > 0 && (
                      <span className="ecr-tree-count" aria-label={t('treeEditor.childCount', { count: node.childCount ?? 0 })}>
                        {node.childCount}
                      </span>
                    )}
                    {renderMeta?.(node)}
                  </div>
                );
              })}
            </div>
          </div>
        )}

        {footer !== undefined && footer !== null && <div className="ecr-tree-foot">{footer}</div>}
      </section>

      {renderPanel !== undefined && (
        <div className="ecr-tree-side">{renderPanel(selectedNode, actionsFor(selectedNode), path)}</div>
      )}

      {moving !== null && onMove !== undefined && (
        <TreeMoveDialog
          node={moving}
          index={index}
          levels={levels}
          busy={working}
          onClose={() => setMoving(null)}
          onMove={(parentId) => {
            const node = moving;
            void run(() => onMove({ id: node.id, parentId, beforeId: null })).then((ok) => {
              if (!ok) return;
              setMoving(null);
              onSelect(node.id);
              setAnnouncement(t('treeEditor.moved', { name: node.label }));
            });
          }}
        />
      )}

      {retiring !== null && onRetire !== undefined && (
        <ConfirmModal
          opened
          title={t('treeEditor.retire.title', { name: retiring.label })}
          text={t('treeEditor.retire.text')}
          consequences={
            (retiring.childCount ?? 0) > 0 ? [t('treeEditor.retire.cascade', { count: retiring.childCount ?? 0 })] : []
          }
          verb={t('treeEditor.retire.verb')}
          isPending={working}
          onClose={() => setRetiring(null)}
          onConfirm={() => {
            const node = retiring;
            void run(() => onRetire(node, (node.childCount ?? 0) > 0)).then((ok) => {
              if (!ok) return;
              setRetiring(null);
              setAnnouncement(t('treeEditor.retiredDone', { name: node.label }));
            });
          }}
        />
      )}
    </div>
  );
}

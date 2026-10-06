import { useEffect, useMemo, useRef, useState, type JSX, type KeyboardEvent } from 'react';
import type { TemplateStructureDto } from '@/api/types';
import { CtorIcon as Icon } from './CtorIcon';
import { localized } from '@/shared/i18n/localized';
import { t } from '@/shared/i18n';
import { filterTree, nodeParam, type CtorNode, type TemplateSheet, type TemplateTable } from './ctorModel';
import '@/features/grid/tableNavigator.css';
import './ctor.css';

export const tableNameOf = (table: TemplateTable): string => localized(table.nameL10n) || table.code;
export const sheetNameOf = (sheet: TemplateSheet): string => localized(sheet.nameL10n) || sheet.code;

/** Видимий рядок дерева: корінь версії, аркуш або таблиця. */
type TreeRow =
  | { readonly key: string; readonly node: CtorNode; readonly level: 1; readonly kind: 'root' }
  | { readonly key: string; readonly node: CtorNode; readonly level: 1; readonly kind: 'sheet'; readonly sheet: TemplateSheet; readonly no: number; readonly total: number; readonly expanded: boolean }
  | { readonly key: string; readonly node: CtorNode; readonly level: 2; readonly kind: 'table'; readonly table: TemplateTable; readonly no: string; readonly sheetCode: string };

export interface CtorTreeProps {
  readonly id: string;
  readonly structure: TemplateStructureDto;
  readonly version: string | undefined;
  readonly selected: CtorNode;
  readonly onSelect: (node: CtorNode) => void;
  readonly hidden?: boolean;
}

/**
 * Дерево «Structure» конструктора версії (`UI-36`): корінь версії → аркуш → таблиця,
 * з пошуком «Find a table».
 *
 * ⚠ Макет (`screens-templates.js`) малює ще й групи таблиць («1 · STATIONARY
 * COMBUSTION»), але в структурі версії поля групи немає (D15-06): дерево плоске
 * за аркушами. Позначок помилок біля таблиць теж немає — перелік проблем
 * публікації сервер віддає лише на спробу опублікувати (`PublishProblemsAlert`).
 *
 * Клавіатура — патерн WAI-ARIA tree: ↑/↓/Home/End між видимими рядками,
 * → розгортає аркуш, ← згортає (або веде з таблиці на її аркуш), Enter/Space вибирає.
 */
export function CtorTree({ id, structure, version, selected, onSelect, hidden = false }: CtorTreeProps): JSX.Element {
  const [query, setQuery] = useState('');
  const selectedSheet =
    selected.kind === 'sheet'
      ? selected.code
      : selected.kind === 'table'
        ? structure.sheets.find((sheet) => sheet.tables.some((table) => table.id === selected.id))?.code
        : undefined;
  // ⚠ Розгорнуто: аркуш вибраного вузла + те, що людина розгорнула сама.
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(() => new Set(selectedSheet === undefined ? [] : [selectedSheet]));
  const [activeKey, setActiveKey] = useState<string | null>(null);
  const tree = useRef<HTMLDivElement>(null);

  // Вибір прийшов з адреси чи з робочої області (клац по таблиці аркуша) — його аркуш розгорнутий.
  useEffect(() => {
    if (selectedSheet !== undefined) setExpanded((prev) => (prev.has(selectedSheet) ? prev : new Set([...prev, selectedSheet])));
  }, [selectedSheet]);

  const filtered = useMemo(() => filterTree(structure, query, tableNameOf, sheetNameOf), [structure, query]);
  const searching = query.trim() !== '';

  const rows = useMemo<TreeRow[]>(() => {
    const out: TreeRow[] = [{ key: 'root', node: { kind: 'root' }, level: 1, kind: 'root' }];
    for (const { sheet, sheetNo, tables } of filtered) {
      // Під час пошуку збіги видно одразу — інакше людина шукала б їх удруге.
      const open = searching || expanded.has(sheet.code);
      out.push({ key: `s:${sheet.code}`, node: { kind: 'sheet', code: sheet.code }, level: 1, kind: 'sheet', sheet, no: sheetNo, total: sheet.tables.length, expanded: open });
      if (open) {
        for (const { table, no } of tables) {
          out.push({ key: `t:${String(table.id)}`, node: { kind: 'table', id: table.id }, level: 2, kind: 'table', table, no, sheetCode: sheet.code });
        }
      }
    }

    return out;
  }, [filtered, searching, expanded]);

  const selectedKey = nodeParam(selected);
  const tabStop = rows.find((row) => row.key === activeKey)?.key ?? rows.find((row) => row.key === selectedKey)?.key ?? 'root';

  const focusRow = (key: string): void => {
    setActiveKey(key);
    tree.current?.querySelector<HTMLElement>(`[data-ctor-node="${key}"]`)?.focus();
  };

  const toggle = (code: string, open: boolean): void =>
    setExpanded((prev) => {
      const next = new Set(prev);
      if (open) next.add(code);
      else next.delete(code);

      return next;
    });

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>, index: number): void => {
    const row = rows[index];
    if (row === undefined) return;
    const move = (to: number): void => {
      const target = rows[Math.max(0, Math.min(rows.length - 1, to))];
      if (target === undefined) return;
      event.preventDefault();
      focusRow(target.key);
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
        move(rows.length - 1);
        break;
      case 'ArrowRight':
        if (row.kind === 'sheet' && !row.expanded) {
          event.preventDefault();
          toggle(row.sheet.code, true);
        } else if (row.kind === 'sheet') {
          move(index + 1);
        }
        break;
      case 'ArrowLeft':
        if (row.kind === 'sheet' && row.expanded && !searching) {
          event.preventDefault();
          toggle(row.sheet.code, false);
        } else if (row.kind === 'table') {
          event.preventDefault();
          focusRow(`s:${row.sheetCode}`);
        }
        break;
      case 'Enter':
      case ' ':
        event.preventDefault();
        onSelect(row.node);
        break;
      default:
        break;
    }
  };

  return (
    <nav id={id} className="ecr-tnav ecr-ctor-tree" aria-label={t('ctor.treeLabel')} hidden={hidden} data-testid="ctor-tree">
      <div className="ecr-ctor-pane-h">
        <h2>{t('ctor.structure')}</h2>
      </div>

      <div className="ecr-tnav-tools">
        <div className="ecr-tnav-search">
          <Icon path="M11 18a7 7 0 1 0 0-14 7 7 0 0 0 0 14zM20 20l-4-4" size={14} />
          <input
            type="search"
            value={query}
            placeholder={t('ctor.findTable')}
            aria-label={t('ctor.findTable')}
            onChange={(event) => {
              setQuery(event.currentTarget.value);
              setActiveKey(null);
            }}
          />
        </div>
      </div>

      {searching && filtered.length === 0 ? (
        <div className="ecr-tnav-empty" data-testid="ctor-tree-empty">
          <span>{t('grid.tree.nothingFound')}</span>
          <button type="button" onClick={() => setQuery('')}>
            {t('grid.tree.clearFilter')}
          </button>
        </div>
      ) : (
        <div ref={tree} className="ecr-tnav-tree" role="tree" aria-label={t('ctor.treeLabel')}>
          {rows.map((row, index) => {
            const common = {
              role: 'treeitem',
              'aria-level': row.level,
              'aria-selected': row.key === selectedKey,
              tabIndex: row.key === tabStop ? 0 : -1,
              className: 'ecr-tnav-item',
              'data-ctor-node': row.key,
              onKeyDown: (event: KeyboardEvent<HTMLDivElement>) => onKeyDown(event, index),
            } as const;

            if (row.kind === 'root') {
              const label = version === undefined ? t('version.title') : t('ctor.versionNode', { version });

              return (
                <div key={row.key} {...common} onClick={() => { setActiveKey(row.key); onSelect(row.node); }}>
                  <span className="ecr-ctor-caret" aria-hidden="true">
                    <Icon path="M6 3v12M18 9a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM6 21a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM18 9a9 9 0 0 1-9 9" size={12} />
                  </span>
                  <span className="ecr-tnav-name">{label}</span>
                </div>
              );
            }

            if (row.kind === 'sheet') {
              const name = sheetNameOf(row.sheet);

              return (
                <div
                  key={row.key}
                  {...common}
                  aria-expanded={row.expanded}
                  title={`${name} · ${row.sheet.code}`}
                  onClick={() => {
                    setActiveKey(row.key);
                    // Перший клац вибирає й розгортає; клац по вже вибраному аркушу згортає/розгортає.
                    if (!searching) toggle(row.sheet.code, row.key !== selectedKey || !row.expanded);
                    onSelect(row.node);
                  }}
                >
                  <span className="ecr-ctor-caret" aria-hidden="true">
                    <Icon path="M9 6l6 6-6 6" size={12} />
                  </span>
                  <span className="ecr-tnav-name">{name}</span>
                  {/* ⚠ Прапорець `isVisible` — із самої структури версії (адміністратору шаблону
                      її показують повністю); числа поруч — кількість таблиць з тієї ж відповіді. */}
                  {!row.sheet.isVisible && <span className="ecr-ctor-hidden">{t('version.hidden')}</span>}
                  <span className="ecr-ctor-total" aria-label={t('ctor.tablesCount', { count: row.total })}>
                    {row.total}
                  </span>
                </div>
              );
            }

            const name = tableNameOf(row.table);

            return (
              <div
                key={row.key}
                {...common}
                title={`${row.no} ${name} · ${row.table.code}`}
                onClick={() => {
                  setActiveKey(row.key);
                  onSelect(row.node);
                }}
              >
                <span className="ecr-tnav-no" aria-hidden="true">
                  {row.no}
                </span>
                <span className="ecr-tnav-name">{name}</span>
              </div>
            );
          })}
        </div>
      )}
    </nav>
  );
}

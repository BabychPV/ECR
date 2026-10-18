import { lazy, Suspense, useState, type JSX } from 'react';
import { Button, Code, Menu } from '@mantine/core';
import { useSearchParams } from 'react-router-dom';
import { t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import type { PermitTypeColumn, PermitTypeTable } from '../api/types';
import { ColumnsTable } from './ColumnsTable';
import { findColumn, findTable, updateColumn, uniqueKeyParts } from './draftModel';
import { EditorParams, HeadNode, useEditorActions } from './editorActions';
import { useTypeEditorApi } from './editorContext';
import { headerRegistryCode, tableRegistryCode, textOf } from './labels';
import { useUnits } from './lookups';
import { StructureTree } from './StructureTree';
import { UnderTheHood } from './UnderTheHood';
import './typeEditor.css';

const ColumnInspector = lazy(() => import('./ColumnInspector').then((module) => ({ default: module.ColumnInspector })));

const MaxProblemsShown = 5;

/** Вкладка «Structure» (мокап, розділ 4): банер, дерево, таблиця стовпців, інспектор. */
export function StructureTab(): JSX.Element {
  const api = useTypeEditorApi();
  const actions = useEditorActions();
  const units = useUnits();
  const [params, setParams] = useSearchParams();
  const [treeOpen, setTreeOpen] = useState(false);
  const { draft, type, editable, problems } = api;

  const requested = params.get(EditorParams.table);
  const selected =
    requested === HeadNode || (requested !== null && findTable(draft, requested) !== undefined)
      ? requested
      : (draft.tables[0]?.code ?? HeadNode);
  const table: PermitTypeTable | undefined = selected === HeadNode ? undefined : findTable(draft, selected);
  const panel = params.get(EditorParams.panel);
  const column: PermitTypeColumn | undefined = table !== undefined && panel !== null ? findColumn(table, panel) : undefined;

  const go = (change: (next: URLSearchParams) => void): void => {
    const next = new URLSearchParams(params);
    change(next);
    setParams(next, { replace: true });
  };
  const selectNode = (code: string): void => {
    go((next) => {
      next.set(EditorParams.table, code);
      next.delete(EditorParams.panel);
    });
    setTreeOpen(false);
  };
  const closePanel = (): void => go((next) => next.delete(EditorParams.panel));

  const shown = table?.columns ?? type.headerFields;
  const keyParts =
    table === undefined ? [] : uniqueKeyParts(table, (c) => textOf(c.nameL10n) || c.code);
  const title = table === undefined ? t('permits.editor.tree.head') : textOf(table.nameL10n) || table.code;

  return (
    <div className="pt-structure">
      {editable && (
        <Banner
          tone="info"
          title={t('permits.editor.banner.title')}
          text={t('permits.editor.banner.text')}
          actions={[{ label: t('permits.editor.banner.add'), onClick: actions.openAddTable }]}
        />
      )}
      {editable && problems.length > 0 && (
        <Banner
          tone="warning"
          title={t('permits.editor.problems.title', { count: problems.length })}
          text={
            <ul className="pt-list">
              {problems.slice(0, MaxProblemsShown).map((problem, index) => (
                <li key={`${problem.messageKey}-${index}`}>{t(problem.messageKey, problem.params)}</li>
              ))}
              {problems.length > MaxProblemsShown && (
                <li>{t('permits.editor.problems.more', { count: problems.length - MaxProblemsShown })}</li>
              )}
            </ul>
          }
        />
      )}
      <div className="pt-split">
        <StructureTree
          draft={draft}
          headerFieldCount={type.headerFields.length}
          selected={selected}
          editable={editable}
          onSelect={selectNode}
          open={treeOpen}
        />
        <div className="pt-main">
          <div className="pt-pane-h">
            <h2>
              {title}
              <span className="pt-count">{t('permits.editor.table.columns', { count: shown.length })}</span>
            </h2>
            <div className="pt-pane-actions">
              <Button
                variant="default"
                size="xs"
                className="pt-tree-toggle"
                aria-expanded={treeOpen}
                onClick={() => setTreeOpen((open) => !open)}
              >
                {t('permits.editor.tree.title')}
              </Button>
              {editable && table !== undefined && (
                <>
                  <Button variant="default" size="xs" onClick={actions.openAddColumn}>
                    {t('permits.editor.table.addColumn')}
                  </Button>
                  <Menu position="bottom-end" withinPortal>
                    <Menu.Target>
                      <Button variant="default" size="xs">
                        {t('permits.editor.table.more')}
                      </Button>
                    </Menu.Target>
                    <Menu.Dropdown>
                      <Menu.Item onClick={actions.openAddSeries}>{t('permits.editor.table.addSeries')}</Menu.Item>
                      <Menu.Divider />
                      <Menu.Item color="statusError" onClick={() => actions.openDeleteTable(table.code)}>
                        {t('permits.editor.table.delete')}
                      </Menu.Item>
                    </Menu.Dropdown>
                  </Menu>
                </>
              )}
            </div>
          </div>
          <ColumnsTable
            columns={shown}
            tables={draft.tables}
            selectedCode={column?.code ?? null}
            unitCode={units.codeOf}
            readOnly={table === undefined}
            onSelect={(code) => go((next) => next.set(EditorParams.panel, code))}
          />
          <div className="pt-below">
            {table === undefined ? (
              <p className="pt-empty pt-flush">
                {t('permits.editor.head.note')}
              </p>
            ) : (
              <Code block>
                {keyParts.length === 0
                  ? t('permits.editor.key.none')
                  : t('permits.editor.key.hint', {
                      parts: keyParts.join(' + '),
                    })}
              </Code>
            )}
            <UnderTheHood
              registryCode={table === undefined ? headerRegistryCode(type.code) : tableRegistryCode(type.code, table.code)}
              parentCode={table === undefined ? undefined : headerRegistryCode(type.code)}
            />
          </div>
        </div>
        {table !== undefined && column !== undefined && (
          <Suspense fallback={null}>
            <ColumnInspector
              table={table}
              column={column}
              tables={draft.tables}
              editable={editable}
              problems={problems}
              onChange={(next) => api.apply((d) => updateColumn(d, table.code, column.code, next))}
              onClose={closePanel}
            />
          </Suspense>
        )}
      </div>
    </div>
  );
}

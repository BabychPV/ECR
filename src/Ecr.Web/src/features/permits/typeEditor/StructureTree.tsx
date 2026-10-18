import type { JSX } from 'react';
import { Button } from '@mantine/core';
import { t } from '@/shared/i18n';
import type { TypeDraft } from './draftModel';
import { HeadNode, useEditorActions } from './editorActions';
import { textOf } from './labels';
import './typeEditor.css';

export interface StructureTreeProps {
  readonly draft: TypeDraft;
  /** Скільки полів у шапці дозволу (фіксує сервер). */
  readonly headerFieldCount: number;
  /** Код вибраної таблиці або `HeadNode`. */
  readonly selected: string;
  readonly editable: boolean;
  readonly onSelect: (nodeCode: string) => void;
  /** На вузькому екрані дерево закрите, поки його не відкрито. */
  readonly open: boolean;
}

/**
 * Дерево структури типу (мокап: `.side` з `.pm-node`): «Permit details» і таблиці з кількістю
 * стовпців. Кнопки «Add table» ходять через `EditorActions` — діалог належить PM-08c.
 */
export function StructureTree({ draft, headerFieldCount, selected, editable, onSelect, open }: StructureTreeProps): JSX.Element {
  const actions = useEditorActions();
  const node = (code: string, label: string, count: number): JSX.Element => (
    <button
      key={code}
      type="button"
      className="pt-node"
      aria-current={selected === code}
      onClick={() => onSelect(code)}
    >
      <span className="pt-label">{label}</span>
      <span className="pt-n">{count}</span>
    </button>
  );

  return (
    <aside className="pt-side" aria-label={t('permits.editor.tree.aria')} data-open={open}>
      <div className="pt-pane-h">
        <h2>{t('permits.editor.tree.title')}</h2>
        {editable && (
          <Button size="xs" onClick={actions.openAddTable}>
            {t('permits.editor.tree.add')}
          </Button>
        )}
      </div>
      <div className="pt-tree">
        {node(HeadNode, t('permits.editor.tree.head'), headerFieldCount)}
        <div className="pt-tree-label">{t('permits.editor.tree.tables')}</div>
        {draft.tables.map((table) => node(table.code, textOf(table.nameL10n) || table.code, table.columns.length))}
        {draft.tables.length === 0 && <div className="pt-empty">{t('permits.editor.tree.empty')}</div>}
        {editable && (
          <div className="pt-tree-foot">
            <Button variant="default" size="xs" onClick={actions.openAddTable}>
              {t('permits.editor.tree.addAnother')}
            </Button>
          </div>
        )}
      </div>
    </aside>
  );
}

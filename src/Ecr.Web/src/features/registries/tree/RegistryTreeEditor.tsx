import { useState, type JSX, type ReactNode } from 'react';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { TreeEditor, TreeNodePanel, type TreeLevel, type TreeNode, type TreeNodeId } from '@/shared/ui/TreeEditor';
import { useRegistryTree } from './useRegistryTree';

export interface RegistryTreeEditorProps {
  readonly registryCode: string;
  /** Підпис дерева (aria-label і назва секції) - зазвичай назва довідника. */
  readonly label: string;
  /** Є право `Registry.EditData` (або грант `Write`) на цей довідник. */
  readonly canEdit: boolean;
  /** Форму нового вузла показує сторінка (атрибути довідника - її справа); без неї «Додати» не малюється. */
  readonly onAddChild?: ((parent: TreeNode | null, level: TreeLevel) => void) | undefined;
  /** Додаткові блоки під відомостями вузла (атрибути, «де використано»). */
  readonly renderPanelExtra?: ((node: TreeNode) => ReactNode) | undefined;
  readonly selectedId?: TreeNodeId | null | undefined;
  readonly onSelect?: ((id: TreeNodeId | null) => void) | undefined;
}

/**
 * Редактор дерева довідника, під'єднаний до контракту N1 (`GET hierarchy/tree`, `POST move/retire`).
 * Тонкий шар: дані й дії - `useRegistryTree`, вигляд і клавіатура - `shared/ui/TreeEditor`.
 *
 * ⚠ Вибір вузла - або керований ззовні (`selectedId`/`onSelect`, напр. з адреси), або внутрішній.
 */
export function RegistryTreeEditor({
  registryCode,
  label,
  canEdit,
  onAddChild,
  renderPanelExtra,
  selectedId,
  onSelect,
}: RegistryTreeEditorProps): JSX.Element {
  const tree = useRegistryTree(registryCode);
  const [own, setOwn] = useState<TreeNodeId | null>(null);
  const selected = selectedId !== undefined ? selectedId : own;
  const slow = usePendingLoading(tree.isFetching && !tree.isPending);
  const loadingLate = usePendingLoading(tree.isPending);

  // Миттєвий спінер заборонений храповиком ФВ-14.26: перші ~200 мс - порожнє місце, далі - текст «завантаження».
  if (tree.isPending && !loadingLate && (tree.error === null || tree.error === undefined)) {
    return <div aria-busy="true" data-testid="tree-pending" />;
  }

  if (tree.error !== null && tree.error !== undefined && tree.nodes.length === 0) {
    return <ErrorAlert error={tree.error} onRetry={tree.refetch} />;
  }

  return (
    <TreeEditor
      label={label}
      nodes={tree.nodes}
      levels={tree.levels}
      selectedId={selected}
      onSelect={(id) => {
        setOwn(id);
        onSelect?.(id);
      }}
      query={tree.query}
      onSearch={tree.setQuery}
      showRetired={tree.showRetired}
      onShowRetiredChange={tree.setShowRetired}
      onMove={tree.move}
      onRetire={tree.retire}
      onAddChild={onAddChild}
      canEdit={canEdit}
      busy={tree.isMutating}
      loading={loadingLate}
      footer={slow ? t('treeEditor.updating') : undefined}
      renderPanel={(node, actions, path) => (
        <TreeNodePanel node={node} path={path} levels={tree.levels} actions={actions}>
          {node !== null && renderPanelExtra?.(node)}
        </TreeNodePanel>
      )}
    />
  );
}

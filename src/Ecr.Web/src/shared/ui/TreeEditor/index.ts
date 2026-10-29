/**
 * Точка входу загального редактора дерева (NAE plan N1/N2).
 *
 * ⚠ Тека, а не файл: модель (`treeModel`, чисті функції), редактор, діалог переносу й панель вузла - окремі файли
 * поруч. Редактор не знає про мережу; з'єднання з контрактом - `features/registries/tree`.
 */
export { TreeEditor, type TreeEditorProps, type TreeMoveRequest, type TreeNodeActions } from './TreeEditor';
export { TreeNodePanel, type TreeNodePanelProps } from './TreeNodePanel';
export {
  buildTreeIndex,
  canPlace,
  childLevelsFor,
  type TreeLevel,
  type TreeNode,
  type TreeNodeId,
} from './treeModel';

import type { NaeTreeNode } from '../airTypes';

/** Рівень самого джерела (НАЕ) у дереві: під ним нових вузлів не створюють. */
export const SourceLevelCode = 'SRC';

export interface NodeOption {
  readonly value: string;
  readonly label: string;
}

/**
 * Вузли дерева, під якими можна створити відсутній НАЕ (усі, крім самих джерел), з підписом-шляхом
 * «Atyrau › Offshore › Barges». Дерево плоске: шлях збирається за `parentEntryId`; цикл чи сирота не зациклюють —
 * довжина шляху обмежена глибиною дерева.
 */
export function nodeOptions(nodes: readonly NaeTreeNode[]): readonly NodeOption[] {
  const byId = new Map(nodes.map((node) => [node.id, node] as const));

  const pathOf = (node: NaeTreeNode): string => {
    const names: string[] = [];
    let current: NaeTreeNode | undefined = node;

    for (let depth = 0; current !== undefined && depth < 16; depth += 1) {
      names.unshift(current.display);
      current = current.parentEntryId === null ? undefined : byId.get(current.parentEntryId);
    }

    return names.join(' › ');
  };

  return nodes
    .filter((node) => node.isActive && node.levelCode !== SourceLevelCode)
    .map((node) => ({ value: String(node.id), label: pathOf(node) }))
    .sort((a, b) => a.label.localeCompare(b.label));
}

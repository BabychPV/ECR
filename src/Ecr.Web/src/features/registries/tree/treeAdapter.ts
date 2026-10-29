import type { TreeLevel, TreeNode } from '@/shared/ui/TreeEditor';
import { localized } from '@/shared/i18n/localized';
import type { RegistryLevel, RegistryTreeNode } from './treeApi';

/**
 * Серверний вузол дерева -> вузол редактора.
 *
 * ⚠ Вузол «виведений», коли вимкнений або його вікно дійсності вже закрите (`validTo` - ПЕРШИЙ недійсний день,
 * тож `validTo <= сьогодні`). Дати - рядки `YYYY-MM-DD`, порівнюються як рядки: формат фіксований, а `Date` додав би часовий пояс.
 * Вузол, що ще не почав діяти (`validFrom` у майбутньому), НЕ виведений.
 */
export function toTreeNodes(nodes: readonly RegistryTreeNode[], today: string): TreeNode[] {
  return nodes.map((node) => ({
    id: node.id,
    parentId: node.parentEntryId,
    label: node.display,
    code: node.code,
    levelCode: node.levelCode,
    ordinal: node.ordinal,
    retired: !node.isActive || (node.validTo !== null && node.validTo <= today),
    childCount: node.childCount,
    isMatch: node.isMatch,
  }));
}

/** Рівні сервера -> рівні редактора (назва мовою читача). */
export function toTreeLevels(levels: readonly RegistryLevel[]): TreeLevel[] {
  return [...levels]
    .sort((a, b) => a.ordinal - b.ordinal)
    .map((level) => ({
      code: level.code,
      ordinal: level.ordinal,
      name: localized(level.name) || level.code,
      isOptional: level.isOptional,
    }));
}

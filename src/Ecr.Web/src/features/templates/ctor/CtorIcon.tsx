import type { JSX } from 'react';

/**
 * Піктограма-контур 24×24 (той самий вигляд, що `Icon` дерева документа).
 * ⚠ Власна копія, а не імпорт з `features/grid/TableNavigator`: статичний імпорт
 * тягнув би в маршрут конструктора весь модуль дерева документа (бюджет `D-132`).
 */
export function CtorIcon({ path, size = 16 }: { readonly path: string; readonly size?: number }): JSX.Element {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.75}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      <path d={path} />
    </svg>
  );
}

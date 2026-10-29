import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { TreeEditor, TreeNodePanel, type TreeLevel, type TreeNode } from '@/shared/ui/TreeEditor';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';

/**
 * Доступність редактора дерева в ОБОХ темах (`ФВ-14.16`, `D-127`): дерево з виведеним вузлом, збігом пошуку,
 * панеллю вузла, відкритим діалогом переносу.
 *
 * ⛔ Окремий файл із суфіксом `.a11y.` - `axe` живе у своєму прогоні (`vitest.a11y.config.ts`), гейти `a11y (dark)`
 * і `a11y (light)`.
 */

const levels: TreeLevel[] = [
  { code: 'region', ordinal: 1, name: 'Region', isOptional: false },
  { code: 'site', ordinal: 2, name: 'Site', isOptional: false },
  { code: 'nae', ordinal: 3, name: 'NAE', isOptional: false },
];

const nodes: TreeNode[] = [
  { id: 1, parentId: null, label: 'Atyrau', code: 'ATY', levelCode: 'region', ordinal: 1, childCount: 2 },
  { id: 2, parentId: 1, label: 'Barges', code: 'BRG', levelCode: 'site', ordinal: 1, childCount: 2 },
  { id: 3, parentId: 2, label: 'Exhaust pipe', code: '1085', levelCode: 'nae', ordinal: 1, isMatch: true },
  { id: 4, parentId: 2, label: 'Old exhaust', code: '1086', levelCode: 'nae', ordinal: 2, retired: true },
  { id: 5, parentId: null, label: 'Mangistau', code: 'MNG', levelCode: 'region', ordinal: 2 },
];

afterEach(cleanup);

const tree = (): React.JSX.Element => (
  <TreeEditor
    label="NAE structure"
    nodes={nodes}
    levels={levels}
    selectedId={3}
    onSelect={() => {}}
    query="108"
    onSearch={() => {}}
    showRetired
    onShowRetiredChange={() => {}}
    onMove={() => {}}
    onRetire={() => {}}
    onAddChild={() => {}}
    canEdit
    renderPanel={(node, actions, path) => (
      <TreeNodePanel node={node} path={path} levels={levels} actions={actions} validFrom="2024-01-01" validTo="2026-12-31" />
    )}
  />
);

describe('TreeEditor - axe без блокуючих порушень', () => {
  it.each(Themes)('тема %s: дерево, збіг пошуку, виведений вузол, панель вузла', async (scheme) => {
    const { container } = render(<Shell colorScheme={scheme}>{tree()}</Shell>);

    const violations = await findViolations(container);

    expect(violations, describeViolations(violations)).toHaveLength(0);
  });

  it.each(Themes)('тема %s: діалог переносу', async (scheme) => {
    const user = userEvent.setup();
    render(<Shell colorScheme={scheme}>{tree()}</Shell>);

    await user.click(screen.getByTestId('tree-panel-move'));
    const dialog = await screen.findByTestId('tree-move-dialog');

    const violations = await findViolations(dialog.closest('[role="dialog"]') as HTMLElement);

    expect(violations, describeViolations(violations)).toHaveLength(0);
  });

  it.each(Themes)('тема %s: порожнє дерево', async (scheme) => {
    const { container } = render(
      <Shell colorScheme={scheme}>
        <TreeEditor label="NAE structure" nodes={[]} levels={levels} selectedId={null} onSelect={() => {}} />
      </Shell>,
    );

    const violations = await findViolations(container);

    expect(violations, describeViolations(violations)).toHaveLength(0);
  });
});

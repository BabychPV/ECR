import { describe, expect, it } from 'vitest';
import {
  ancestorsOf,
  buildTreeIndex,
  canPlace,
  childLevelsFor,
  descendantIds,
  isTypeaheadKey,
  moveTargets,
  pathLabel,
  resolveTreeKey,
  splitMatch,
  typeaheadTarget,
  visibleRows,
  type TreeLevel,
  type TreeNode,
} from '@/shared/ui/TreeEditor/treeModel';

/**
 * Модель редактора дерева: видимі рядки, пошук, правила рівнів, перенос, клавіатура.
 * Чисті функції - без DOM, тож перевіряються на малому, але повному зразку (регіон > зона > майданчик > група > NAE).
 */

const levels: TreeLevel[] = [
  { code: 'region', ordinal: 1, name: 'Region', isOptional: false },
  { code: 'area', ordinal: 2, name: 'Area', isOptional: true },
  { code: 'site', ordinal: 3, name: 'Site', isOptional: false },
  { code: 'equip', ordinal: 4, name: 'Equipment', isOptional: true },
  { code: 'nae', ordinal: 5, name: 'NAE', isOptional: false },
];

const node = (id: number, parentId: number | null, label: string, levelCode: string, ordinal = 1, extra: Partial<TreeNode> = {}): TreeNode => ({
  id,
  parentId,
  label,
  levelCode,
  ordinal,
  code: label,
  ...extra,
});

const sample: TreeNode[] = [
  node(1, null, 'Atyrau', 'region'),
  node(2, 1, 'Offshore', 'area'),
  node(3, 2, 'Barges', 'site'),
  node(4, 3, 'Generator', 'equip'),
  node(5, 4, '1085', 'nae', 1),
  node(6, 4, '1086', 'nae', 2),
  node(7, 4, '1087', 'nae', 3, { retired: true }),
  node(8, 2, 'Island_A', 'site', 2),
  node(9, null, 'Mangistau', 'region', 2),
  node(10, 9, 'ZPK_1', 'site'),
];

describe('visibleRows', () => {
  const index = buildTreeIndex(sample);

  it('згорнуте дерево - лише корені, з позицією серед сусідів', () => {
    const rows = visibleRows(index, new Set(), false);

    expect(rows.map((row) => row.node.label)).toEqual(['Atyrau', 'Mangistau']);
    expect(rows.map((row) => [row.level, row.posInSet, row.setSize])).toEqual([
      [1, 1, 2],
      [1, 2, 2],
    ]);
    expect(rows.every((row) => row.hasChildren && !row.expanded)).toBe(true);
  });

  it('розгорнуте - обхід углиб у порядку ordinal, рівні та aria-позиції правильні', () => {
    const rows = visibleRows(index, new Set([1, 2, 3, 4]), false);

    expect(rows.map((row) => row.node.label)).toEqual([
      'Atyrau', 'Offshore', 'Barges', 'Generator', '1085', '1086', '1087', 'Island_A', 'Mangistau',
    ]);
    const nae = rows.find((row) => row.node.label === '1086');
    expect(nae).toMatchObject({ level: 5, posInSet: 2, setSize: 3, hasChildren: false });
    expect(rows.find((row) => row.node.label === 'Island_A')).toMatchObject({ level: 3, posInSet: 2, setSize: 2 });
  });

  it('пошук (expandAll) розгортає все, незалежно від набору розгорнутих', () => {
    expect(visibleRows(index, new Set(), true)).toHaveLength(sample.length);
  });

  it('батько поза списком або сам собі батько - корінь, цикл не зависає', () => {
    const odd = buildTreeIndex([node(1, 99, 'Orphan', 'site'), node(2, 2, 'Self', 'site'), node(3, null, 'Root', 'region')]);

    expect(visibleRows(odd, new Set(), true).map((row) => row.node.label).sort()).toEqual(['Orphan', 'Root', 'Self']);
  });

  it('сусіди сортуються за ordinal стабільно', () => {
    const index2 = buildTreeIndex([node(1, null, 'B', 'region', 2), node(2, null, 'A', 'region', 1), node(3, null, 'C', 'region', 2)]);

    expect(visibleRows(index2, new Set(), false).map((row) => row.node.label)).toEqual(['A', 'B', 'C']);
  });
});

describe('ancestorsOf, descendantIds, pathLabel', () => {
  const index = buildTreeIndex(sample);

  it('предки - від кореня до батька', () => {
    expect(ancestorsOf(index, 5).map((item) => item.label)).toEqual(['Atyrau', 'Offshore', 'Barges', 'Generator']);
    expect(ancestorsOf(index, 1)).toEqual([]);
  });

  it('нащадки без самого вузла', () => {
    expect([...descendantIds(index, 3)].sort((a, b) => Number(a) - Number(b))).toEqual([4, 5, 6, 7]);
  });

  it('шлях - через розділювач', () => {
    expect(pathLabel(index, 5)).toBe('Atyrau › Offshore › Barges › Generator › 1085');
  });
});

describe('splitMatch', () => {
  it('підсвічує всі входження без урахування регістру', () => {
    expect(splitMatch('Barges Barge', 'barge')).toEqual([
      { text: 'Barge', match: true },
      { text: 's ', match: false },
      { text: 'Barge', match: true },
    ]);
  });

  it('порожній запит чи немає збігу - цілий текст', () => {
    expect(splitMatch('Island', '')).toEqual([{ text: 'Island', match: false }]);
    expect(splitMatch('Island', 'zzz')).toEqual([{ text: 'Island', match: false }]);
  });
});

describe('правила рівнів', () => {
  it('canPlace: глибше за батька, пропущені рівні - необов\'язкові', () => {
    expect(canPlace(levels, 'region', 'area')).toBe(true);
    expect(canPlace(levels, 'region', 'site')).toBe(true); // area необов'язкова
    expect(canPlace(levels, 'area', 'equip')).toBe(false); // site обов'язковий між ними
    expect(canPlace(levels, 'site', 'nae')).toBe(true); // equip необов'язковий
    expect(canPlace(levels, 'site', 'site')).toBe(false);
    expect(canPlace(levels, 'nae', 'site')).toBe(false);
  });

  it('canPlace: корінь - лише якщо всі вищі рівні необов\'язкові; невідомий рівень не блокує', () => {
    expect(canPlace(levels, null, 'region')).toBe(true);
    expect(canPlace(levels, null, 'site')).toBe(false); // region обов'язковий
    expect(canPlace(levels, 'mystery', 'site')).toBe(true);
    expect(canPlace(levels, 'region', null)).toBe(true);
  });

  it('childLevelsFor: від найближчого до першого обов\'язкового включно', () => {
    expect(childLevelsFor(levels, null).map((level) => level.code)).toEqual(['region']);
    expect(childLevelsFor(levels, 'region').map((level) => level.code)).toEqual(['area', 'site']);
    expect(childLevelsFor(levels, 'site').map((level) => level.code)).toEqual(['equip', 'nae']);
    expect(childLevelsFor(levels, 'nae')).toEqual([]);
    expect(childLevelsFor(levels, 'mystery')).toEqual([]);
  });
});

describe('moveTargets', () => {
  const index = buildTreeIndex(sample);
  const barges = sample[2] as TreeNode;

  it('не він сам, не нащадки, не виведені; лише дозволені рівнем', () => {
    const result = moveTargets(index, levels, barges, '', 50);
    const labels = result.items.map((item) => item.node.label);

    // Порядок - за шляхом: «Atyrau», «Atyrau › Offshore», «Mangistau».
    expect(labels).toEqual(['Atyrau', 'Offshore', 'Mangistau']);
    expect(labels).not.toContain('Barges');
    expect(labels).not.toContain('Generator');
    expect(result.rootAllowed).toBe(false); // site не може бути коренем: region обов'язковий
  });

  it('region може стати коренем, але не може стати дитиною свого нащадка', () => {
    const result = moveTargets(index, levels, sample[0] as TreeNode, '', 50);

    expect(result.rootAllowed).toBe(true);
    expect(result.items).toEqual([]);
  });

  it('пошук за назвою/кодом і ліміт з правдивим total', () => {
    const naeNode = sample[4] as TreeNode;
    const all = moveTargets(index, levels, naeNode, '', 1);

    expect(all.items).toHaveLength(1);
    expect(all.total).toBeGreaterThan(1);
    expect(moveTargets(index, levels, naeNode, 'island', 50).items.map((item) => item.node.label)).toEqual(['Island_A']);
  });

  it('виведений вузол не пропонується батьком', () => {
    const retiredParent = [...sample.slice(0, 3), node(20, 2, 'Old', 'site', 9, { retired: true })];
    const result = moveTargets(buildTreeIndex(retiredParent), levels, node(21, null, 'x', 'nae'), 'Old', 50);

    expect(result.items).toEqual([]);
  });
});

describe('resolveTreeKey', () => {
  const index = buildTreeIndex(sample);
  const rows = visibleRows(index, new Set([1, 2, 3, 4]), false);
  const at = (label: string): number => rows.findIndex((row) => row.node.label === label);
  const opts = { canEdit: true, reorderable: true };
  const press = (label: string, key: string, mods: { altKey?: boolean } = {}) =>
    resolveTreeKey(rows, index, at(label), { key, ...mods }, opts);

  it('стрілки та Home/End', () => {
    expect(press('Atyrau', 'ArrowDown')).toEqual({ type: 'focus', id: 2 });
    expect(press('Atyrau', 'ArrowUp')).toEqual({ type: 'none' });
    expect(press('Offshore', 'Home')).toEqual({ type: 'focus', id: 1 });
    expect(press('Offshore', 'End')).toEqual({ type: 'focus', id: 9 });
  });

  it('→: розгорнути згорнуте, перейти до першої дитини розгорнутого, нічого на листку', () => {
    const collapsed = visibleRows(index, new Set(), false);
    expect(resolveTreeKey(collapsed, index, 0, { key: 'ArrowRight' }, opts)).toEqual({ type: 'expand', id: 1 });
    expect(press('Atyrau', 'ArrowRight')).toEqual({ type: 'focus', id: 2 });
    expect(press('1085', 'ArrowRight')).toEqual({ type: 'none' });
  });

  it('←: згорнути розгорнуте, інакше - до батька', () => {
    expect(press('Atyrau', 'ArrowLeft')).toEqual({ type: 'collapse', id: 1 });
    expect(press('1085', 'ArrowLeft')).toEqual({ type: 'focus', id: 4 });
    // Згорнутий корінь: ні згортати, ні йти нікуди.
    expect(press('Mangistau', 'ArrowLeft')).toEqual({ type: 'none' });
  });

  it('Enter і Space вибирають', () => {
    expect(press('Barges', 'Enter')).toEqual({ type: 'select', id: 3 });
    expect(press('Barges', ' ')).toEqual({ type: 'select', id: 3 });
  });

  it('Delete виводить, але не вже виведений; Insert додає; Alt+M переносить', () => {
    expect(press('Barges', 'Delete')).toEqual({ type: 'retire', id: 3 });
    expect(press('1087', 'Delete')).toEqual({ type: 'none' });
    expect(press('Barges', 'Insert')).toEqual({ type: 'add', id: 3 });
    expect(press('Barges', 'm', { altKey: true })).toEqual({ type: 'move', id: 3 });
  });

  it('без права змін - лише навігація', () => {
    const view = { canEdit: false, reorderable: true };
    const run = (key: string, alt = false) => resolveTreeKey(rows, index, at('Barges'), { key, altKey: alt }, view);

    expect(run('Delete')).toEqual({ type: 'none' });
    expect(run('Insert')).toEqual({ type: 'none' });
    expect(run('m', true)).toEqual({ type: 'none' });
    expect(run('ArrowDown')).toEqual({ type: 'focus', id: 4 });
  });

  it('Alt+↑/↓ - порядок серед сусідів: перед яким стати', () => {
    // 1085(1) 1086(2) 1087(3) під Generator
    expect(press('1086', 'ArrowUp', { altKey: true })).toMatchObject({ type: 'reorder', id: 6, parentId: 4, beforeId: 5, position: 1, total: 3 });
    expect(press('1085', 'ArrowDown', { altKey: true })).toMatchObject({ type: 'reorder', id: 5, beforeId: 7, position: 2 });
    expect(press('1086', 'ArrowDown', { altKey: true })).toMatchObject({ type: 'reorder', id: 6, beforeId: null, position: 3 });
    expect(press('1085', 'ArrowUp', { altKey: true })).toEqual({ type: 'none' });
    expect(press('1087', 'ArrowDown', { altKey: true })).toEqual({ type: 'none' });
  });

  it('порядок вимкнено під час пошуку (reorderable=false)', () => {
    expect(resolveTreeKey(rows, index, at('1086'), { key: 'ArrowUp', altKey: true }, { canEdit: true, reorderable: false })).toEqual({ type: 'none' });
  });

  it('Ctrl/Meta-комбінації дерево не перехоплює', () => {
    expect(resolveTreeKey(rows, index, 0, { key: 'ArrowDown', ctrlKey: true }, opts)).toEqual({ type: 'none' });
  });
});

describe('typeahead', () => {
  const index = buildTreeIndex(sample);
  const rows = visibleRows(index, new Set(), true);

  it('isTypeaheadKey: лише друкований символ без модифікаторів', () => {
    expect(isTypeaheadKey({ key: 'a' })).toBe(true);
    expect(isTypeaheadKey({ key: ' ' })).toBe(false);
    expect(isTypeaheadKey({ key: 'a', ctrlKey: true })).toBe(false);
    expect(isTypeaheadKey({ key: 'Enter' })).toBe(false);
  });

  it('перша літера - наступний рядок, що починається з неї; повтор перебирає по колу', () => {
    const labels = rows.map((row) => row.node.label);
    const from = labels.indexOf('Atyrau');

    expect(typeaheadTarget(rows, from, 'm')?.node.label).toBe('Mangistau');
    expect(typeaheadTarget(rows, labels.indexOf('Mangistau'), 'm')?.node.label).toBe('Mangistau');
    expect(typeaheadTarget(rows, from, 'qq')).toBeUndefined();
  });

  it('кілька літер уточнюють з поточного рядка', () => {
    const labels = rows.map((row) => row.node.label);

    expect(typeaheadTarget(rows, labels.indexOf('Island_A'), 'isl')?.node.label).toBe('Island_A');
    expect(typeaheadTarget(rows, 0, 'zpk')?.node.label).toBe('ZPK_1');
  });
});

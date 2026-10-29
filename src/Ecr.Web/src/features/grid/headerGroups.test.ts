import type { ColumnGrouping, ColumnRegular } from '@revolist/revogrid';
import { describe, expect, it } from 'vitest';

import { withHeaderGroups } from './headerGroups';

const leaf = (prop: string): ColumnRegular => ({ prop, name: prop });
const groups = [
  { id: 1, parentId: null, label: 'Emissions' },
  { id: 2, parentId: 1, label: 'Q1' },
  { id: 3, parentId: 1, label: 'Q2' },
];
const columns = [
  { code: 'a', headerGroupId: null },
  { code: 'b', headerGroupId: 2 },
  { code: 'c', headerGroupId: 2 },
  { code: 'd', headerGroupId: 3 },
  { code: 'e', headerGroupId: 1 },
  { code: 'f', headerGroupId: null },
];
const flat = ['a', 'b', 'c', 'd', 'e', 'f'].map(leaf);

const classOf = (column: ColumnRegular): string => {
  const props = (column.columnProperties as unknown as () => { class: string })();

  return props.class;
};

describe('withHeaderGroups', () => {
  it('returns the very same array when there are no groups', () => {
    expect(withHeaderGroups(flat, [], columns)).toBe(flat);
    expect(withHeaderGroups(flat, undefined, columns)).toBe(flat);
    expect(withHeaderGroups(flat, groups, columns.map((c) => ({ code: c.code })))).toBe(flat);
  });

  it('nests consecutive columns into groups and keeps order', () => {
    const tree = withHeaderGroups(flat, groups, columns);

    expect(tree).toHaveLength(3);
    expect((tree[0] as ColumnRegular).prop).toBe('a');
    const top = tree[1] as ColumnGrouping;

    expect(top.name).toBe('Emissions');
    expect(top.children).toHaveLength(3);
    expect((top.children[0] as ColumnGrouping).name).toBe('Q1');
    expect((top.children[0] as ColumnGrouping).children.map((c) => (c as ColumnRegular).prop)).toEqual(['b', 'c']);
    expect((top.children[1] as ColumnGrouping).name).toBe('Q2');
    expect((top.children[2] as ColumnRegular).prop).toBe('e');
    expect((tree[2] as ColumnRegular).prop).toBe('f');
  });

  it('lifts shallower columns over the empty group cells above them', () => {
    const tree = withHeaderGroups(flat, groups, columns);
    const top = tree[1] as ColumnGrouping;
    const q1 = top.children[0] as ColumnGrouping;

    expect(classOf(tree[0] as ColumnRegular)).toContain('ecr-hg-up-2');
    expect(classOf(tree[2] as ColumnRegular)).toContain('ecr-hg-up-2');
    expect(classOf(top.children[2] as ColumnRegular)).toContain('ecr-hg-up-1');
    expect(classOf(q1.children[0] as ColumnRegular)).not.toContain('ecr-hg-up');
    expect(classOf(q1.children[0] as ColumnRegular)).toContain('ecr-hg-col');
  });

  it('reports the number of spanned columns on every group cell', () => {
    const tree = withHeaderGroups(flat, groups, columns);
    const top = tree[1] as ColumnGrouping & { columnProperties: () => Record<string, unknown> };
    const q1 = top.children[0] as ColumnGrouping & { columnProperties: () => Record<string, unknown> };

    expect(top.columnProperties()['aria-colspan']).toBe(4);
    expect(q1.columnProperties()['aria-colspan']).toBe(2);
  });

  it('keeps an existing header class and adds the group path for screen readers', () => {
    const numeric: ColumnRegular = { prop: 'b', name: 'b', columnProperties: () => ({ class: 'ecr-header-numeric' }) };
    const tree = withHeaderGroups([numeric], groups, [{ code: 'b', headerGroupId: 2 }]);
    const q1 = (tree[0] as ColumnGrouping).children[0] as ColumnGrouping;
    const column = q1.children[0] as ColumnRegular;

    expect(classOf(column)).toContain('ecr-header-numeric');
    expect(classOf(column)).toContain('ecr-hg-col');
    expect(column.columnTemplate).toBeDefined();
  });

  it('ignores cycles and unknown group ids', () => {
    const cyclic = [
      { id: 1, parentId: 2, label: 'A' },
      { id: 2, parentId: 1, label: 'B' },
    ];
    const tree = withHeaderGroups([leaf('x'), leaf('y')], cyclic, [
      { code: 'x', headerGroupId: 1 },
      { code: 'y', headerGroupId: 99 },
    ]);

    expect(tree.length).toBeGreaterThan(0);
  });
});

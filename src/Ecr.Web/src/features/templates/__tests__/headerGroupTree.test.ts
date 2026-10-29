import { describe, expect, it } from 'vitest';
import type { TemplateHeaderGroupDto } from '@/api/types';
import {
  addGroup,
  depthOf,
  draftsOf,
  flatten,
  isValid,
  moveGroup,
  previewHeaderRows,
  relabel,
  removeGroup,
  toRequest,
} from '../headerGroupTree';

const dto = (id: number, code: string, parentId: number | null, ordinal: number, en: string): TemplateHeaderGroupDto => ({
  id,
  code,
  parentId,
  ordinal,
  labelL10n: { values: { en } },
});
const groups = [dto(1, 'E', null, 0, 'Emissions'), dto(2, 'Q1', 1, 0, 'Q1'), dto(3, 'JAN', 2, 0, 'Jan'), dto(4, 'F', null, 1, 'Fuel')];

describe('headerGroupTree', () => {
  it('turns ids into parent codes and flattens depth-first', () => {
    const drafts = draftsOf(groups);

    expect(drafts.find((group) => group.code === 'JAN')?.parentCode).toBe('Q1');
    expect(flatten(drafts).map(({ group, depth }) => `${group.code}:${String(depth)}`)).toEqual(['E:1', 'Q1:2', 'JAN:3', 'F:1']);
    expect(depthOf(drafts, 'JAN')).toBe(3);
  });

  it('refuses a fourth level', () => {
    const drafts = draftsOf(groups);

    expect(addGroup(drafts, 'JAN')).toHaveLength(drafts.length);
    expect(addGroup(drafts, 'Q1')).toHaveLength(drafts.length + 1);
  });

  it('removes a group with its subgroups and renumbers siblings', () => {
    const next = removeGroup(draftsOf(groups), 'E');

    expect(next.map((group) => group.code)).toEqual(['F']);
    expect(next[0]?.ordinal).toBe(0);
  });

  it('moves a group among its siblings only', () => {
    const next = moveGroup(draftsOf(groups), 'F', -1);

    expect(flatten(next).filter(({ depth }) => depth === 1).map(({ group }) => group.code)).toEqual(['F', 'E']);
    expect(moveGroup(next, 'F', -1)).toEqual(next);
  });

  it('requires a name in at least one language and drops empty languages from the request', () => {
    const added = addGroup(draftsOf(groups), null);
    const fresh = added[added.length - 1];

    expect(isValid(added)).toBe(false);

    const named = relabel(added, fresh?.code ?? '', { en: 'X', ru: ' ' });

    expect(isValid(named)).toBe(true);
    expect(toRequest(named).find((item) => item.code === fresh?.code)?.labelL10n).toEqual({ en: 'X' });
  });

  it('builds preview rows with spans and empty cells for ungrouped columns', () => {
    const rows = previewHeaderRows(groups, [null, 3, 3, 2, 4], (group) => group.code);

    expect(rows).toHaveLength(3);
    expect(rows[0]).toEqual([
      { label: null, span: 1 },
      { label: 'E', span: 3 },
      { label: 'F', span: 1 },
    ]);
    expect(rows[2]).toEqual([
      { label: null, span: 1 },
      { label: 'JAN', span: 2 },
      { label: null, span: 2 },
    ]);
    expect(previewHeaderRows(undefined, [null, null], (group) => group.code)).toEqual([]);
  });
});

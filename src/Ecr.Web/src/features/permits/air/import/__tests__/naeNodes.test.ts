import { afterEach, describe, expect, it, vi } from 'vitest';
import type { NaeTreeNode } from '../../airTypes';
import { nodeOptions } from '../naeNodes';

// Пояс у vmThreads не задати (`dateOnly.test.ts`) — тому «сьогодні людини» підставляється моком: 1 квітня в Атирау.
vi.mock('@/shared/format/dateOnly', () => ({ todayDateOnly: () => '2026-04-01' }));

/**
 * W-7 (аудит 2026-11): «сьогодні» для фільтра чинних вузлів — календарний день людини (`todayDateOnly`), а не UTC
 * (`toISOString`). О 03:00 1 квітня за Атирау (+05:00) у UTC ще 31 березня — вузол, закритий 31.03, пропонувався б як
 * чинний.
 */
describe('nodeOptions: «сьогодні» за місцевим днем', () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  const node = (validTo: string | null): NaeTreeNode => ({
    childCount: 0,
    code: 'AREA_1',
    display: 'A',
    id: 1,
    isActive: true,
    isMatch: false,
    levelCode: 'AREA',
    ordinal: 1,
    parentEntryId: null,
    validFrom: null,
    validTo,
  });

  it('W-7: вузол, закритий учора за місцевим днем, не чинний, хоч у UTC ще «сьогодні»', () => {
    vi.useFakeTimers({ now: new Date('2026-03-31T22:00:00Z') });

    expect(nodeOptions([node('2026-03-31')])).toEqual([]);
  });

  it('вузол, чинний сьогодні за місцевим днем, пропонується', () => {
    vi.useFakeTimers({ now: new Date('2026-03-31T22:00:00Z') });

    expect(nodeOptions([node('2026-04-01')])).toEqual([{ value: '1', label: 'A' }]);
  });
});

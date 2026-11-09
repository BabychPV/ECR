import { describe, expect, it } from 'vitest';
import type { TableSliceDto } from '@/api/types';
import { rowDisplayName } from '../rowName';

/** Fresh eyes 09.11: our texts never show the hex row key — the label, else «row N · permit». */
describe('rowDisplayName', () => {
  const slice = {
    rows: [
      { rowKey: '33732b706603476f8a1b', label: null },
      { rowKey: 'R2', label: 'Sea Water Intake' },
    ],
  } as unknown as TableSliceDto;

  it('a labelled row is named by its label', () => {
    expect(rowDisplayName(slice, 'R2', 'KZ-A')).toBe('Sea Water Intake');
  });

  it('a dynamic row is «row N · permit», never its hex key', () => {
    const name = rowDisplayName(slice, '33732b706603476f8a1b', 'KZ-A');
    expect(name).toMatch(/1/);
    expect(name).toContain('KZ-A');
    expect(name).not.toContain('33732b');
  });

  it('an unknown row is not named by its key either', () => {
    expect(rowDisplayName(undefined, '33732b706603476f8a1b', null)).not.toContain('33732b');
  });
});

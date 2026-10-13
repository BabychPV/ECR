import { describe, expect, it } from 'vitest';
import { latestPublishedVersionId } from '../CreateProjectModal';

const v = (id: number, status: string, publishedAt: string | null) => ({ id, status, publishedAt });

describe('latestPublishedVersionId', () => {
  it('порожній перелік → null', () => {
    expect(latestPublishedVersionId([])).toBeNull();
  });

  it('одна опублікована версія', () => {
    expect(latestPublishedVersionId([v(7, 'Published', '2026-10-01T00:00:00Z')])).toBe('7');
  });

  it('непубліковані ігноруються, навіть новіші', () => {
    expect(
      latestPublishedVersionId([
        v(1, 'Published', '2026-09-01T00:00:00Z'),
        v(2, 'Draft', null),
        v(3, 'Deprecated', '2026-10-05T00:00:00Z'),
      ]),
    ).toBe('1');
  });

  it('лише чернетки → null', () => {
    expect(latestPublishedVersionId([v(2, 'Draft', null)])).toBeNull();
  });

  it('обирає найпізнішу дату публікації між шаблонами', () => {
    expect(
      latestPublishedVersionId([
        v(10, 'Published', '2026-10-02T00:00:00Z'),
        v(4, 'Published', '2026-10-09T00:00:00Z'),
        v(8, 'Published', '2026-10-03T00:00:00Z'),
      ]),
    ).toBe('4');
  });

  it('за рівної дати чи без дати — більший id', () => {
    expect(
      latestPublishedVersionId([v(3, 'Published', null), v(5, 'Published', null)]),
    ).toBe('5');
    expect(
      latestPublishedVersionId([
        v(9, 'Published', '2026-10-02T00:00:00Z'),
        v(6, 'Published', '2026-10-02T00:00:00Z'),
      ]),
    ).toBe('9');
  });
});

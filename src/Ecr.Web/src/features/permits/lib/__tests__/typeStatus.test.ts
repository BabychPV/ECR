import { describe, expect, it } from 'vitest';
import { hasPublishedVersion, isNeverPublished } from '../typeStatus';

describe('typeStatus', () => {
  it('Draft з version 1 (як віддає сервер) — ніколи не публікувався, версія не вирішує', () => {
    const draft = { status: 'Draft', version: 1 };
    expect(isNeverPublished(draft)).toBe(true);
    expect(hasPublishedVersion(draft)).toBe(false);
  });

  it('Published (навіть з відкритою чернеткою нової версії) — публікувався', () => {
    expect(isNeverPublished({ status: 'Published' })).toBe(false);
    expect(hasPublishedVersion({ status: 'Published' })).toBe(true);
  });
});

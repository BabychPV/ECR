import { describe, expect, it } from 'vitest';
import { readSeedEnCatalog } from '@/test/seedSql';
import { documentCountText, recalcProgressText, type RecalcProgress } from '../recalcProgress';

/**
 * ✎ debt-1109 x1: банер і тост перерахунку писали «Queued 1 documents» — число стояло перед словом у реченні
 * каталогу. Тепер «N документів» — окремий рядок із категоріями множини (`recalcProgress.documentCount.*`).
 */
const base: RecalcProgress = {
  jobId: 'j1',
  projectId: 7,
  projectCode: 'PRJ-7',
  phase: 'queued',
  total: 1,
  done: 0,
  skipped: 0,
  failed: 0,
  recalculated: 0,
  updatedAt: '2026-11-09T10:00:00Z',
};

describe('x1: множина кількості документів у перерахунку', () => {
  it('один документ — категорія one; три — other; невідомо — other з «—»', () => {
    expect(documentCountText(1)).toContain('recalcProgress.documentCount.one');
    expect(documentCountText(3)).toContain('recalcProgress.documentCount.other');
    expect(documentCountText(null)).toContain('count=—');
  });

  it('банер «Queued …» для одного документа підставляє форму one', () => {
    expect(recalcProgressText(base)).toContain('documentCount=⟦recalcProgress.documentCount.one');
    expect(recalcProgressText({ ...base, phase: 'running', done: 1, total: 1 })).toContain(
      'documentCount=⟦recalcProgress.documentCount.one',
    );
  });

  it('en-каталог: жодного «{n} documents» у реченнях перерахунку; форми one/other є', () => {
    const catalog = readSeedEnCatalog();
    const sentences = [...catalog.matchAll(/\(N'(recalcProgress\.[A-Za-z.]+)',\s*N'en',\s*N'([^']*)'/gu)];

    expect(sentences.length).toBeGreaterThan(10);
    for (const [, key, text] of sentences) {
      if (key?.startsWith('recalcProgress.documentCount.') === true) continue;
      expect(text, key).not.toMatch(/\{[a-zA-Z]+\} documents/u);
    }
    expect(catalog).toContain("(N'recalcProgress.documentCount.one', N'en', N'{count} document', 1)");
    expect(catalog).toContain("(N'recalcProgress.documentCount.other', N'en', N'{count} documents', 1)");
  });
});

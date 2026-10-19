import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it, vi } from 'vitest';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { problemText } from '../problemText';

/**
 * Таблиця текстів вад (PM-08b): кожен випадок валідатора має речення в каталозі.
 *
 * Мутаційний доказ (виконано вручну, відновлено): прибраний `case …tableWithoutKey` у `problemText.ts`
 * валить «кожен випадок валідатора має рядок»; випадок, дописаний у `draftValidate.ts` без рядка тут,
 * падає там само (тест читає файл валідатора).
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));

const source = readFileSync(path.resolve(__dirname, '../draftValidate.ts'), 'utf8');
const cases = [...new Set([...source.matchAll(/problem\(\s*'([A-Za-z]+)'/g)].map((match) => match[1]!))];

describe('problemText', () => {
  it('валідатор має принаймні двадцять випадків (регулярка читає файл правильно)', () => {
    expect(cases.length).toBeGreaterThanOrEqual(20);
  });

  it.each(cases)('«%s» має речення в каталозі', (name) => {
    const text = problemText({ messageKey: `err.ECR-PRM-0422.${name}`, params: {} });
    expect(text).not.toContain('⟦');
    expect(text).not.toBe(`err.ECR-PRM-0422.${name}`);
  });

  it('підставляє параметри', () => {
    expect(problemText({ messageKey: 'err.ECR-PRM-0422.tableWithoutKey', params: { tableCode: 'Storage' } })).toContain('"Storage"');
  });

  it('невідомий ключ показується як є', () => {
    expect(problemText({ messageKey: 'err.ECR-PRM-0422.unheardOf', params: {} })).toBe('err.ECR-PRM-0422.unheardOf');
  });
});

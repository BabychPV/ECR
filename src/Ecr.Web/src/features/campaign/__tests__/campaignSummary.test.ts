import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  campaignSummary,
  isCampaignTruncated,
  type CampaignSummary,
} from '@/features/campaign/api';

/**
 * Огляд кампанії звітності (`BE-22`).
 *
 * ⛔ Головне, що тут перевіряється, — адреса ОДНА і без фільтра проєктів.
 * Рішення людини на `Q15-07` дає доступ окремим правом `Report.ViewCampaign`,
 * а не грантами; клієнт, який дописав би `projectId`, тихо перетворив би огляд
 * кампанії назад на перегляд власних проєктів.
 */

const original = globalThis.fetch;

afterEach(() => {
  globalThis.fetch = original;
});

/** Порожня відповідь сервера; повертає надіслану адресу й метод. */
function capture(): { url: () => string; method: () => string | undefined } {
  let url = '';
  let method: string | undefined;

  globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    url = String(input);
    method = init?.method;

    return new Response(JSON.stringify({ periodKey: 202601, totalProjects: 0, projects: [] }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    });
  }) as typeof globalThis.fetch;

  return { url: () => url, method: () => method };
}

/** Відповідь із заданим числом проєктів у переліку і в лічильнику. */
function summaryOf(listed: number, total: number): CampaignSummary {
  return {
    periodKey: 202601,
    totalProjects: total,
    projects: Array.from({ length: listed }, (_, i) => ({
      projectId: i + 1,
      projectCode: `P${String(i + 1)}`,
      // ⚠ `LocalizedText` їде як `{ values: … }`, а не як голий словник:
      // саме так його серіалізує сервер, і саме цього чекає `localized()`.
      nameL10n: { values: { en: `Project ${String(i + 1)}` } },
      documents: 0,
      draft: 0,
      submitted: 0,
      approved: 0,
      rejected: 0,
      snapshots: 0,
      progress: 'InProgress',
      submissionDeadline: null,
    })),
    totals: {
      projects: total,
      documents: 0,
      draft: 0,
      submitted: 0,
      approved: 0,
      rejected: 0,
      snapshots: 0,
      done: 0,
      overdue: 0,
      atRisk: 0,
      inProgress: total,
    },
  };
}

describe('огляд кампанії', () => {
  it('це GET на адресу кампанії з періодом і без фільтра проєктів', async () => {
    const sent = capture();

    await campaignSummary(202601);

    expect(sent.url()).toBe('/api/v1/campaign/summary?periodKey=202601');
    expect(sent.method()).toBeUndefined();
  });

  it('обрізаний стелею перелік видно за totalProjects, а не за довжиною', () => {
    // ⚠ Саме ці два числа й розрізняють «кампанія з 200 проєктів» і
    // «показано 200 із 300». Без порівняння екран мовчав би про другу сотню.
    expect(isCampaignTruncated(summaryOf(200, 300))).toBe(true);
    expect(isCampaignTruncated(summaryOf(200, 200))).toBe(false);
  });
});

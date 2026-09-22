import { describe, expect, it } from 'vitest';
import type { CampaignProgress, CampaignProject } from '@/features/campaign/api';
import { holdingUp, lastSubmissionDay } from '@/features/campaign/campaignView';

/**
 * Подання серверної класифікації кампанії.
 *
 * ⛔ `lastSubmissionDay` рахує дату за годинником ЗСУВУ з рядка. Кейси біля
 * півночі зі зсувом на схід від UTC і Києва падають, якщо дату взяти в поясі
 * браузера; кейси на межі місяця й року — якщо «мінус доба» зроблено
 * рядковими маніпуляціями, а не календарем.
 */

describe('останній день подання', () => {
  it.each([
    ['2026-06-16T00:00:00+05:00', '2026-06-15'],
    ['2026-06-16T00:00:00+06:00', '2026-06-15'],
    ['2026-03-01T00:00:00+05:00', '2026-02-28'],
    ['2027-01-01T00:00:00+06:00', '2026-12-31'],
    ['2026-06-16T00:00:00-05:00', '2026-06-15'],
    ['2026-06-16T00:00:00Z', '2026-06-15'],
    ['2026-06-16T00:00:00.000+05:00', '2026-06-15'],
    ['2026-06-16T00:00+05:00', '2026-06-15'],
  ])('%s → %s', (deadline, day) => {
    expect(lastSubmissionDay(deadline)).toBe(day);
  });

  it('межі ще немає (null) — дати немає', () => {
    expect(lastSubmissionDay(null)).toBeNull();
  });

  it('рядок без зсуву чи не момент — дати немає, а не вгадана', () => {
    expect(lastSubmissionDay('2026-06-16T00:00:00')).toBeNull();
    expect(lastSubmissionDay('2026-06-16')).toBeNull();
    expect(lastSubmissionDay('не дата')).toBeNull();
  });
});

describe('хто затримує', () => {
  function p(id: number, progress: CampaignProgress): CampaignProject {
    return {
      projectId: id,
      projectCode: `P${String(id)}`,
      nameL10n: { values: { en: `P${String(id)}` } },
      documents: 1,
      draft: 0,
      submitted: 0,
      approved: 0,
      rejected: 0,
      snapshots: 0,
      progress,
      submissionDeadline: null,
    };
  }

  it('лише Overdue і AtRisk; прострочені вище; всередині класу — порядок сервера', () => {
    const rows = holdingUp([p(1, 'AtRisk'), p(2, 'Done'), p(3, 'Overdue'), p(4, 'InProgress'), p(5, 'AtRisk'), p(6, 'Overdue')]);

    expect(rows.map((row) => row.projectId)).toEqual([3, 6, 1, 5]);
  });

  it('дзеркало: лише Done і InProgress — не затримує ніхто', () => {
    expect(holdingUp([p(1, 'Done'), p(2, 'InProgress')])).toEqual([]);
  });
});

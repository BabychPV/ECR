import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { PeriodCalendarDto } from '@/api/types';
import { PeriodPolicyManager } from '@/features/projects/PeriodPolicyManager';
import { formatDate } from '@/shared/format';
import { loadCatalog } from '@/shared/i18n';

/**
 * Приймання 08.11, f5: «policy with a date preview». Діалог політик показував лише зсуви в днях — щоб зрозуміти,
 * коли закриється місяць, людина мусила рахувати сама.
 *
 * ⛔ Мутаційний доказ: без `<PolicyDatePreview>` у `PeriodPolicyManager.tsx` перші два випадки червоні (прев'ю
 * немає), а без `setLastEdited`/чернетки в `offsets` — червоний другий (дата не рухається за введенням).
 */
const Strings: Record<string, string> = {
  'periods.managePolicies': 'Manage policies',
  'periods.policyHardClose': 'Hard close',
  'periods.policyPreview.title': 'Dates for {project} with policy {policy}:',
  'periods.policyPreview.newTitle': 'Dates for {project} if it used this new policy:',
  'periods.policyPreview.sample': 'Dates for policy {policy} on a sample month:',
  'periods.policyPreview.sampleNew': 'Dates for this new policy on a sample month:',
  'periods.policyPreview.period': '{period}: opens {opens} · late from {late} · locks on {locks}',
  'periods.policyPreview.year': 'Year edits until {date}.',
  'periods.policyPreview.orderInvalid': 'Late edits would start after the lock.',
  'periods.policyPreview.usedBy': 'Used by {project}',
  'periods.policyPreview.lockedStay': 'Months already locked stay locked; reopen them on the Periods tab.',
};

const policies = [
  { id: 7, code: 'STD', openOffsetDays: 0, graceOffsetDays: 15, hardCloseOffsetDays: 45, yearGraceOffsetDays: 45 },
  { id: 8, code: 'OTHER', openOffsetDays: 0, graceOffsetDays: 5, hardCloseOffsetDays: 10, yearGraceOffsetDays: 0 },
];

const calendar = {
  projectId: 5,
  periodKind: 'Monthly',
  currentPeriodMode: 'Auto',
  timeZoneId: 'Asia/Atyrau',
  policy: policies[0],
  periods: [
    { id: 9, periodKey: 202609, year: 2026, sequence: 9, state: 'Closed', isCurrent: false, periodEnd: '2026-09-30', startsAt: '2026-09-01T00:00:00+05:00', graceEndsAt: null, endsAt: '2026-11-14T00:00:00+05:00', reopenedUntil: null },
    { id: 10, periodKey: 202610, year: 2026, sequence: 10, state: 'Grace', isCurrent: true, periodEnd: '2026-10-31', startsAt: '2026-10-01T00:00:00+05:00', graceEndsAt: null, endsAt: '2026-12-15T00:00:00+05:00', reopenedUntil: null },
    { id: 11, periodKey: 202611, year: 2026, sequence: 11, state: 'Open', isCurrent: false, periodEnd: '2026-11-30', startsAt: '2026-11-01T00:00:00+05:00', graceEndsAt: null, endsAt: '2027-01-14T00:00:00+05:00', reopenedUntil: null },
    { id: 12, periodKey: 202612, year: 2026, sequence: 12, state: 'Scheduled', isCurrent: false, periodEnd: '2026-12-31', startsAt: '2026-12-01T00:00:00+05:00', graceEndsAt: null, endsAt: '2027-02-14T00:00:00+05:00', reopenedUntil: null },
  ],
} as unknown as PeriodCalendarDto;

/** Дата так, як її показує прев'ю (та сама дата майданчика, мова продукту). */
function shown(y: number, m: number, d: number): string {
  return formatDate(new Date(Date.UTC(y, m - 1, d)), { dateStyle: 'medium', timeZone: 'UTC' });
}

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Strings });
      if (url.includes('/period-policies')) return json(policies);
      return json(null);
    }),
  );
}

async function open(props: Parameters<typeof PeriodPolicyManager>[0]): Promise<void> {
  mockFetch();
  await loadCatalog('en', 'private');
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <PeriodPolicyManager {...props} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  fireEvent.click(screen.getByRole('button', { name: 'Manage policies' }));
  await screen.findByText('STD');
}

function previewLine(id: string, periodKey: number): string {
  const box = document.getElementById(id);
  return box?.querySelector(`[data-policy-preview-period="${String(periodKey)}"]`)?.textContent ?? '';
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('PeriodPolicyManager — прев\'ю дат (f5)', () => {
  it('для проєкту: поточний і наступний період, дати за серверним правилом', async () => {
    await open({ calendar, projectCode: 'PRJ' });

    expect(await screen.findByText('Dates for PRJ with policy STD:')).toBeDefined();
    expect(screen.getByText('Used by PRJ')).toBeDefined();

    // Жовтень: пізні з 31.10 + 15 = 15.11, закрито з 31.10 + 45 = 15.12.
    const october = previewLine('period-policy-preview', 202610);
    expect(october).toContain(`late from ${shown(2026, 11, 15)}`);
    expect(october).toContain(`locks on ${shown(2026, 12, 15)}`);
    // Листопад відкривається 01.11 (зсув 0), закривається 30.11 + 45 = 14.01.2027.
    const november = previewLine('period-policy-preview', 202611);
    expect(november).toContain(`opens ${shown(2026, 11, 1)}`);
    expect(november).toContain(`locks on ${shown(2027, 1, 14)}`);
    // Вікно року: 31.12.2026 + 45 → 14.02.2027.
    expect(document.querySelector('#period-policy-preview [data-policy-preview-year]')?.textContent).toBe(
      `Year edits until ${shown(2027, 2, 14)}.`,
    );
  });

  it('живцем: зміна «Hard close» одразу міняє дату закриття', async () => {
    await open({ calendar, projectCode: 'PRJ' });
    await screen.findByText('Dates for PRJ with policy STD:');

    const [stdHardClose] = screen.getAllByRole('textbox', { name: 'Hard close' });
    fireEvent.change(stdHardClose!, { target: { value: '60' } });

    // 31.10 + 60 = 30.12.2026.
    await waitFor(() =>
      expect(previewLine('period-policy-preview', 202610)).toContain(`locks on ${shown(2026, 12, 30)}`),
    );
  });

  // ✎ F13 e1 (09.11): збереження відпущеної політики повертає Late → Open, а заблоковані місяці — ні; прев'ю каже це
  // ДО збереження. Мутація: прибрати рядок `lockedStay` з `PolicyDatePreview` — червоний.
  it('попереджає, що заблоковані місяці лишаються заблокованими', async () => {
    await open({ calendar, projectCode: 'PRJ' });
    await screen.findByText('Dates for PRJ with policy STD:');

    expect(document.querySelector('#period-policy-preview [data-policy-preview-locked-stay]')?.textContent).toBe(
      'Months already locked stay locked; reopen them on the Periods tab.',
    );
  });

  it('без проєкту — зразковий місяць і чесний підпис', async () => {
    await open({});

    expect(screen.getByText('Dates for this new policy on a sample month:')).toBeDefined();
    expect(document.getElementById('period-policy-new-preview')?.getAttribute('data-policy-preview')).toBe('sample');
    // Рядкового прев'ю без проєкту й без редагування немає — нічого вигаданого.
    expect(document.getElementById('period-policy-preview')).toBeNull();
  });
});

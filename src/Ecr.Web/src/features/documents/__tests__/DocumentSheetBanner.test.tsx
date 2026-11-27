import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentSheetBanner } from '@/features/documents/DocumentSheetBanner';
import type { DocumentLock } from '@/features/documents/documentLock';
import { testTheme } from '@/test/render';

/**
 * UI-26: банер стану аркуша з контекстом «хто, коли, що далі».
 *
 * ⛔ Хто й коли — лише з журналу ЦЬОГО аркуша; журналу немає — без імені.
 */

const History = [
  // Найновіші перші; подія іншого аркуша з тим самим станом — не наша.
  { action: 'Submit', at: '2026-09-18T08:00:00Z', byDisplayName: 'Other Person', fromState: 'Draft', reason: null, sheetCode: 'HID', stepOrdinal: null, toState: 'Submitted' },
  { action: 'Submit', at: '2026-09-17T08:00:00Z', byDisplayName: 'D. Akhmetova', fromState: 'Draft', reason: null, sheetCode: 'GEN', stepOrdinal: null, toState: 'Submitted' },
  { action: 'Reject', at: '2026-09-16T08:00:00Z', byDisplayName: 'A. Approver', fromState: 'Submitted', reason: 'Fix row 4', sheetCode: 'GEN', stepOrdinal: null, toState: 'Rejected' },
];

const requestedUrls: string[] = [];

function stubHistory(events: unknown[] | 'fail'): void {
  requestedUrls.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      requestedUrls.push(String(input));

      return events === 'fail'
        ? new Response(JSON.stringify({ status: 500 }), { status: 500, headers: { 'Content-Type': 'application/json' } })
        : new Response(JSON.stringify(events), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
}

function show(
  state: string,
  lock: DocumentLock | null,
  canDecide = false,
  sheetCopyId: number | null = null,
  recalculatedAfterSubmission = false,
  part: 'all' | 'chips' | 'alerts' = 'all',
): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <DocumentSheetBanner
          documentId={1}
          periodKey={202610}
          sheetCode="GEN"
          sheetCopyId={sheetCopyId}
          state={state}
          lock={lock}
          canDecide={canDecide}
          recalculatedAfterSubmission={recalculatedAfterSubmission}
          part={part}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentSheetBanner', () => {
  it('✎ UAT-recalc-ux: поданий аркуш, перерахований після подання, — позначка для погоджувача; без факту — немає', async () => {
    stubHistory(History);
    show('Submitted', 'sheetSubmitted', true, null, true);

    await screen.findByText(/document\.banner\.submittedBy/);
    const banner = screen.getByTestId('document-sheet-banner');
    expect(banner.querySelector('[data-recalculated-after-submission]')?.textContent).toContain(
      '⟦sheet.recalculatedAfterSubmission⟧',
    );
  });

  it('✎ UAT-recalc-ux: без перерахунку після подання позначки немає', async () => {
    stubHistory(History);
    show('Submitted', 'sheetSubmitted', true);

    await screen.findByText(/document\.banner\.submittedBy/);
    expect(screen.getByTestId('document-sheet-banner').querySelector('[data-recalculated-after-submission]')).toBeNull();
  });

  it('поданий аркуш для погоджувача: хто й коли з журналу ЦЬОГО аркуша, і крок «вирішіть»', async () => {
    stubHistory(History);
    show('Submitted', 'sheetSubmitted', true);

    const banner = await screen.findByText(/document\.banner\.submittedBy/);
    // ⛔ Мутаційний доказ: без фільтра за `sheetCode` тут було б «Other Person».
    expect(banner.textContent).toContain('name=D. Akhmetova');
    expect(screen.getByTestId('document-sheet-banner').textContent).toContain('⟦document.banner.submittedDecide⟧');
    expect(screen.getByTestId('document-sheet-banner').getAttribute('data-document-lock')).toBe('sheetSubmitted');
  });

  it('RS: банер копії бере подію САМЕ копії і питає журнал з sheetCopyId; оригінал — без параметра', async () => {
    const copyHistory = [
      // Найновіша подія того ж коду й стану — але оригіналу; копія — нижче.
      { ...History[1], at: '2026-09-19T08:00:00Z', byDisplayName: 'Original Author', sheetCopyId: null },
      { ...History[1], at: '2026-09-18T09:00:00Z', byDisplayName: 'Copy Author', sheetCopyId: 77 },
    ];
    stubHistory(copyHistory);
    show('Submitted', 'sheetSubmitted', true, 77);

    const banner = await screen.findByText(/document\.banner\.submittedBy/);
    // ⛔ Мутаційний доказ: без звірки `sheetCopyId` тут було б «Original Author».
    expect(banner.textContent).toContain('name=Copy Author');
    expect(requestedUrls.some((url) => url.includes('/workflow/history?') && url.includes('sheetCopyId=77'))).toBe(true);
  });

  it('RS: банер оригіналу не бере подію копії і не шле sheetCopyId', async () => {
    stubHistory([
      { ...History[1], at: '2026-09-19T08:00:00Z', byDisplayName: 'Copy Author', sheetCopyId: 77 },
      { ...History[1], at: '2026-09-18T09:00:00Z', byDisplayName: 'Original Author', sheetCopyId: null },
    ]);
    show('Submitted', 'sheetSubmitted', true);

    const banner = await screen.findByText(/document\.banner\.submittedBy/);
    expect(banner.textContent).toContain('name=Original Author');
    expect(requestedUrls.filter((url) => url.includes('sheetCopyId'))).toEqual([]);
  });

  it('автор подання чекає — інший крок', async () => {
    stubHistory(History);
    show('Submitted', 'sheetSubmitted', false);

    await screen.findByText(/document\.banner\.submittedBy/);
    expect(screen.getByTestId('document-sheet-banner').textContent).toContain('⟦document.banner.submittedWait⟧');
  });

  it('журнал не прочитано — заголовок без вигаданого імені', async () => {
    stubHistory('fail');
    show('Submitted', 'sheetSubmitted', true);

    // ✎ DOCUX L8: заголовок — підпис чипа («… ⓘ»), пояснення — прихованим текстом у ньому ж.
    expect(await screen.findByText(/^⟦document\.banner\.submitted⟧ ⓘ/)).toBeDefined();
    expect(screen.queryByText(/submittedBy/)).toBeNull();
  });

  it('відхилений аркуш: причина й що робити далі', async () => {
    stubHistory(History);
    show('Rejected', null);

    await screen.findByText(/document\.banner\.rejectedBy/);
    expect(screen.getByTestId('document-sheet-banner').textContent).toContain('«Fix row 4»');
  });

  it('ширша причина (закритий період) — старий банер без контексту аркуша', () => {
    stubHistory(History);
    show('Submitted', 'periodClosed', true);

    expect(screen.queryByTestId('document-sheet-banner')).toBeNull();
    expect(document.querySelector('[data-document-lock="periodClosed"]')).not.toBeNull();
  });

  it('чернетка без блокування — банера немає', () => {
    stubHistory(History);
    show('Draft', null);

    expect(screen.queryByRole('status')).toBeNull();
  });

  it('DOCUX L8 (Q7): поданий аркуш — ЧИП «Submitted … by …» (не Alert), пояснення й крок — у ⓘ і для читалки', async () => {
    stubHistory(History);
    show('Submitted', 'sheetSubmitted', false, null, false, 'chips');

    const chip = await screen.findByText(/document\.banner\.submittedBy/);
    expect(chip.closest('button')).not.toBeNull();
    const banner = screen.getByTestId('document-sheet-banner');
    expect(banner.getAttribute('data-sheet-state-chip')).toBe('Submitted');
    expect(banner.querySelector('.mantine-Alert-root')).toBeNull();
    expect(document.querySelector('.mantine-Alert-root')).toBeNull();
    expect(banner.textContent).toContain('⟦document.banner.submittedWait⟧');
  });

  it('DOCUX L8: частини — чип лише в `chips`, «Rejected» з причиною — лише в `alerts`', async () => {
    stubHistory(History);
    show('Submitted', 'sheetSubmitted', false, null, false, 'alerts');
    expect(screen.queryByTestId('document-sheet-banner')).toBeNull();
    cleanup();

    show('Rejected', null, false, null, false, 'chips');
    expect(screen.queryByTestId('document-sheet-banner')).toBeNull();
    cleanup();

    show('Rejected', null, false, null, false, 'alerts');
    expect((await screen.findByTestId('document-sheet-banner')).closest('.mantine-Alert-root')).not.toBeNull();
  });

  it('RC b18 (keyboardPath): назва кнопки-чипа — лише заголовок; пояснення й крок — її опис (aria-describedby), не назва', async () => {
    stubHistory(History);
    show('Submitted', 'sheetSubmitted', true, null, false, 'chips');

    const chip = (await screen.findByText(/document\.banner\.submittedBy/)).closest('button') as HTMLElement;
    expect(chip.textContent).not.toContain('submittedDecide');
    const described = document.getElementById(chip.getAttribute('aria-describedby') ?? '');
    expect(described?.textContent).toContain('⟦document.banner.submittedDecide⟧');
    expect(screen.queryByRole('button', { name: /submittedDecide/ })).toBeNull();
  });
});


import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import type { JSX } from 'react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { components } from '@/api/schema';
import { DocumentLockBanner } from '@/features/documents/DocumentLockBanner';
import { documentLockOf } from '@/features/documents/documentLock';
import { useDocumentPeriodReopen } from '@/features/documents/periodReopen/useDocumentPeriodReopen';
import { testTheme } from '@/test/render';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { formatDateTime } from '@/shared/format/datetime';
import { siteUntilText } from '@/shared/siteMoment';
import { showDone } from '@/shared/ui/notify';

vi.mock('@/shared/ui/notify', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/ui/notify')>()),
  showDone: vi.fn(),
}));

/**
 * Приймальна 08.11 (i): «Reopen January 2026 for this document…» у банері закритого періоду (i1), недоступна дія з
 * причиною словами (i6), банер «Reopened for this document until …» і редагованість документа, поки виняток діє (i2).
 */

type PeriodState = components['schemas']['PeriodState'];

const Future = '2099-01-31T19:00:00Z';

interface Call {
  readonly url: string;
  readonly method: string;
  readonly body: unknown;
}

const calls: Call[] = [];

function stub(state: Record<string, unknown>): void {
  calls.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const method = init?.method ?? 'GET';
      calls.push({ url: String(input), method, body: init?.body === undefined ? null : JSON.parse(String(init.body)) });

      const payload =
        method === 'POST'
          ? { id: 9, periodKey: 202601, openUntil: Future, reason: 'Fix', openedByUserId: 3, openedByName: null, openedAt: Future }
          : state;

      return new Response(JSON.stringify(payload), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
}

function Harness({ periodState, zone }: { readonly periodState: PeriodState; readonly zone?: string | undefined }): JSX.Element {
  const reopen = useDocumentPeriodReopen({ documentId: 5, periodKey: 202601, periodState, timeZoneId: zone });
  const lock = documentLockOf({ projectStatus: 'Active', periodState: reopen.periodState, sheetState: 'Draft' });

  return (
    <>
      <span data-testid="effective-state">{reopen.periodState}</span>
      <DocumentLockBanner lock={lock} periodKey={202601} periodAction={reopen.lockAction} />
      {reopen.banner}
      {reopen.dialog}
    </>
  );
}

function show(periodState: PeriodState, zone?: string): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <Harness periodState={periodState} zone={zone} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

const closedState = (overrides: Record<string, unknown>): Record<string, unknown> => ({
  periodKey: 202601,
  active: null,
  canOpen: true,
  denial: null,
  defaultOpenUntil: Future,
  ...overrides,
});

/** Кнопка недоступна (Mantine ставить `disabled` на `<button>`). */
function disabled(element: HTMLElement): boolean {
  return (element as HTMLButtonElement).disabled;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('useDocumentPeriodReopen', () => {
  it('i1: у банері закритого періоду — «Reopen … for this document…»; діалог каже, що відкривається лише цей документ, і без причини не підтверджує', async () => {
    stub(closedState({}));
    show('Closed');

    const action = await screen.findByTestId('document-period-reopen');
    expect(action.textContent).toContain('document.reopen.action');
    // Місяць — словами («January 2026»), а не ключем 202601.
    expect(action.textContent).toContain('period=January 2026');
    expect(disabled(action)).toBe(false);
    // Дія — у банері закритого періоду.
    expect(action.closest('[data-document-lock="periodClosed"]')).not.toBeNull();

    fireEvent.click(action);

    expect((await screen.findByTestId('document-period-reopen-scope')).textContent).toContain('document.reopen.dialogScope (period=January 2026)');
    const confirm = screen.getByTestId('document-period-reopen-confirm');
    // ⛔ Мутація: прибрати перевірку порожньої причини — кнопка активна, червоне.
    expect(disabled(confirm)).toBe(true);

    fireEvent.change(screen.getByTestId('document-period-reopen-reason'), { target: { value: '  Лист №17  ' } });
    expect(disabled(confirm)).toBe(false);
    fireEvent.click(confirm);

    await waitFor(() => expect(calls.some((c) => c.method === 'POST')).toBe(true));
    const post = calls.find((c) => c.method === 'POST');
    expect(post?.url).toBe('/api/v1/documents/5/period-reopen');
    // Строк за замовчуванням — із сервера (кінець доби майданчика); причина — без крайніх пробілів.
    expect(post?.body).toEqual({ periodKey: 202601, reason: 'Лист №17', openUntil: new Date(Future).toISOString() });
  });

  /*
   * ✎ AN-178: хук передає діалогу пояс майданчика — тост «місяць відкрито до …» тим самим правилом, що банер.
   * Future (19:00Z) — північ 1 лютого в Атирау (+05:00): «Jan 31, 2099, 11:59 PM»; у поясі браузера (UTC) — «7:00 PM».
   * ⛔ Мутація: прибрати `timeZoneId={timeZoneId}` у `useDocumentPeriodReopen` — тост у поясі браузера, червоне.
   */
  it('AN-178: тост після відкриття — строк у поясі майданчика, переданому хуком у діалог', async () => {
    vi.mocked(showDone).mockClear();
    stub(closedState({}));
    show('Closed', 'Asia/Atyrau');

    fireEvent.click(await screen.findByTestId('document-period-reopen'));
    fireEvent.change(await screen.findByTestId('document-period-reopen-reason'), { target: { value: 'Fix' } });
    fireEvent.click(screen.getByTestId('document-period-reopen-confirm'));

    await waitFor(() => expect(vi.mocked(showDone)).toHaveBeenCalled());
    const toast = String(vi.mocked(showDone).mock.calls[0]?.[0] ?? '').replace(/[  ]/gu, ' ');
    expect(toast).toContain('document.reopen.done');
    expect(toast).toContain('until=Jan 31, 2099, 11:59 PM');
  });

  it('i6: без права дія видима, але недоступна, і причина — видимим текстом', async () => {
    stub(closedState({ canOpen: false, denial: 'NoPermission' }));
    show('Closed');

    const action = await screen.findByTestId('document-period-reopen');
    // ⛔ Мутація: показувати кнопку активною без права — червоне.
    expect(disabled(action)).toBe(true);
    expect(screen.getByTestId('document-period-reopen-denied').textContent).toContain('⟦document.reopen.noPermission⟧');
  });

  it('i6 (приймальна P2): причина відмови — словами, без коду права', () => {
    const text = Catalog['document.reopen.noPermission'];
    expect(text).toBe('Only a user allowed to reopen periods in this project can reopen a locked month. Ask your period manager.');
    expect(text).not.toMatch(/Period\.Reopen/);
  });

  it('i2: діючий виняток — банер «Reopened for this document until …», документ не замкнений (Grace), дії відкриття немає', async () => {
    stub(closedState({
      active: { id: 9, periodKey: 202601, openUntil: Future, reason: 'Fix', openedByUserId: 3, openedByName: 'A. Admin', openedAt: Future },
    }));
    show('Closed');

    const banner = await screen.findByTestId('document-period-reopened');
    expect(banner.textContent).toContain('document.reopen.activeTitle');
    expect(banner.textContent).toContain('A. Admin');
    // ⛔ Мутація: не піднімати стан до Grace — банер закритого періоду лишається, і сітка була б замкнена.
    expect(screen.getByTestId('effective-state').textContent).toBe('Grace');
    expect(document.querySelector('[data-document-lock="periodClosed"]')).toBeNull();
    expect(screen.queryByTestId('document-period-reopen')).toBeNull();
  });

  it('p3-1110: строк у банері — у поясі майданчика, тим самим форматом, що й рядок місяця «Проєкти → Періоди»', async () => {
    // 19:00 UTC = 00:00 наступної доби в Атирау (+05:00); ✎ AN-169: північ — «Nov 10, 11:59 PM» (кінець доби), а не час браузера.
    const until = '2026-11-10T19:00:00Z';
    stub(closedState({
      active: { id: 9, periodKey: 202601, openUntil: until, reason: 'Fix', openedByUserId: 3, openedByName: 'A. Admin', openedAt: until },
    }));
    show('Closed', 'Asia/Atyrau');

    const site = siteUntilText(until, 'Asia/Atyrau');
    expect(site).not.toBeNull();
    // Тест має сенс лише тоді, коли пояс браузера дає інший рядок.
    expect(site).not.toBe(formatDateTime(until));

    const banner = await screen.findByTestId('document-period-reopened');
    // ⛔ Мутація: `formatDateTime` замість поясу майданчика — у банері час браузера, червоне.
    expect(banner.textContent).toContain(`until=${String(site)}`);
    // ✎ AN-169: ⛔ Мутація: `siteMomentText` замість `siteUntilText` — «until=Nov 11, 2026, 12:00 AM», червоне.
    expect(banner.textContent?.replace(/[  ]/gu, ' ')).toContain('until=Nov 10, 2026, 11:59 PM');
  });

  it('закритий місяць, стан ще вантажиться: місце дії вже зайняте (без зсуву панелі листа нижче)', async () => {
    calls.length = 0;
    // Відповідь не приходить: стан винятку «в дорозі».
    vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>(() => undefined)));
    show('Closed');

    // ⛔ Мутація: прибрати заглушку на час завантаження — кнопка з'явиться пізніше й зсуне сітку (e2e sheetBottom), червоне.
    const pending = await screen.findByTestId('document-period-reopen-pending');
    expect(pending.closest('[data-document-lock="periodClosed"]')).not.toBeNull();
    expect(disabled(pending)).toBe(true);
    expect(screen.queryByTestId('document-period-reopen')).toBeNull();
  });

  it('відкритий місяць: стану винятку не питає й нічого не показує', async () => {
    stub(closedState({}));
    show('Open');

    await waitFor(() => expect(screen.getByTestId('effective-state').textContent).toBe('Open'));
    expect(calls).toHaveLength(0);
    expect(screen.queryByTestId('document-period-reopen')).toBeNull();
  });
});

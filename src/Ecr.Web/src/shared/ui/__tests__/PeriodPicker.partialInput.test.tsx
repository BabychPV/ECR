import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { JSX } from 'react';
import { PeriodPicker, isCompletePeriodKey } from '@/shared/ui/PeriodPicker';
import { DocumentsPage } from '@/pages/DocumentsPage';
import { CampaignOverviewPage } from '@/pages/admin/CampaignOverviewPage';
import { renderWithMantine, testTheme } from '@/test/render';

/**
 * Неповний набір у «Period» не йде ні в `onChange`, ні в мережу.
 *
 * ⛔ Живий стенд (2026-09-24): набір `202608` у полі давав на `/` п'ять
 * `GET /api/v1/documents/summary?periodKey=2|20|202|2026|20260` і на
 * `/admin/campaign` п'ять `GET /api/v1/campaign/summary?periodKey=…` — усі
 * `422` від `PeriodKey.Parse`. Предмет перевірки — ЛІЧИЛЬНИК запитів, а не
 * вигляд поля: поле й до виправлення показувало набране правильно.
 *
 * ⛔ Мутаційний доказ: у `PeriodPicker.handleInput` замінити
 * `isCompletePeriodKey(next)` на `true` — червоніють усі тести нижче, що
 * рахують виклики (`onChange` отримує `2`, `20`, …; мережа — п'ять `422`-адрес).
 */

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

describe('isCompletePeriodKey — те саме правило, що PeriodKey.IsValid на сервері (місячний)', () => {
  it('повний ключ — рівно шість цифр, місяць 01..12', () => {
    expect(isCompletePeriodKey(202608)).toBe(true);
    expect(isCompletePeriodKey(190001)).toBe(true);
    expect(isCompletePeriodKey(999912)).toBe(true);
  });

  it('проміжні цифри набору, місяць поза 01..12, рік поза 1900..9999 і дроби — не ключ', () => {
    for (const partial of [2, 20, 202, 2026, 20260]) expect(isCompletePeriodKey(partial)).toBe(false);
    expect(isCompletePeriodKey(202600)).toBe(false);
    expect(isCompletePeriodKey(202613)).toBe(false);
    expect(isCompletePeriodKey(189912)).toBe(false);
    expect(isCompletePeriodKey(2026081)).toBe(false);
    expect(isCompletePeriodKey(202608.5)).toBe(false);
  });
});

describe('PeriodPicker: неповний набір лишається в полі', () => {
  it('набір 202608 по цифрі — onChange РІВНО ОДИН раз, із повним ключем', async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();

    function Host(): JSX.Element {
      return <PeriodPicker value={null} onChange={onChange} />;
    }

    renderWithMantine(<Host />);
    const input = screen.getByRole('textbox', { name: '⟦documents.period⟧' });

    await user.type(input, '20260');
    // Поле показує набране — навіть коли далі нічого не пішло.
    expect((input as HTMLInputElement).value).toBe('20260');
    expect(onChange).not.toHaveBeenCalled();

    await user.type(input, '8');
    expect(onChange.mock.calls).toEqual([[202608]]);
  });

  it('неповний набір без червоної помилки; blur повертає поле до чинного періоду', async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();
    renderWithMantine(<PeriodPicker value={202512} onChange={onChange} />);
    const input = screen.getByRole('textbox', { name: '⟦documents.period⟧' });

    await user.type(input, '2026', { initialSelectionStart: 0, initialSelectionEnd: 6 });
    expect((input as HTMLInputElement).value).toBe('2026');
    expect(input.getAttribute('aria-invalid')).not.toBe('true');
    // Підпис попереднього періоду не стоїть під чужим набором.
    expect(screen.queryByText('December 2025')).toBeNull();

    fireEvent.blur(input);
    expect((input as HTMLInputElement).value).toBe('202512');
    expect(screen.getByText('December 2025')).toBeTruthy();
    expect(onChange).not.toHaveBeenCalled();
  });

  it('стрілка під час неповного набору крокує від ЧИННОГО періоду, як раніше', async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();
    renderWithMantine(<PeriodPicker value={202512} onChange={onChange} />);
    const input = screen.getByRole('textbox', { name: '⟦documents.period⟧' });

    await user.type(input, '20', { initialSelectionStart: 0, initialSelectionEnd: 6 });
    fireEvent.click(screen.getByRole('button', { name: '⟦period.next⟧' }));

    expect(onChange.mock.calls).toEqual([[202601]]);
  });
});

/* ── Мережа: той самий лічильник, що на живому стенді ─────────────────── */

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

const me = {
  denies: [],
  grants: {},
  isSimulation: false,
  language: 'en',
  mustChangePassword: false,
  permissions: ['Report.ViewCampaign'],
  simulatedForUserId: null,
  userId: 1,
  userName: 'tester',
};

/** Значення `periodKey` кожного запиту до `summaryPath`, у порядку надходження. */
function countSummary(summaryPath: string): string[] {
  const seen: string[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input), 'http://x');

      if (url.pathname === summaryPath) seen.push(url.searchParams.get('periodKey') ?? '');
      if (url.pathname === '/api/v1/me') return json(me);
      if (url.pathname === '/api/v1/documents/summary') {
        return json({ draft: 0, submitted: 0, approved: 0, rejected: 0, withIssues: 0 });
      }
      if (url.pathname === '/api/v1/campaign/summary') return json({ totalProjects: 0, projects: [], totals: {} });
      if (url.pathname === '/api/v1/documents' || url.pathname === '/api/v1/projects') {
        return json({ items: [], nextCursor: null, totalCount: 0 });
      }

      return json(null);
    }),
  );

  return seen;
}

function show(page: JSX.Element, path: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[path]}>
        <QueryClientProvider client={client}>{page}</QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

describe('лічильник запитів при наборі періоду', () => {
  it(
    '`/` (перелік документів): набір 202608 — 0 запитів на неповні, 1 на повний',
    async () => {
      const seen = countSummary('/api/v1/documents/summary');
      const user = userEvent.setup();
      show(<DocumentsPage />, '/');

      const input = await screen.findByRole('textbox', { name: '⟦documents.period⟧' }, { timeout: SlowEnvTimeout });
      await user.type(input, '20260');
      await new Promise((resolve) => setTimeout(resolve, 100));
      expect(seen).toEqual([]);

      await user.type(input, '8');
      await waitFor(() => expect(seen).toEqual(['202608']));
    },
    SlowEnvTimeout,
  );

  it(
    '`/admin/campaign`: набір 202608 поверх 202512 — жодного запиту з неповним ключем',
    async () => {
      const seen = countSummary('/api/v1/campaign/summary');
      const user = userEvent.setup();
      show(<CampaignOverviewPage />, '/admin/campaign?periodKey=202512');

      const input = await screen.findByRole('textbox', { name: '⟦documents.period⟧' }, { timeout: SlowEnvTimeout });
      await waitFor(() => expect(seen).toEqual(['202512']));

      await user.type(input, '202608', { initialSelectionStart: 0, initialSelectionEnd: 6 });
      await waitFor(() => expect(seen).toEqual(['202512', '202608']));
    },
    SlowEnvTimeout,
  );
});

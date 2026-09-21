import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { t } from '@/shared/i18n';
import { testTheme } from '@/test/render';
import { SearchLauncher } from '@/features/search/SearchLauncher';

/**
 * Палітра й межа частоти пошуку (BE-19): `429` з кодом `ECR-REQ-0429`.
 *
 * ⚠ Тіло відмови — рівно те, що пише сервер (`LoginRateLimiting.RejectAsync`):
 * строк лише в заголовку `Retry-After`, у тілі його немає. Код — літералом,
 * так само як `ErrorCodes.TooManyRequests` на сервері.
 *
 * ⚠ Час — фейковий: палітра відкривається на справжніх таймерах, далі
 * вмикаються фейкові, і кожен запит записує момент (`Date.now()`), тож «чекає
 * `Retry-After`» перевіряється числом, а не відчуттям.
 */

const original = globalThis.fetch;

afterEach(() => {
  vi.useRealTimers();
  cleanup();
  globalThis.fetch = original;
});

const Hits = [{ kind: 'document', id: 42, code: 'DOC-42', title: 'Permit 2026' }];

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

/** `429` рівно такої форми, як відповідає сервер на межі пошуку. */
function limited(retryAfter?: number): Response {
  const headers: Record<string, string> = { 'Content-Type': 'application/problem+json' };
  if (retryAfter !== undefined) headers['Retry-After'] = String(retryAfter);

  return new Response(
    JSON.stringify({
      type: 'https://ecr.ncoc.kz/errors/ECR-REQ-0429',
      title: 'Too many requests',
      status: 429,
      detail: 'Too many searches, try again shortly.',
      instance: '/api/v1/search',
      errorCode: 'ECR-REQ-0429',
      correlationId: 'cid-429',
    }),
    { status: 429, headers },
  );
}

interface Call {
  readonly term: string;
  readonly at: number;
}

/** Підміняє мережу; `reply` отримує текст і номер запиту до `/api/v1/search` (з 1). */
function serve(reply: (term: string, n: number) => Response): { calls: Call[] } {
  const calls: Call[] = [];

  globalThis.fetch = vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(String(input), 'http://x');
    if (url.pathname !== '/api/v1/search') return json([]);

    const term = url.searchParams.get('q') ?? '';
    calls.push({ term, at: Date.now() });

    return reply(term, calls.length);
  }) as typeof globalThis.fetch;

  return { calls };
}

function mount(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/units']}>
          <SearchLauncher />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Відкриває палітру на справжніх таймерах, далі час — фейковий. */
async function openPalette(): Promise<HTMLElement> {
  const user = userEvent.setup();
  await user.click(screen.getByRole('button', { name: t('search.open') }));
  // ⚠ Запас на холодний `import()` палітри під навантаженням повного прогону:
  // типової 1 с `findByRole` там не вистачає.
  const input = await screen.findByRole('combobox', { name: t('search.open') }, { timeout: 5_000 });
  await waitFor(() => expect(document.activeElement).toBe(input), { timeout: 5_000 });

  // ⚠ Без власного `toFake`: список у `vite.config.ts` (`toNotFake`) — розширення
  // дефолту vitest, і з `toFake` разом він не працює.
  vi.useFakeTimers();

  return input;
}

/** Просуває фейковий час і дає відпрацювати мережі та React. */
async function advance(ms: number): Promise<void> {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
  for (let i = 0; i < 5; i += 1) {
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });
  }
}

function type(input: HTMLElement, value: string): void {
  fireEvent.change(input, { target: { value } });
}

function statusText(): string {
  return screen.getByRole('status').textContent ?? '';
}

describe('палітра: межа частоти пошуку (ECR-REQ-0429)', { timeout: 20_000 }, () => {
  it('429 — не ErrorAlert, а рядок стану; повтор рівно через Retry-After', async () => {
    const net = serve((_term, n) => (n === 1 ? limited(3) : json(Hits)));
    mount();
    const input = await openPalette();

    type(input, 'Permit');
    await advance(200);

    expect(net.calls.map((call) => call.term)).toEqual(['Permit']);
    // ⛔ Межа частоти — не аварія й не «нічого не знайдено».
    expect(screen.queryByRole('alert')).toBeNull();
    expect(statusText()).toBe(t('search.rateLimited', { seconds: 3 }));
    expect(statusText()).not.toContain(t('search.empty'));

    // До строку — жодного запиту.
    await advance(2_900);
    expect(net.calls).toHaveLength(1);

    await advance(200);
    expect(net.calls.map((call) => call.term)).toEqual(['Permit', 'Permit']);
    const [first, second] = net.calls;
    expect((second?.at ?? 0) - (first?.at ?? 0)).toBeGreaterThanOrEqual(3_000);

    expect(screen.getByRole('listbox')).toBeTruthy();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('без Retry-After — дефолтні 2 с', async () => {
    const net = serve((_term, n) => (n === 1 ? limited() : json(Hits)));
    mount();
    const input = await openPalette();

    type(input, 'Permit');
    await advance(200);

    expect(statusText()).toBe(t('search.rateLimited', { seconds: 2 }));
    await advance(1_900);
    expect(net.calls).toHaveLength(1);

    await advance(200);
    expect(net.calls).toHaveLength(2);
    const [first, second] = net.calls;
    expect((second?.at ?? 0) - (first?.at ?? 0)).toBeGreaterThanOrEqual(2_000);
  });

  it('текст змінився під час очікування — повторюється НОВИЙ текст, один раз', async () => {
    const net = serve((_term, n) => (n === 1 ? limited(3) : json(Hits)));
    mount();
    const input = await openPalette();

    type(input, 'Permit');
    await advance(200);

    // Набір під час очікування мережу не чіпає — навіть після затримки введення.
    type(input, 'Permix');
    await advance(1_000);
    expect(net.calls).toHaveLength(1);

    await advance(2_500);
    await advance(5_000);

    expect(net.calls.map((call) => call.term)).toEqual(['Permit', 'Permix']);
  });

  it('три 429 поспіль — звичайна відмова, і повтори припиняються', async () => {
    const net = serve(() => limited(1));
    mount();
    const input = await openPalette();

    type(input, 'Permit');
    await advance(200);
    expect(screen.queryByRole('alert')).toBeNull();

    await advance(1_100);
    expect(net.calls).toHaveLength(2);
    expect(screen.queryByRole('alert')).toBeNull();

    await advance(1_100);
    expect(net.calls).toHaveLength(3);
    expect(screen.getByRole('alert')).toBeTruthy();
    expect(statusText()).not.toContain(t('search.empty'));

    await advance(30_000);
    expect(net.calls).toHaveLength(3);
  });

  it('закриття під час очікування скасовує таймер: після закриття запитів немає', async () => {
    const net = serve((_term, n) => (n === 1 ? limited(5) : json(Hits)));
    mount();
    const input = await openPalette();

    type(input, 'Permit');
    await advance(200);
    expect(net.calls).toHaveLength(1);

    fireEvent.keyDown(input, { key: 'Escape' });
    await advance(500);
    expect(net.calls).toHaveLength(1);

    // Повторне відкриття ДО старого строку й той самий текст: рівно один
    // запит — від введення, а не від забутого таймера.
    fireEvent.click(screen.getByRole('button', { name: t('search.open'), hidden: true }));
    await advance(300);
    type(screen.getByRole('combobox', { name: t('search.open') }), 'Permit');
    await advance(200);
    expect(net.calls).toHaveLength(2);

    await advance(10_000);
    expect(net.calls.map((call) => call.term)).toEqual(['Permit', 'Permit']);
  });

  it('палітра закрита — за 10 с жодного запиту', async () => {
    const net = serve(() => limited(2));
    mount();
    const input = await openPalette();

    type(input, 'Permit');
    await advance(200);
    fireEvent.keyDown(input, { key: 'Escape' });

    await advance(10_000);
    expect(net.calls).toHaveLength(1);
  });

  it('інша відмова — як і раніше ErrorAlert, без автоматичного повтору', async () => {
    const net = serve(
      () =>
        new Response(
          JSON.stringify({ title: 'Boom', status: 500, errorCode: 'ECR-SYS-0500', correlationId: 'c' }),
          { status: 500, headers: { 'Content-Type': 'application/problem+json', 'Retry-After': '1' } },
        ),
    );
    mount();
    const input = await openPalette();

    type(input, 'Permit');
    await advance(200);

    expect(screen.getByRole('alert')).toBeTruthy();
    await advance(10_000);
    expect(net.calls).toHaveLength(1);
  });
});

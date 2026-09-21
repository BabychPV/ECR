import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { TestDataSourceModal } from '@/features/integration/TestDataSourceModal';

/**
 * «Test connection» (директива №15 §3 `UI-09`).
 *
 * ⛔ Три відповіді сервера — три РІЗНІ банери:
 *  - `200 { ok: true }` — успіх із кількістю сутностей;
 *  - `200 { ok: false }` — відповідь КАНАЛУ, а не збій запиту: банер відмови з
 *    причиною джерела, без коду кореляції;
 *  - `409` — проба цього з'єднання вже йде.
 */
const Path = '/api/v1/data-sources/7/test';

let calls: { url: string; body: unknown }[] = [];

function respond(reply: () => Response): void {
  calls = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), 'http://localhost').pathname;

      calls.push({ url, body: init?.body === undefined ? undefined : JSON.parse(String(init.body)) });

      return reply();
    }),
  );
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function show(): { onClose: ReturnType<typeof vi.fn> } {
  const onClose = vi.fn();
  const client = new QueryClient({ defaultOptions: { mutations: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <TestDataSourceModal opened sourceId={7} sourceName="Main PI server" onClose={onClose} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return { onClose };
}

function verb(): HTMLButtonElement {
  return screen.getByRole('button', { name: /sources\.testConnection/ });
}

/** Вписує причину й тисне дієслово. */
function runWith(reason: string): void {
  fireEvent.change(screen.getByRole('textbox'), { target: { value: reason } });
  fireEvent.click(verb());
}

async function outcome(): Promise<HTMLElement> {
  return waitFor(() => {
    const node = document.querySelector<HTMLElement>('[data-test-outcome]');
    expect(node, 'банер результату').not.toBeNull();

    return node as HTMLElement;
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('TestDataSourceModal', () => {
  it('L6: фокус на Cancel; дієслово вимкнене, доки причини немає', async () => {
    respond(() => json({ ok: true, entities: 1, error: null }));
    show();

    await waitFor(() => expect(document.activeElement).not.toBe(document.body));
    expect(document.activeElement).toBe(screen.getByRole('button', { name: /common\.cancel/ }));

    expect(verb().disabled).toBe(true);

    // Пробіли — не причина: на сервер пішов би порожній рядок.
    fireEvent.change(screen.getByRole('textbox'), { target: { value: '   ' } });
    expect(verb().disabled).toBe(true);

    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'після заміни сертифіката' } });
    expect(verb().disabled).toBe(false);
  });

  it('причина їде на сервер обрізаною — вона йде в журнал безпеки', async () => {
    respond(() => json({ ok: true, entities: 1, error: null }));
    show();

    runWith('  після заміни сертифіката  ');
    await outcome();

    expect(calls).toEqual([{ url: Path, body: { reason: 'після заміни сертифіката' } }]);
  });

  it('ok: true — банер успіху з кількістю сутностей', async () => {
    respond(() => json({ ok: true, entities: 1250, error: null }));
    show();

    runWith('перевірка');

    const banner = await outcome();
    expect(banner.getAttribute('data-test-outcome')).toBe('ok');

    // Каталогу в тесті немає: `formatCount` дає позначений ключ із ВЖЕ
    // відформатованим числом — тобто число пройшло саме через нього.
    expect(banner.textContent).toContain('sources.testEntities.other');
    expect(banner.textContent).toContain('count=1,250');
  });

  it('ok: false — відмова джерела, а не збій запиту; причина з каталогу за messageKey', async () => {
    respond(() =>
      json({
        ok: false,
        entities: 0,
        error: 'raw adapter text',
        messageKey: 'integration.test.adapterNotRegistered',
      }),
    );
    show();

    runWith('перевірка');

    const banner = await outcome();
    expect(banner.getAttribute('data-test-outcome')).toBe('failed');
    expect(banner.textContent).toContain('integration.test.adapterNotRegistered');
    expect(banner.textContent).not.toContain('raw adapter text');

    // ⛔ Не `ErrorAlert`: жива область одна — сам банер, без «збою» поруч.
    expect(screen.getAllByRole('alert')).toEqual([banner]);
  });

  it('ok: false без messageKey — сирий error джерела', async () => {
    respond(() => json({ ok: false, entities: 0, error: 'connection refused', messageKey: null }));
    show();

    runWith('перевірка');

    const banner = await outcome();
    expect(banner.getAttribute('data-test-outcome')).toBe('failed');
    expect(banner.textContent).toContain('connection refused');
  });

  it('409 — «проба вже йде», а не загальна помилка', async () => {
    // ⚠ Без `errorCode`: гілка за статусом, а код відмови не заведений у
    // каталозі кодів клієнта. `messageKey` робить `detail` показним.
    respond(() =>
      json(
        {
          title: 'Conflicting state',
          status: 409,
          detail: 'A connection test for data source "PI-MAIN" is already running.',
          correlationId: 'c-409',
          messageKey: 'err.ECR-JOB-0409.dataSourceTestRunning',
        },
        409,
      ),
    );
    show();

    runWith('перевірка');

    const banner = await outcome();
    expect(banner.getAttribute('data-test-outcome')).toBe('running');
    expect(banner.textContent).toContain('sources.testRunning');
    expect(banner.textContent).toContain('is already running');
    expect(screen.getAllByRole('alert')).toEqual([banner]);
  });

  it('інша відмова (403) — справжній збій запиту, без банера результату', async () => {
    respond(() => json({ title: 'Forbidden', status: 403, correlationId: 'c-403' }, 403));
    show();

    runWith('перевірка');

    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(document.querySelector('[data-test-outcome]')).toBeNull();
  });
});

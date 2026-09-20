import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ChannelsPanel } from '@/features/notifications/ChannelsPanel';
import { testTheme } from '@/test/render';

/**
 * Канали сповіщень (`BE-33`, екран `/admin/notifications`).
 *
 * ⛔ Два твердження, заради яких цей файл існує:
 *   1. відмова `GET …/channels` не виглядає як «каналів не заведено»
 *      (директива D15 §0, правило L10) — інакше адміністратор заводить канал,
 *      який уже є, а на сусідній панелі та сама відмова забирає вісь матриці
 *      правил;
 *   2. канал БЕЗ секрету названий попередженням, а не порожньою коміркою: він
 *      ввімкнений і не доставить нічого, і дізнаються про це рівно тоді, коли
 *      сповіщення були потрібні.
 */

const Refusal = {
  type: 'about:blank',
  title: 'Internal Server Error',
  status: 500,
  detail: 'перелік каналів прочитати не вдалося',
  errorCode: 'ECR-SYS-0500',
  correlationId: 'cid-channels-1',
  // ⚠ Без ознаки подробиця до екрана не доходить (рішення про мову).
  messageKey: 'err.ECR-SYS-0500.unexpected',
};

const channels = [
  {
    id: 1,
    name: 'Ops mailbox',
    kind: 'Smtp',
    isEnabled: true,
    hasSecret: true,
    modifiedAt: '2026-09-01T10:00:00Z',
    settings: { host: 'smtp.example.org', port: 587, from: 'ecr@example.org', recipients: ['ops@example.org'], useTls: true },
  },
  {
    id: 2,
    name: 'Duty channel',
    kind: 'TeamsWebhook',
    isEnabled: true,
    hasSecret: false,
    modifiedAt: '2026-09-02T10:00:00Z',
    settings: { title: 'ECR' },
  },
];

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json',
    },
  });
}

function mockServer(result: 'ok' | 'refuse' | 'empty'): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';

      if (path.endsWith('/api/v1/notifications/channels')) {
        if (result === 'refuse') return json(Refusal, 500);

        return json(result === 'empty' ? [] : channels);
      }

      return json(null);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <ChannelsPanel />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ChannelsPanel: відмова переліку ≠ «каналів не заведено»', () => {
  it('канали не приїхали — причина з кодом, таблиці НЕМАЄ', async () => {
    mockServer('refuse');
    show();

    const alert = await waitFor(() => screen.getByRole('alert'), { timeout: 10_000 });

    expect(alert.textContent ?? '').toContain('перелік каналів прочитати не вдалося');
    expect(alert.textContent ?? '').toContain('ECR-SYS-0500');
    expect(screen.queryByRole('table')).toBeNull();

    /*
     * ⚠ «Додати канал» ЛИШАЄТЬСЯ доступним, і це рішення, а не недогляд:
     * завести канал при непрочитаному переліку законно, а заборона відняла б
     * роботу, не відвернувши псування даних (дублікат видаляється). Заборонено
     * рівно те, що псує дані, — як у запобіжнику архівації періодів.
     */
    expect(screen.getByText(/notifications\.addChannel/)).toBeDefined();
  }, 30_000);

  it('канали приїхали — таблиця на місці, банера немає', async () => {
    mockServer('ok');
    show();

    // ⚠ Дзеркало: спершу дочекатися самих даних — інакше «банера немає» зелене
    // на будь-якому коді.
    await screen.findByText('Ops mailbox');

    expect(screen.queryByRole('alert')).toBeNull();
  }, 30_000);

  it('канал без секрету названий попередженням, а не порожньою коміркою', async () => {
    mockServer('ok');
    show();

    const row = await screen.findByRole('row', { name: /Duty channel/ });

    // ⛔ Саме в рядку каналу без секрету — пошук по всьому екрану лишався б
    // зеленим, якби попередження стояло не там.
    expect(row.textContent ?? '').toContain('notifications.secretMissing');

    const withSecret = screen.getByRole('row', { name: /Ops mailbox/ });
    expect(withSecret.textContent ?? '').not.toContain('notifications.secretMissing');
  }, 30_000);

  it('каналів справді немає — пояснення, а не порожня таблиця', async () => {
    mockServer('empty');
    show();

    /*
     * ⚠ Каталог у тесті не вантажиться, тож `t()` повертає позначений ключ
     * `⟦notifications.noChannels⟧`. Закриваюча дужка в шаблоні обов'язкова:
     * без неї той самий вираз збігся б і з `noChannelsHint`, і випадок
     * проходив би, навіть якби лишилася сама підказка без заголовка.
     */
    await screen.findByText(/notifications\.noChannels⟧/);

    expect(screen.queryByRole('table')).toBeNull();
    expect(screen.queryByRole('alert')).toBeNull();
  }, 30_000);
});

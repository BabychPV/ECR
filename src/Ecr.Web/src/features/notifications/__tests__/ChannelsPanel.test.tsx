import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
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
    settings: { recipients: ['ops@example.org'] },
    transportFromConfiguration: true,
  },
  {
    id: 2,
    name: 'Duty channel',
    kind: 'TeamsWebhook',
    isEnabled: true,
    hasSecret: false,
    modifiedAt: '2026-09-02T10:00:00Z',
    settings: { title: 'ECR' },
    transportFromConfiguration: false,
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

  it('канал Teams без адреси вебхука названий попередженням, а не порожньою коміркою', async () => {
    mockServer('ok');
    show();

    const row = await screen.findByRole('row', { name: /Duty channel/ });

    // ⛔ Саме в рядку каналу без адреси — пошук по всьому екрану лишався б
    // зеленим, якби попередження стояло не там.
    expect(row.textContent ?? '').toContain('notifications.webhookMissing');
  }, 30_000);

  it('для поштового каналу секрет — «не застосовується», а не «немає секрету»', async () => {
    mockServer('ok');
    show();

    const row = await screen.findByRole('row', { name: /Ops mailbox/ });

    /*
     * ⛔ Перевірено в коді сервера, а не за назвами контракту: секрет каналу
     * читає лише `TeamsWebhookSender` (для нього це адреса вебхука), а
     * `SmtpChannelSender` бере пароль із транспорту процесу. Тому «немає
     * секрету» для пошти читалося б як незавершене налаштування — і
     * адміністратор шукав би пароль, якого цей канал не спитає ніколи.
     */
    expect(row.textContent ?? '').toContain('notifications.notApplicable');
    expect(row.textContent ?? '').not.toContain('notifications.webhookMissing');

    // ⚠ Чому «не застосовується» — опис самої позначки, і вона в порядку
    // табуляції: `title`, яким це пояснювалось раніше, не видно з клавіатури.
    const notApplicable = within(row).getByText(/notifications\.notApplicable/);
    expect(notApplicable.hasAttribute('title')).toBe(false);
    expect(notApplicable.tabIndex).toBe(0);
    expect(
      within(row).queryAllByRole('paragraph', { description: /notifications\.secretNotUsedSmtp/ }),
    ).toContain(notApplicable);

    // ⚠ І дії теж немає: збережена адреса для пошти нікуди не піде.
    expect(within(row).queryByText(/notifications\.setWebhook/)).toBeNull();
  }, 30_000);

  it('форма поштового каналу не показує полів, яких сервер не читає', async () => {
    mockServer('ok');
    show();

    fireEvent.click(await screen.findByText(/notifications\.addChannel/));

    // ⚠ Спершу дочекатися самої форми — інакше твердження про відсутність
    // полів зелене просто тому, що модалка ще не відкрилася.
    await screen.findByLabelText(/notifications\.smtpRecipients/);

    /*
     * ⛔ Полів `host`/`port`/`from`/`useTls` немає ні у формі, ні в контракті
     * (рішення 2026-09-20): транспорт береться з налаштувань застосунку, а
     * спроба зберегти їх — `422`. Показані, вони обіцяли б налаштування, якого
     * не станеться.
     */
    expect(screen.queryByLabelText(/notifications\.smtpHost/)).toBeNull();
    expect(screen.queryByLabelText(/notifications\.smtpPort/)).toBeNull();
    expect(screen.queryByLabelText(/notifications\.smtpFrom/)).toBeNull();
    expect(screen.queryByLabelText(/notifications\.smtpUseTls/)).toBeNull();
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

import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ChannelsPanel } from '@/features/notifications/ChannelsPanel';
import { testTheme } from '@/test/render';

/**
 * Транспорт поштового каналу — із налаштувань застосунку (`c3652cee`), і чи є
 * каналу чим доставляти (`208fb92e`, `transportConfigured`).
 *
 * ⛔ Чотири твердження, заради яких цей файл існує:
 *   1. екран каже, ЗВІДКИ береться транспорт, і каже це за ознакою сервера
 *      `transportFromConfiguration`, а не за видом каналу;
 *   2. попередження «не надсилатиме» — за `transportConfigured === false`, А
 *      НЕ за `transportFromConfiguration`: сервер ніколи не віддає для пошти
 *      `transportFromConfiguration: false`, тож попередження на цій ознаці
 *      мертве (не показується НІКОЛИ) — саме це і виправлено;
 *   3. дві ознаки незалежні: канал може одночасно брати транспорт із
 *      налаштувань застосунку І не мати його налаштованим (порожній
 *      `Smtp:Host`) — обидва рядки на екрані тоді на місці разом;
 *   4. збереження не везе `host`/`port`/`useTls`/`from` — сервер відповів би
 *      на них `422 ECR-REQ-0422`.
 */

/**
 * Рядки каталогу, якими екран каже про транспорт. ⚠ Наявні ключі: новий без
 * рядка в `09-seed.sql` червонить `EndpointCoverageTests` і гейт `a11y`.
 */
const FromConfiguration = 'notifications.smtpTransportHint';
const NotConfigured = 'notifications.test.smtpNotConfigured';

interface Channel {
  readonly id: number;
  readonly name: string;
  readonly kind: 'Smtp' | 'TeamsWebhook';
  readonly isEnabled: boolean;
  readonly hasSecret: boolean;
  readonly modifiedAt: string;
  readonly settings: { readonly recipients?: string[]; readonly title?: string };
  readonly transportFromConfiguration: boolean;
  readonly transportConfigured: boolean;
}

const Mail: Channel = {
  id: 1,
  name: 'Ops mailbox',
  kind: 'Smtp',
  isEnabled: true,
  hasSecret: false,
  modifiedAt: '2026-09-01T10:00:00Z',
  settings: { recipients: ['ops@example.org'], title: 'ECR' },
  transportFromConfiguration: true,
  transportConfigured: true,
};

const Teams: Channel = {
  id: 2,
  name: 'Duty channel',
  kind: 'TeamsWebhook',
  isEnabled: true,
  hasSecret: true,
  modifiedAt: '2026-09-02T10:00:00Z',
  settings: { title: 'ECR' },
  transportFromConfiguration: false,
  transportConfigured: true,
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

/** Тіла запитів на запис каналу — у порядку надходження. */
const sent: unknown[] = [];

function mockServer(channels: readonly Channel[]): void {
  sent.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = String(input).split('?')[0] ?? '';
      const method = (init?.method ?? 'GET').toUpperCase();

      if (/\/api\/v1\/notifications\/channels(\/\d+)?$/.test(path) && method !== 'GET') {
        sent.push(JSON.parse(String(init?.body ?? 'null')));

        return json({ ...Mail, id: 99 });
      }

      if (path.endsWith('/api/v1/notifications/channels')) return json(channels);

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

describe('ChannelsPanel: транспорт пошти — з налаштувань застосунку', () => {
  it('поштовий канал з ознакою сервера — сказано, звідки транспорт; попередження немає', async () => {
    mockServer([Mail, Teams]);
    show();

    const row = await screen.findByRole('row', { name: /Ops mailbox/ });
    const text = row.textContent ?? '';

    expect(text).toContain(FromConfiguration);
    // ⚠ Дзеркало попередження нижче: на тому самому рядку, але з `true`.
    expect(text).not.toContain(NotConfigured);
  }, 30_000);

  it('SMTP-транспорт процесу не налаштовано — попередження «не надсилатиме» поруч із підказкою', async () => {
    mockServer([{ ...Mail, transportConfigured: false }, Teams]);
    show();

    const row = await screen.findByRole('row', { name: /Ops mailbox/ });
    const text = row.textContent ?? '';

    /*
     * ⛔ Ознака читається САМЕ з `transportConfigured`, а не з
     * `transportFromConfiguration` (та лишається `true`) і не з
     * `kind === 'Smtp'`: заміна на будь-яке з двох дала б тут «немає
     * попередження» там, де сервер сказав «доставляти нічим».
     */
    expect(text).toContain(NotConfigured);
    // ⚠ Незалежний факт: канал усе одно бере транспорт із налаштувань
    // застосунку — просто там порожньо. Обидва рядки на місці разом.
    expect(text).toContain(FromConfiguration);
  }, 30_000);

  it('дзеркало: транспорт налаштовано — попередження немає навіть поруч із підказкою', async () => {
    mockServer([{ ...Mail, transportConfigured: true }, Teams]);
    show();

    const row = await screen.findByRole('row', { name: /Ops mailbox/ });
    const text = row.textContent ?? '';

    // ⛔ Тут і попередній тест відрізняються ЛИШЕ `transportConfigured`:
    // мутація «показувати попередження завжди» червонить саме цей тест.
    expect(text).toContain(FromConfiguration);
    expect(text).not.toContain(NotConfigured);
  }, 30_000);

  it('Teams — ні «з налаштувань застосунку», ні попередження про SMTP', async () => {
    mockServer([Mail, Teams]);
    show();

    // ⚠ Спершу дочекатися рядка пошти — інакше «немає» зелене до даних.
    await screen.findByRole('row', { name: /Ops mailbox/ });
    const row = screen.getByRole('row', { name: /Duty channel/ });
    const text = row.textContent ?? '';

    expect(text).not.toContain(FromConfiguration);
    expect(text).not.toContain(NotConfigured);
  }, 30_000);

  it('форма наявного каналу каже те, що сказав сервер, а не статичну підказку', async () => {
    mockServer([{ ...Mail, transportConfigured: false }, Teams]);
    show();

    const row = await screen.findByRole('row', { name: /Ops mailbox/ });
    fireEvent.click(within(row).getByText(/notifications\.editChannel/));

    const dialog = await screen.findByRole('dialog');
    await within(dialog).findByLabelText(/notifications\.smtpRecipients/);

    // ⚠ Попередження — за `transportConfigured` з відповіді сервера;
    // підказка «з налаштувань застосунку» лишається (`transportFromConfiguration`
    // не чіпали), і обидва рядки на формі не суперечать одне одному.
    expect(dialog.textContent ?? '').toContain(NotConfigured);
    expect(dialog.textContent ?? '').toContain(FromConfiguration);
  }, 30_000);

  it('форма нового каналу — відповіді сервера ще немає, тож лишається підказка', async () => {
    mockServer([Mail, Teams]);
    show();

    fireEvent.click(await screen.findByText(/notifications\.addChannel/));

    const dialog = await screen.findByRole('dialog');
    await within(dialog).findByLabelText(/notifications\.smtpRecipients/);

    expect(dialog.textContent ?? '').toContain(FromConfiguration);
    expect(dialog.textContent ?? '').not.toContain(NotConfigured);
  }, 30_000);

  it('збереження не везе host/port/useTls/from — лише адресатів і префікс теми', async () => {
    mockServer([Mail, Teams]);
    show();

    const row = await screen.findByRole('row', { name: /Ops mailbox/ });
    fireEvent.click(within(row).getByText(/notifications\.editChannel/));

    const dialog = await screen.findByRole('dialog');
    await within(dialog).findByLabelText(/notifications\.smtpRecipients/);
    fireEvent.click(within(dialog).getByRole('button', { name: /common\.save/ }));

    await waitFor(() => expect(sent).toHaveLength(1), { timeout: 10_000 });

    const body = sent[0] as { settings?: Record<string, unknown> };

    // ⛔ Точний перелік ключів, а не «немає host»: будь-яке з чотирьох полів
    // транспорту дає `422 …notificationChannelTransportFromConfiguration`.
    expect(Object.keys(body.settings ?? {}).sort()).toEqual(['recipients', 'title']);
    expect(body.settings).toEqual({ recipients: ['ops@example.org'], title: 'ECR' });
  }, 30_000);
});

import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { NotificationsPage } from '@/pages/admin/NotificationsPage';
import type { NotificationChannel, NotificationRuleMatrix } from '@/features/notifications/api';
import { renderWithQuery } from '@/test/render';

/**
 * `/admin/notifications`: заголовок між `ChannelsPanel` і `RulesMatrixPanel`,
 * і власний текст кожного блоку на порожньому стенді.
 *
 * ⛔ Два дефекти, які ловить цей файл.
 *
 * 1. (закрито заголовком між блоками) Без заголовка між блоками два різні
 *    факти — «каналів немає» (`ChannelsPanel`) і «правил немає, бо каналів
 *    немає» (`RulesMatrixPanel`) — виглядали б як один блок, надрукований
 *    двічі.
 * 2. (закрито окремим ключем каталогу) `RulesMatrixPanel` брав чужий текст
 *    `notifications.noChannels` / `notifications.noChannelsHint` — той самий,
 *    що й `ChannelsPanel` над ним, — замість власного
 *    `notifications.rulesNoChannels` / `notifications.rulesNoChannelsHint`.
 *    Навіть із заголовком між блоками це читалось як копіпаста: під написом
 *    «Rules» стояв текст «No channels yet».
 *
 * ⚠ `RulesMatrixPanel` навмисно НЕ малює власного заголовка (коментар над
 * `aria-label` таблиці в `RulesMatrixPanel.tsx`: окремий `<Title>` усередині
 * блоку рвав би `heading-order`, гейти `a11y (dark)`/`a11y (light)`) — «рівень
 * задає сторінка». Тому заголовок — у `NotificationsPage.tsx`, а власний
 * текст блоку — у `RulesMatrixPanel.tsx` (ключі каталогу, `09-seed.sql`).
 *
 * Доказ дефекту (1) побудований на порядку вузлів у DOM, а не лише на
 * наявності заголовка: мутація, яка додає заголовок кудись-небудь (наприклад,
 * під обидва блоки або перед обома), мала б лишити цей тест червоним.
 * Доказ дефекту (2) — на РІЗНИХ ключах під двома заголовками: тест не мокає
 * каталог `ui-strings`, тож `t()` віддає позначений ключ (`⟦ключ⟧`), і
 * мутація, що поверне `RulesMatrixPanel` до `notifications.noChannels`,
 * зробить обидва блоки знову однаковими — тест почервоніє.
 */

const Matrix: NotificationRuleMatrix = {
  eventKinds: ['JobFailed'],
  rules: [],
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

/** Порожній стенд: каналів нуль, правил нуль, доставок нуль — усі три відповіді успішні. */
function mockEmptyStand(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';

      if (path === '/api/v1/notifications/channels') return json([]);
      if (path === '/api/v1/notifications/rules') return json(Matrix);
      if (path === '/api/v1/notifications/deliveries') {
        return json({ items: [], nextCursor: null, totalCount: 0 });
      }

      throw new Error(`Непередбачена адреса: ${path}`);
    }),
  );
}

function show(): void {
  renderWithQuery(<NotificationsPage />);
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('NotificationsPage: заголовок розділяє тексти ChannelsPanel і RulesMatrixPanel, і тексти різні', () => {
  it('порожній стенд — блок Rules показує ВЛАСНИЙ текст, не текст блоку Channels', async () => {
    mockEmptyStand();
    show();

    // ⛔ Головне твердження проти дефекту (2): ключі під заголовками РІЗНІ.
    // До фіксу тут стояв би той самий `⟦notifications.noChannels⟧` двічі.
    const channelsBlock = await waitFor(
      () => screen.getByText('⟦notifications.noChannels⟧'),
      { timeout: 10_000 },
    );
    const rulesBlock = screen.getByText('⟦notifications.rulesNoChannels⟧');

    expect(screen.getByText('⟦notifications.noChannelsHint⟧')).toBeDefined();
    expect(screen.getByText('⟦notifications.rulesNoChannelsHint⟧')).toBeDefined();

    // Заголовок секції правил — з ключа каталогу `notifications.rules`
    // (значення `Rules` у `09-seed.sql`), рівня `<h2>`, як і в `ChannelsPanel`.
    const rulesHeading = screen.getByRole('heading', { name: '⟦notifications.rules⟧' });
    expect(rulesHeading.tagName).toBe('H2');

    // ⛔ Доказ дефекту (1): заголовок стоїть СТРОГО між блоком Channels і
    // блоком Rules, а не деінде на сторінці (до обох чи після обох).
    expect(
      channelsBlock.compareDocumentPosition(rulesHeading) & Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
    expect(
      rulesHeading.compareDocumentPosition(rulesBlock) & Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
  });

  it('порядок заголовків сторінки: h3 «Notifications» → h2 «Channels» → h2 «Rules»', async () => {
    mockEmptyStand();
    show();

    await waitFor(() => {
      expect(screen.getByText('⟦notifications.noChannels⟧')).toBeDefined();
      expect(screen.getByText('⟦notifications.rulesNoChannels⟧')).toBeDefined();
    });

    const headings = screen.getAllByRole('heading');

    expect(headings.map((node) => [node.tagName, node.textContent])).toEqual([
      ['H3', '⟦notifications.title⟧'],
      ['H2', '⟦notifications.channels⟧'],
      ['H2', '⟦notifications.rules⟧'],
    ]);
  });
});

/**
 * Дефект, знайдений ручною перевіркою `/admin/notifications`: `ChannelsPanel`
 * і `RulesMatrixPanel` читали `listNotificationChannels()` під РІЗНИМИ ключами
 * кешу React Query (`['notification-channels']` проти локального
 * `['notifications', 'channels']` у `RulesMatrixPanel`). Після створення
 * каналу `ChannelsPanel` інвалідував лише свій запис — таблиця каналів
 * оновлювалась миттєво, а `RulesMatrixPanel` далі показував «канали не
 * заведено», доки щось інше (повний ремаунт сторінки, `F5`) не форсувало
 * повторний запит.
 *
 * ⛔ Доказ побудований на ОДНОМУ рендері сторінки: `show()` викликається
 * рівно один раз, мутація йде через справжню форму `ChannelsPanel`, а
 * перевірка — на тому самому DOM-дереві. Другий `render()` після мутації
 * замаскував би дефект: новий `QueryClient` не мав би старого (хибного)
 * кеша, і тест був би зеленим і до фіксу.
 */
describe('NotificationsPage: ChannelsPanel і RulesMatrixPanel діляться одним кешем каналів', () => {
  it('новостворений канал одразу видно в RulesMatrixPanel, без перемонтування сторінки', async () => {
    let channels: NotificationChannel[] = [];

    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const path = String(input).split('?')[0] ?? '';
        const method = (init?.method ?? 'GET').toUpperCase();

        if (path === '/api/v1/notifications/channels' && method === 'GET') {
          return json(channels);
        }

        if (path === '/api/v1/notifications/channels' && method === 'POST') {
          const body = JSON.parse(String(init?.body ?? '{}')) as {
            name: string;
            kind: NotificationChannel['kind'];
            settings?: NotificationChannel['settings'];
          };

          const created: NotificationChannel = {
            id: 1,
            name: body.name,
            kind: body.kind,
            isEnabled: true,
            hasSecret: false,
            modifiedAt: '2026-09-23T10:00:00Z',
            settings: body.settings ?? {},
            transportFromConfiguration: true,
            transportConfigured: true,
          };

          channels = [...channels, created];
          return json(created, 201);
        }

        if (path === '/api/v1/notifications/rules') return json(Matrix);

        if (path === '/api/v1/notifications/deliveries') {
          return json({ items: [], nextCursor: null, totalCount: 0 });
        }

        throw new Error(`Непередбачена адреса: ${method} ${path}`);
      }),
    );

    // ⛔ Єдиний виклик рендеру за весь тест — див. коментар над `describe`.
    show();

    // Стартовий, порожній стенд: обидва блоки кажуть «каналів немає» — кожен
    // СВОЇМ ключем (`ChannelsPanel` — `notifications.noChannels`,
    // `RulesMatrixPanel` — `notifications.rulesNoChannels`).
    await waitFor(() => {
      expect(screen.getByText('⟦notifications.noChannels⟧')).toBeDefined();
      expect(screen.getByText('⟦notifications.rulesNoChannels⟧')).toBeDefined();
    });

    fireEvent.click(screen.getByText(/notifications\.addChannel⟧/));

    const nameInput = await screen.findByLabelText(/notifications\.channelName⟧/);
    fireEvent.change(nameInput, { target: { value: 'Ops mailbox' } });

    fireEvent.click(screen.getByText(/common\.save⟧/));

    // `ChannelsPanel` бачить свою ж мутацію — це очікувано й до фіксу.
    await screen.findByRole('cell', { name: 'Ops mailbox' });

    /*
     * ⛔ Головне твердження. До фіксу спільного кешу тут лишалось би
     * `⟦notifications.rulesNoChannels⟧` у `RulesMatrixPanel` — ключ кешу, за
     * яким інвалідувала мутація `ChannelsPanel`, не збігався з ключем, за
     * яким читав `RulesMatrixPanel`, тож другий запит на дані взагалі не йшов.
     */
    await waitFor(() => {
      expect(screen.queryByText('⟦notifications.noChannels⟧')).toBeNull();
      expect(screen.queryByText('⟦notifications.rulesNoChannels⟧')).toBeNull();
    });

    expect(screen.getByRole('columnheader', { name: 'Ops mailbox' })).toBeDefined();
  }, 30_000);
});

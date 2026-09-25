import { afterEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import { NotificationsPage } from '@/pages/admin/NotificationsPage';
import type {
  NotificationChannel,
  NotificationDeliveryPage,
  NotificationRuleMatrix,
} from '@/features/notifications/api';
import { renderWithQuery } from '@/test/render';

/**
 * `X-19` (разом із `X-22` для `/admin/jobs`): три таблиці `/admin/notifications`
 * — канали, матриця правил, журнал доставок — малювались голим Mantine
 * `<Table>` без власного `overflow:auto`. Матриця «подія × канал» додає
 * колонку на КОЖЕН канал (прапорець + `Select` 120 px), і вже на трьох каналах
 * ряд ширший за 1280 px; журнал доставок несе необмежений текст помилки в
 * останній колонці. Без обгортки таблиця розсуває саму СТОРІНКУ, і та
 * скролиться горизонтально ЦІЛКОМ — разом із заголовками над таблицею
 * (`KIT.md` §6.5: «сторінка не скролиться горизонтально: широке — у власному
 * overflow:auto»).
 *
 * ⛔ Мутаційний доказ структурний, тим самим прийомом, що й
 * `PeriodsPage.badgeOverflow.test.tsx`/`SnapshotsPage.auditPass5.test.tsx`:
 * jsdom не рахує layout, тож перевіряється не сам overflow, а те, що його
 * лікує, — кожна з трьох таблиць лежить усередині `.mantine-ScrollArea-root`,
 * а не голою на сторінці. Приберіть `<ScrollArea>` навколо будь-якої з трьох
 * таблиць — відповідний `expect` у цьому файлі падає.
 */

const channel: NotificationChannel = {
  id: 1,
  name: 'Ops mailbox',
  kind: 'Smtp',
  isEnabled: true,
  hasSecret: false,
  modifiedAt: '2026-09-23T10:00:00Z',
  settings: {},
  transportFromConfiguration: true,
  transportConfigured: true,
};

const matrix: NotificationRuleMatrix = {
  eventKinds: ['JobFailed'],
  rules: [],
};

const deliveries: NotificationDeliveryPage = {
  items: [
    {
      id: 1,
      at: '2026-09-23T10:05:00Z',
      eventKind: 'JobFailed',
      eventKey: 'job-1',
      channelId: 1,
      channelName: 'Ops mailbox',
      status: 'Sent',
      error: null,
    },
  ],
  nextCursor: null,
  totalCount: 1,
};

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function mockNonEmptyStand(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';

      if (path === '/api/v1/notifications/channels') return json([channel]);
      if (path === '/api/v1/notifications/rules') return json(matrix);
      if (path === '/api/v1/notifications/deliveries') return json(deliveries);

      throw new Error(`Непередбачена адреса: ${path}`);
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('NotificationsPage: жодна з трьох таблиць не гола на сторінці (X-19)', () => {
  it('канали, матриця правил і журнал доставок — кожна у власному ScrollArea', async () => {
    mockNonEmptyStand();
    renderWithQuery(<NotificationsPage />);

    // ⚠ Три незалежні запити (канали, правила, доставки) розв'язуються не в
    // одному кадрі — чекаємо, доки з'являться всі три таблиці, а не лише
    // перша, що встигла.
    const tables = await waitFor(() => {
      const found = screen.getAllByRole('table');
      expect(found).toHaveLength(3);
      return found;
    });

    for (const table of tables) {
      expect(table.closest('.mantine-ScrollArea-root')).not.toBeNull();
    }
  });
});

import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnapshotsPage } from '@/pages/admin/SnapshotsPage';
import { statusTable } from '@/shared/ui/StatusBadge';
import { testTheme } from '@/test/render';

/**
 * Статус зрізу малює набір (`StatusBadge kind="snapshot"`), а не сторінка.
 *
 * ⛔ Тут стояло `<Badge>{snapshot.status}</Badge>`: користувач бачив код
 * сервера (`Draft`) сирим рядком — не мовою інтерфейсу й одним кольором на всі
 * стани. Стани — `SnapshotStatus` (`Enums.cs`, `D-65`): `Draft`, `Approved`,
 * `Submitted`; `Frozen` нижче — навмисно невідомий.
 *
 * ✎ Файл звався `…statusBadgeMinWidth`: `min-width: fit-content` (аудит-пас 8,
 * lane6, п.9) тепер властивість самого набору, але на цій таблиці вона
 * перевіряється й далі — останнім тестом.
 */
const project = { id: 42, code: 'KASH_2026', status: 'Active' as const };

const states = ['Draft', 'Approved', 'Submitted', 'Frozen'];

const snapshots = states.map((status, index) => ({
  id: index + 1,
  builtAt: '2026-01-15T10:00:00Z',
  contentHash: `abc${String(index)}`,
  isCurrent: false,
  periodKey: 202601,
  projectId: 42,
  reportVersionId: 1,
  rowCount: 10,
  status,
}));

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: [],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (url.includes('/api/v1/reports/snapshots')) return json(snapshots);
      if (url.includes('/api/v1/reports')) return json([]);

      if (url.includes('/api/v1/projects')) {
        return json({ items: [project], nextCursor: null, totalCount: 1 });
      }

      return json(null);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/admin/snapshots']}>
        <QueryClientProvider client={client}>
          <SnapshotsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

function badgeOf(state: string): HTMLElement | null {
  return document.querySelector<HTMLElement>(`[data-status-state="${state}"]`);
}

const SlowEnvTimeout = 400_000;

/** Таблиця домальована: по одній контрольній сумі на рядок. */
async function ready(): Promise<void> {
  await screen.findByText('abc3', {}, { timeout: SlowEnvTimeout });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SnapshotsPage: статус зрізу — бейдж набору, а не код сервера', () => {
  it(
    'кожен стан сервера має бейдж, підпис іде з каталогу, сирого коду як тексту немає',
    async () => {
      mockFetch();
      show();
      await ready();

      /*
       * ⛔ Мутаційний доказ (RED, якщо повернути `{snapshot.status}`): сирий
       * `Badge` не лишає `data-status-*`, а на екрані з'являється текст `Draft`.
       *
       * ⚠ Каталогу цей файл не завантажує, тож `t()` віддає позначений ключ
       * (`D-138`) — саме він доводить, що підпис пройшов через `t()`.
       */
      expect(badgeOf('Draft')?.textContent).toBe('⟦status.snapshot.Draft⟧');
      expect(badgeOf('Submitted')?.textContent).toBe('⟦status.snapshot.Submitted⟧');
      expect(document.querySelectorAll('[data-status-kind="snapshot"]')).toHaveLength(4);

      for (const state of states) {
        expect(screen.queryByText(state), `сирий код «${state}» на екрані`).toBeNull();
      }

      // Розрізнення, а не один колір на всіх: чернетки регулятор не бачить.
      expect(badgeOf('Draft')?.getAttribute('data-status-tone')).toBe('muted');
      expect(badgeOf('Approved')?.getAttribute('data-status-tone')).toBe('neutral');
    },
    SlowEnvTimeout,
  );

  it(
    'невідомий стан не ламає таблицю й позначений невідомим',
    async () => {
      // ⛔ `ReportSnapshotSummary.status` доходить до клієнта простим `string`.
      expect(statusTable.snapshot['Frozen'], 'фікстура має бути невідомим станом').toBeUndefined();

      mockFetch();
      show();
      await ready();

      expect(badgeOf('Frozen')?.getAttribute('data-status-known')).toBe('false');
      expect(badgeOf('Frozen')?.getAttribute('data-status-tone')).toBe('warning');
      expect(badgeOf('Draft')?.getAttribute('data-status-known')).toBe('true');
    },
    SlowEnvTimeout,
  );

  it(
    'бейдж не стискається нижче власного тексту: min-width: fit-content',
    async () => {
      mockFetch();
      show();
      await ready();

      // ⛔ Без цього стилю таблиця стискає бейдж до нечитабельного «D…» замість
      // перемикання ScrollArea на горизонтальну прокрутку (jsdom не рахує
      // розкладку, тому доказ структурний).
      expect(badgeOf('Draft')?.style.minWidth).toBe('fit-content');
    },
    SlowEnvTimeout,
  );
});

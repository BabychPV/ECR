import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ConsistencyIssuesPage } from '@/pages/admin/ConsistencyIssuesPage';
import { testTheme } from '@/test/render';

/**
 * WCAG 2.4.3: причина «Run now» (`ReasonModalChunk` у `ConsistencyIssuesPage`, D-132) — натиснули РАНІШЕ, ніж приїхав
 * чанк діалогу.
 *
 * ⛔ Діалог монтується ВЖЕ відкритим посеред очікування. Фокус повертає власний `useRestoreFocus` `ReasonModal`: він
 * запам'ятовує відкривача на першому відкритому коміті, коли кнопка «Run now» досі у фокусі.
 *
 * ⚠ Ворота — `use(promise)` у підміненому компоненті, а НЕ `await` у фабриці `vi.mock`, і клік — лише після `loaded`
 * (той самий прийом, що `VersionDiffButton.lateChunk.test.tsx`): другий `import()` того самого модуля, поки async-фабрика
 * ще працює, vitest віддавав би справжнім модулем.
 *
 * Мутаційний доказ: прибрати `useRestoreFocus(opened, returnFocusTo)` з `ReasonModal` — тест червоний.
 *
 * ⚠ Каталог не вантажиться: `t()` дає `⟦ключ⟧`.
 */
const chunk = vi.hoisted(() => {
  let release: () => void = () => undefined;
  const ready = new Promise<void>((resolve) => {
    release = resolve;
  });

  return { ready, open: () => release(), loaded: false as boolean };
});

vi.mock('@/shared/ui/ReasonModal', async (importOriginal) => {
  const { createElement, use } = await import('react');
  const actual = await importOriginal<typeof import('@/shared/ui/ReasonModal')>();
  function LateReasonModal(props: Parameters<typeof actual.ReasonModal>[0]): ReturnType<typeof actual.ReasonModal> {
    use(chunk.ready);

    return createElement(actual.ReasonModal, props);
  }

  chunk.loaded = true;

  return { ...actual, ReasonModal: LateReasonModal };
});

const RunNow = '⟦consistency.runNow⟧';

const Finding = {
  id: 7,
  detectedAt: '2026-09-18T03:00:00Z',
  severity: 3,
  ruleCode: 'BROKEN_FK',
  entityType: 'doc.TableRow',
  entityId: 4021,
  message: 'Рядок 4021 посилається на екземпляр таблиці 77 періоду 202601, якого не існує.',
  resolvedAt: null,
  resolvedByUserId: null,
};

function mockApi(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (body: unknown, status = 200): Response =>
        new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

      if (url.endsWith('/api/v1/me')) {
        return json({
          userId: 1,
          userName: 'admin',
          language: 'en',
          permissions: ['System.ViewHealth', 'System.RunJob'],
          isSimulation: false,
          denies: [],
          grants: {},
          mustChangePassword: false,
          simulatedForUserId: null,
        });
      }

      if (url.includes('/api/v1/consistency/issues')) {
        return json({ items: [Finding], nextCursor: null, totalCount: null });
      }

      throw new Error(`Немає заглушки для ${url}`);
    }),
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('ConsistencyIssuesPage: фокус після закриття причини «Run now», коли чанк запізнився', () => {
  it('клік → чанк приїхав → «Cancel» → фокус знову на «Run now»', async () => {
    mockApi();
    render(
      <MantineProvider theme={testTheme}>
        <MemoryRouter initialEntries={['/admin/consistency']}>
          <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
            <ConsistencyIssuesPage />
          </QueryClientProvider>
        </MemoryRouter>
      </MantineProvider>,
    );

    const trigger = await screen.findByRole('button', { name: RunNow });
    await screen.findByText(Finding.message);

    // Обгортка змонтована завжди — чанк вантажиться разом зі сторінкою; клікаємо, коли фабрика вже доробила.
    await waitFor(() => expect(chunk.loaded).toBe(true));
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });

    trigger.focus();
    fireEvent.click(trigger);
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    // Діалогу ще немає: чанк «у дорозі», а `opened` уже `true`.
    expect(screen.queryByRole('dialog')).toBeNull();

    chunk.open();
    const dialog = await screen.findByRole('dialog');
    await waitFor(() => expect(dialog.contains(document.activeElement)).toBe(true));

    fireEvent.click(within(dialog).getByRole('button', { name: /common\.cancel/ }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    await waitFor(() => expect(document.activeElement).toBe(trigger));
  });
});

import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { testTheme } from '@/test/render';
import { GroupsPage } from '../GroupsPage';
import type { PermitGroupDto } from '../groupsApi';

/**
 * WCAG 2.4.3: шторка групи (`GroupDrawer`, лінивий чанк, D-132) — «New group» натиснули РАНІШЕ, ніж приїхав чанк.
 *
 * ⛔ Шторка монтується ВЖЕ відкритою (`?panel=_new`), і `returnFocus` Mantine відкривача не бачить. Повертає фокус
 * `DetailDrawer`: відкривача він читає в рендері на першому відкритому кадрі, тобто коли чанк приїхав, — кнопка «New
 * group» на той час досі у фокусі. Чанк тут тримає `vi.mock` із воротами — шторка монтується лише після кліку.
 *
 * ⚠ Ворота — `use(promise)` у підміненому компоненті, а НЕ `await` у фабриці `vi.mock`: код сам передзавантажує чанк
 * (до кліку), і другий `import()` того самого модуля, поки фабрика ще чекає, vitest віддавав справжнім модулем — діалог
 * з'являвся одразу (CI 40107107198). Для React це той самий стан, що й чанк у дорозі: межа `Suspense` показує
 * `fallback={null}`, компонент монтується вже відкритим лише після `open()`.
 *
 * Мутаційний доказ: прибрати `if (opened) setOpener(focusedOutside(panelId));` у `DetailDrawer` — тест червоний.
 */
const chunk = vi.hoisted(() => {
  let release: () => void = () => undefined;
  const ready = new Promise<void>((resolve) => {
    release = resolve;
  });

  return { ready, open: () => release() };
});

vi.mock('../GroupDrawer', async (importOriginal) => {
  const { createElement, use } = await import('react');
  const actual = await importOriginal<typeof import('../GroupDrawer')>();
  function LateDrawer(props: Parameters<typeof actual.GroupDrawer>[0]): ReturnType<typeof actual.GroupDrawer> {
    use(chunk.ready);

    return createElement(actual.GroupDrawer, props);
  }

  return { ...actual, GroupDrawer: LateDrawer };
});

vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    params === undefined ? key : `${key} ${JSON.stringify(params)}`,
  language: () => 'en',
}));
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { permissions: ['Registry.View', 'Permit.EditType'], isSimulation: false } }),
}));

const Water: PermitGroupDto = {
  code: 'WATER',
  descriptionL10n: {},
  isActive: true,
  nameL10n: { en: 'Water permits' },
  registryCode: 'PRMG_WATER',
  registryDefId: 90,
  rowVersion: 'v1',
  types: [],
  updatedAt: '2026-10-20T10:00:00Z',
  usedInColumns: 0,
  usedInTemplates: 0,
};

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('Groups: фокус після закриття шторки, коли чанк запізнився', () => {
  it('«New group» → чанк приїхав → Esc → фокус знову на «New group»', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);
        if (url.includes('/api/v1/languages')) return json([{ code: 'en', nameNative: 'English', isDefault: true }]);
        if (url.includes('/api/v1/permit-types')) return json({ items: [], nextCursor: null, totalCount: 0 });
        if (url.includes('/api/v1/permit-groups')) return json([Water]);

        return json(null);
      }),
    );
    render(
      <MantineProvider theme={testTheme}>
        <MemoryRouter initialEntries={['/admin/permits/groups']}>
          <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
            <Routes>
              <Route path="/admin/permits/groups" element={<GroupsPage />} />
            </Routes>
          </QueryClientProvider>
        </MemoryRouter>
      </MantineProvider>,
    );
    await screen.findByText('Water permits');
    const opener = await screen.findByRole('button', { name: 'permits.groups.new' });
    opener.focus();

    fireEvent.click(opener);
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    // Шторки ще немає: чанк не приїхав, а `?panel=_new` уже в адресі.
    expect(screen.queryByRole('dialog')).toBeNull();

    chunk.open();
    const drawer = await screen.findByRole('dialog');
    await waitFor(() => expect(drawer.contains(document.activeElement)).toBe(true));

    fireEvent.keyDown(drawer, { key: 'Escape' });
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    await waitFor(() => expect(document.activeElement).toBe(opener));
  });
});

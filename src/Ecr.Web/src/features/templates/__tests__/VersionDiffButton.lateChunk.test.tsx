import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';
import { VersionDiffButton } from '../VersionDiffButton';

/**
 * WCAG 2.4.3: «Порівняти» (`VersionDiffButton`, D-132) — натиснули РАНІШЕ, ніж приїхав чанк діалогу.
 *
 * ⛔ `VersionDiffButton.test.tsx` перевіряє фокус, коли чанк приїздить одразу; тут `vi.mock` із воротами віддає модуль
 * діалогу лише після кліку, тож діалог монтується ВЖЕ відкритим посеред очікування.
 *
 * ⚠ Ворота — `use(promise)` у підміненому компоненті, а НЕ `await` у фабриці `vi.mock`: код сам передзавантажує чанк
 * (до кліку), і другий `import()` того самого модуля, поки фабрика ще чекає, vitest віддавав справжнім модулем — діалог
 * з'являвся одразу (CI 40107107198). Для React це той самий стан, що й чанк у дорозі: межа `Suspense` показує
 * `fallback={null}`, компонент монтується вже відкритим лише після `open()`.
 *
 * Мутаційний доказ: прибрати `useRestoreFocus(opened)` з `VersionDiffDialog` — тест червоний.
 *
 * ⚠ Каталог не вантажиться: `t()` дає `⟦ключ⟧`.
 */
const chunk = vi.hoisted(() => {
  let release: () => void = () => undefined;
  const ready = new Promise<void>((resolve) => {
    release = resolve;
  });

  return { ready, open: () => release() };
});

vi.mock('@/features/templates/VersionDiff', async (importOriginal) => {
  const { createElement, use } = await import('react');
  const actual = await importOriginal<typeof import('@/features/templates/VersionDiff')>();
  function LateDialog(props: Parameters<typeof actual.VersionDiffDialog>[0]): ReturnType<typeof actual.VersionDiffDialog> {
    use(chunk.ready);

    return createElement(actual.VersionDiffDialog, props);
  }

  return { ...actual, VersionDiffDialog: LateDialog };
});

const Versions = [
  { id: 10, version: '1.0', status: 'Published', presentationRevision: 0, clonedFromVersionId: null, publishedAt: null },
  { id: 11, version: '2.0', status: 'Draft', presentationRevision: 0, clonedFromVersionId: 10, publishedAt: null },
] as const;

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('VersionDiffButton: фокус після закриття, коли чанк запізнився', () => {
  it('клік → чанк приїхав → «Close» → фокус знову на «Порівняти»', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } })),
    );
    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <VersionDiffButton templateVersionId={11} versions={Versions} />
        </QueryClientProvider>
      </MantineProvider>,
    );
    const trigger = screen.getByRole('button', { name: '⟦version.diff⟧' });
    // Фокус кличе передзавантаження чанка (`onFocus`) — воно теж іде через підмінений модуль.
    await act(async () => {
      trigger.focus();
      await new Promise((resolve) => setTimeout(resolve, 0));
    });

    fireEvent.click(trigger);
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    expect(screen.queryByRole('dialog')).toBeNull();

    chunk.open();
    const dialog = await screen.findByRole('dialog');
    await waitFor(() => expect(dialog.contains(document.activeElement)).toBe(true));

    fireEvent.click(within(dialog).getByRole('button', { name: '⟦common.close⟧' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    await waitFor(() => expect(document.activeElement).toBe(trigger));
  });
});

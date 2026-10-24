import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { act, cleanup, render, screen, fireEvent, within, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { TemplateVersionPage } from '@/pages/admin/TemplateVersionPage';
import { testTheme } from '@/test/render';

/**
 * WCAG 2.4.3: ліниві `ReasonModal` / `ConfirmModal` конструктора версії (D-132) — натиснули РАНІШЕ, ніж приїхав чанк.
 *
 * ⛔ Сценарій: «More» → «Withdraw from use», а `import()` діалогу ще в дорозі. Пункт меню згасає разом із меню, фокус
 * падає на `BODY`, і лише ПОТІМ приїжджає чанк — `useRestoreFocus` усередині модалки запам'ятовує `BODY`, тобто нічого,
 * і після закриття фокус лишався на `BODY`. Чанк тримає `vi.mock` із воротами — модуль діалогу віддається лише після
 * кліку; кожен модуль вантажиться у файлі раз, тож на модуль — один тест.
 *
 * Мутаційний доказ: прибрати `useRestoreFocus(deprecating)` з `TemplateVersionPage` — червоний перший тест. Другий
 * («Delete sheet», кнопка, що лишається у фокусі) — сторож хука самої модалки: прибрати
 * `useRestoreFocus(opened, returnFocusTo)` з `ConfirmModal` — червоний другий.
 *
 * ⚠ Каталог не вантажиться: `t()` дає `⟦ключ⟧`.
 *
 * ⚠ Ворота — `use(promise)` у підміненому компоненті, а НЕ `await` у фабриці `vi.mock`: код сам передзавантажує чанк
 * (`preloadDialogs`, ще в першому тесті), і другий `import()` того самого модуля, поки фабрика ще чекає, vitest віддавав справжнім модулем — діалог
 * з'являвся одразу (CI 40107107198). Для React це той самий стан, що й чанк у дорозі: межа `Suspense` показує
 * `fallback={null}`, компонент монтується вже відкритим лише після `open()`.
 */
const chunks = vi.hoisted(() => {
  const gate = (): { ready: Promise<void>; open: () => void; loaded: boolean } => {
    let release: () => void = () => undefined;
    const ready = new Promise<void>((resolve) => {
      release = resolve;
    });

    return { ready, open: () => release(), loaded: false };
  };

  return { reason: gate(), confirm: gate() };
});

vi.mock('@/shared/ui/ReasonModal', async (importOriginal) => {
  await chunks.reason.ready;

  return importOriginal();
});
// ⚠ `ConfirmModal` — воротами `use(promise)`, а не `await` у фабриці: див. примітку нижче.
vi.mock('@/shared/ui/ConfirmModal', async (importOriginal) => {
  const { createElement, use } = await import('react');
  const actual = await importOriginal<typeof import('@/shared/ui/ConfirmModal')>();
  function LateConfirm(props: Parameters<typeof actual.ConfirmModal>[0]): ReturnType<typeof actual.ConfirmModal> {
    use(chunks.confirm.ready);

    return createElement(actual.ConfirmModal, props);
  }

  chunks.confirm.loaded = true;

  return { ...actual, ConfirmModal: LateConfirm };
});

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function stubFetch(status: 'Draft' | 'Published'): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/me')) {
        return json({
          userId: 0,
          userName: 'test',
          language: 'en',
          permissions: ['Template.View', 'Template.Edit', 'Template.Publish'],
          isSimulation: false,
        });
      }
      if (url.includes('/structure')) {
        return json({
          isEditable: status === 'Draft',
          presentationRevision: 0,
          groupRules: [],
          templateVersionId: 1,
          sheets: [
            {
              id: 1,
              code: 'SHEET',
              nameL10n: { values: { en: 'Sheet' } },
              isMandatory: true,
              isVisible: true,
              ordinal: 1,
              sheetGroup: null,
              tables: [],
            },
          ],
        });
      }
      if (url.includes('/versions?limit=')) {
        return json({
          items: [{ id: 1, version: '1.0', status, presentationRevision: 0, clonedFromVersionId: null, publishedAt: null }],
          nextCursor: null,
          totalCount: null,
        });
      }

      return json([]);
    }),
  );
}

function renderPage(entry: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[entry]}>
          <Routes>
            <Route path="/admin/templates/:id/versions/:versionId" element={<TemplateVersionPage />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Чанк приїхав → «Cancel» → діалог зник. */
async function releaseThenCancel(release: () => void): Promise<void> {
  // Діалогу ще немає: чанк не приїхав, а стан «відкрито» уже є.
  expect(screen.queryByRole('dialog')).toBeNull();
  release();
  const dialog = await screen.findByRole('dialog');
  await waitFor(() => expect(dialog.contains(document.activeElement)).toBe(true));

  fireEvent.click(within(dialog).getByRole('button', { name: '⟦common.cancel⟧' }));
  await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
}

beforeEach(() => {
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  );
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('TemplateVersionPage: фокус після закриття лінивого діалогу, коли чанк запізнився', () => {
  it('«More» → «Withdraw from use»: меню згасло до приїзду чанка — фокус на кнопці «More», а не на BODY', async () => {
    stubFetch('Published');
    renderPage('/admin/templates/1/versions/1');

    const more = await screen.findByTestId('version-more');
    more.focus();
    fireEvent.click(more);
    const item = await screen.findByRole('menuitem', { name: '⟦version.deprecate⟧' });
    item.focus();
    fireEvent.click(item);
    await waitFor(() => expect(screen.queryByRole('menuitem', { name: '⟦version.deprecate⟧' })).toBeNull());

    await releaseThenCancel(chunks.reason.open);

    await waitFor(() => expect(document.activeElement).toBe(more));
  }, 30_000);

  it('«Delete sheet»: фокус повертається на кнопку видалення', async () => {
    stubFetch('Draft');
    renderPage('/admin/templates/1/versions/1?node=s:SHEET');

    const remove = await screen.findByRole('button', { name: '⟦sheets.delete⟧' });
    remove.focus();
    // ⛔ Гонка, через яку тест «мигав» (CI 40113614883, 40110453615): код сам передзавантажує чанк, і поки async-фабрика
    // `vi.mock` ще працює, другий `import()` того самого модуля (від `lazy` після кліку; `preloadDialogs`) vitest віддає СПРАВЖНІМ модулем —
    // шторка з'являлась одразу, без воріт. Тож клікаємо лише тоді, коли передзавантаження вже дало підмінений модуль.
    await waitFor(() => expect(chunks.confirm.loaded).toBe(true));
    // Ще одна макрозадача — щоб vitest устиг покласти доготований модуль у реєстр.
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });

    fireEvent.click(remove);
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });

    await releaseThenCancel(chunks.confirm.open);

    await waitFor(() => expect(document.activeElement).toBe(remove));
  }, 30_000);
});

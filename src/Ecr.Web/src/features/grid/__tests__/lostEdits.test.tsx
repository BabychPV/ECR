import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest';
import { fireEvent, render, renderHook, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import type { JSX, ReactNode } from 'react';
import { apiFetch, setLoginRedirect } from '@/api/client';
import { useDocumentPending } from '@/features/grid/autosave';
import { putPendingEdit, resetPending } from '@/features/grid/pendingStore';
import { peekLostEdits } from '@/features/grid/lostEdits';
import { queryKeys } from '@/api/queryKeys';
import type { TableSliceDto } from '@/api/types';
import { LoginPage } from '@/pages/LoginPage';

/**
 * Сесія обірвалась (`401`) з незбереженими правками → після входу людина бачить
 * ПРОПОЗИЦІЮ повернути їх.
 *
 * ⚠ Тест навмисно ходить лише публічними шляхами продукту: `useDocumentPending`
 * (як `DocumentPage`), `apiFetch` (справжній `401`), `LoginPage` (справжній
 * сабміт). Жодного прямого читання сховища — інакше мутація «пиши в
 * `localStorage`» пройшла б повз.
 *
 * ⚠ Тут лишається рівно поведінка СТОРІНКИ ВХОДУ. Повний шлях «401 → вхід →
 * документ → правка знову в сховищі незбереженого» доводить
 * `restoreEdits.test.tsx`: він монтує справжній `DocumentPage` і коштує
 * відповідно.
 */

const Owner = 42;
const Stranger = 43;
const DocumentId = 7;

const STRINGS: Record<string, string> = {
  'login.title': 'Environmental Compliance Reporting',
  'login.windows': 'Sign in with Windows',
  'login.or': 'or',
  'login.user': 'User name',
  'login.password': 'Password',
  'login.submit': 'Sign in',
  'login.hint': 'hint',
};

let lang = 0;

/** Мережа: `401` на будь-який API, доки сесію не відкрито входом; `/me` — `meUserId`. */
function stubServer(meUserId: number): void {
  const language = `lost-${String(++lang)}`;
  localStorage.setItem('uiLanguage', language);
  let signedIn = false;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (body: unknown, status = 200): Response =>
        new Response(JSON.stringify(body), {
          status,
          headers: { 'Content-Type': 'application/json' },
        });

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: language, revision: 1, strings: STRINGS });
      }
      if (url.includes('/public/bootstrap')) {
        return json({
          productVersion: '',
          languages: [],
          windowsSignInEnabled: false,
          localSignInEnabled: true,
        });
      }
      if (url.endsWith('/api/v1/login/local')) {
        signedIn = true;
        return new Response(null, { status: 204 });
      }
      if (!signedIn) return new Response(null, { status: 401 });
      if (url.endsWith('/api/v1/me')) return json({ userId: meUserId, userName: 'x' });

      throw new Error(`Непередбачений запит: ${url}`);
    }),
  );
}

function wrapper({ children }: { children: ReactNode }): JSX.Element {
  return <QueryClientProvider client={new QueryClient()}>{children}</QueryClientProvider>;
}

/** Власник `Owner` правив документ, три комірки не встигли піти, сесія впала. */
async function loseThreeEdits(): Promise<void> {
  const { unmount } = renderHook(() => useDocumentPending(DocumentId, Owner), { wrapper });

  for (const column of ['A', 'B', 'C']) {
    putPendingEdit(1, 202609, {
      rowKey: 'r1',
      columnCode: column,
      value: '5',
      isEmpty: false,
      baseVersion: 'AAA=',
    });
  }

  await apiFetch('/api/v1/documents/7').catch(() => undefined);

  // Перезавантаження сторінки: пам'ять зникає.
  unmount();
  resetPending();
}

async function signIn(): Promise<void> {
  render(
    <MantineProvider>
      <MemoryRouter initialEntries={['/login']}>
        <Routes>
          <Route path="/login" element={<LoginPage />} />
          <Route path="/" element={<p>home</p>} />
          <Route path="/documents/:id" element={<p>document page</p>} />
        </Routes>
      </MemoryRouter>
    </MantineProvider>,
  );

  fireEvent.change(await screen.findByLabelText('User name'), { target: { value: 'op' } });
  const password = document.querySelector('input[type="password"]');
  if (password === null) throw new Error('немає поля пароля');
  fireEvent.change(password, { target: { value: 'pw' } });
  fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
}

beforeEach(() => {
  setLoginRedirect(() => {});
  window.history.replaceState(null, '', `/documents/${String(DocumentId)}`);
});

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
  sessionStorage.clear();
  resetPending();
});

describe('незбережені правки при обриві сесії', () => {
  it('401 з незбереженими правками → після входу пропонують повернути 3 зміни', async () => {
    stubServer(Owner);
    await loseThreeEdits();

    // ⛔ Слід — у вкладці (`sessionStorage`), не в браузері назавжди.
    const keys = (store: Storage): string[] =>
      Array.from({ length: store.length }, (_, i) => store.key(i) ?? '');
    expect(keys(localStorage).filter((key) => key.includes('lostEdits'))).toEqual([]);
    expect(keys(sessionStorage).filter((key) => key.includes('lostEdits'))).toHaveLength(1);

    await signIn();

    const alert = await screen.findByRole('alert');
    // ⚠ Рядків `login.restoreEdits.*` у тестовому каталозі навмисно немає (сід
    // заводить інтегратор): `t()` показує ключ `⟦…⟧` разом із параметрами, і
    // саме вони доводять, що пропонують ТРИ зміни в ЦЬОМУ документі.
    expect(alert.textContent).toContain(
      `⟦login.restoreEdits.text (count=3, documentId=${String(DocumentId)})⟧`,
    );

    // Повертає до документа, звідки перенаправили.
    fireEvent.click(screen.getByRole('button', { name: '⟦login.restoreEdits.continue⟧' }));
    expect(await screen.findByText('document page')).toBeDefined();
  });

  /*
   * ⛔ Слід НЕ витрачається показом. Доки він ніс лише факт, показати його раз
   * було правильно — більше з ним не робили нічого. Тепер у ньому самі правки,
   * і стирання на вході знищило б їх рівно в мить, коли людина погодилася їх
   * повернути: `DocumentPage` не знайшов би вже нічого.
   */
  it('перехід на документ сліду не витрачає', async () => {
    stubServer(Owner);
    await loseThreeEdits();
    await signIn();
    await screen.findByRole('alert');
    fireEvent.click(screen.getByRole('button', { name: '⟦login.restoreEdits.continue⟧' }));
    await screen.findByText('document page');
    document.body.innerHTML = '';

    stubServer(Owner);
    await signIn();
    expect(await screen.findByRole('alert')).toBeDefined();
  });

  it('«Відхилити» стирає слід — удруге не пропонують', async () => {
    stubServer(Owner);
    await loseThreeEdits();
    await signIn();
    await screen.findByRole('alert');
    fireEvent.click(screen.getByRole('button', { name: '⟦login.restoreEdits.discard⟧' }));
    await screen.findByText('home');
    document.body.innerHTML = '';

    stubServer(Owner);
    await signIn();
    expect(await screen.findByText('home')).toBeDefined();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('інший користувач на тому ж комп’ютері не бачить чужої втрати', async () => {
    stubServer(Owner);
    await loseThreeEdits();

    stubServer(Stranger);
    await signIn();

    expect(await screen.findByText('home')).toBeDefined();
    expect(screen.queryByRole('alert')).toBeNull();
    expect(document.body.textContent).not.toContain('login.restoreEdits');
  });

  it('без незбережених правок 401 нічого не лишає', async () => {
    stubServer(Owner);
    const { unmount } = renderHook(() => useDocumentPending(DocumentId, Owner), { wrapper });
    await apiFetch('/api/v1/documents/7').catch(() => undefined);
    unmount();

    expect(sessionStorage.length).toBe(0);
  });

  /*
   * ⛔ G1-08: `baseVersion` правки — версія на мить введення, а не надсилання. Правка
   * над ВЛАСНИМ успішним збереженням того ж рядка (кеш уже `BBB=`) після входу
   * виглядала б «чужою зміною» і `planRestore` викидав би її як конфлікт.
   */
  describe('G1-08: версія рядка в сліді', () => {
    function cachedSlice(rowVersion: string, cellA: unknown): TableSliceDto {
      return {
        tableInstanceId: 1,
        periodKey: 202609,
        columns: [],
        rows: [
          { rowKey: 'r1', ordinal: 1, rowKind: 'Static', label: null, rowVersion, cells: { A: cellA }, isOrphaned: false },
        ],
        cellPermissions: {},
        cellConfirmations: {},
      } as unknown as TableSliceDto;
    }

    async function lose(slice: TableSliceDto): Promise<void> {
      const client = new QueryClient();
      client.setQueryData(queryKeys.slices.one(1, 202609), slice);
      const { unmount } = renderHook(() => useDocumentPending(DocumentId, Owner), {
        wrapper: ({ children }: { children: ReactNode }): JSX.Element => (
          <QueryClientProvider client={client}>{children}</QueryClientProvider>
        ),
      });
      putPendingEdit(1, 202609, {
        rowKey: 'r1',
        columnCode: 'A',
        value: '9',
        isEmpty: false,
        baseVersion: 'AAA=',
        before: '4',
      });
      await apiFetch('/api/v1/documents/7').catch(() => undefined);
      unmount();
    }

    it('рядок піднявся власним збереженням → у слід їде остання відома версія', async () => {
      stubServer(Owner);
      await lose(cachedSlice('BBB=', '4'));

      const edit = peekLostEdits(Owner)?.slices[0]?.edits[0];
      expect(edit?.baseVersion).toBe('BBB=');
    });

    it('чужа зміна тієї самої комірки → версія лишається старою (конфлікт чесний)', async () => {
      stubServer(Owner);
      await lose(cachedSlice('BBB=', '77'));

      const edit = peekLostEdits(Owner)?.slices[0]?.edits[0];
      expect(edit?.baseVersion).toBe('AAA=');
    });
  });
});

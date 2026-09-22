import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest';
import { cleanup, fireEvent, render, renderHook, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import type { JSX, ReactNode } from 'react';
import { apiFetch, setLoginRedirect } from '@/api/client';
import { useDocumentPending } from '@/features/grid/autosave';
import { pendingSlices, putPendingEdit, resetPending } from '@/features/grid/pendingStore';
import { LoginPage } from '@/pages/LoginPage';
import { DocumentPage } from '@/pages/DocumentPage';

/**
 * `ФВ-3.6`: правки, що загинули разом із сесією, ПОВЕРТАЮТЬСЯ, а не просто
 * оплакуються.
 *
 * ⚠ Набір ходить лише публічними шляхами продукту: `useDocumentPending` (як
 * `DocumentPage`), справжній `401` через `apiFetch`, справжній `LoginPage`,
 * справжній `DocumentPage`. Жодного імпорту з нових модулів і жодного прямого
 * запису у сховище браузера — інакше мутація «пиши в `localStorage`» або
 * «застосуй чужий слід» пройшла б повз: тест довів би, що модуль працює, а не
 * що працює продукт.
 *
 * ⛔ Сітка підмінена (`D1-12`, як у наборах сторінки документа): `RevoGrid`
 * тягне власні запити й до відновлення стосунку не має. Зріз, який читає
 * перевірка версій, приходить тим самим `GET`, що й для сітки.
 */
vi.mock('@/features/grid/SheetTables', () => ({
  SheetTables: (): JSX.Element => <div data-testid="grid-stub" />,
}));

vi.mock('@/features/methodologies/CalculationResultsPanel', () => ({
  CalculationResultsPanel: (): JSX.Element => <div data-testid="calc-stub" />,
}));

const Owner = 42;
const Stranger = 43;
const DocumentId = 7;
const TableInstanceId = 1;

/**
 * Період зрізу, у якому правили.
 *
 * ⚠ Навмисно НЕ той, що відкриє сторінка (вона бере поточний з календаря):
 * слід несе адресу зрізу сам, і відновлення не має залежати від того, який
 * період людина бачить у момент повернення.
 */
const PeriodKey = 202401;

/** Версія рядків на момент правки. */
const BaseVersion = 'AAA=';

/** ⛔ Рядок `r3` за час простою змінив хтось інший — його правка не поїде. */
const ChangedVersion = 'CHANGED=';

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

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

/**
 * Сервер: `401` на будь-який API, доки сесію не відкрито входом.
 *
 * @param meUserId чий профіль віддає `/api/v1/me`.
 * @param signedIn почати вже з відкритою сесією (сценарії без сторінки входу).
 */
function stubServer(meUserId: number, signedIn = false): void {
  const language = `restore-${String(++lang)}`;
  localStorage.setItem('uiLanguage', language);
  let open = signedIn;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

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
        open = true;
        return new Response(null, { status: 204 });
      }
      if (!open) return new Response(null, { status: 401 });

      if (url.endsWith('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: [],
          simulatedForUserId: null,
          userId: meUserId,
          userName: 'operator',
        });
      }

      // ⛔ Збереження НЕ ЗАВЕРШУЄТЬСЯ навмисно. Відновлена правка — звичайна
      // незбережена зміна, тож за 500 мс її підхоплює автозбереження і, якщо
      // сервер відповість, вона зі сховища зникне. Перевірка міряє РЕЗУЛЬТАТ
      // відновлення, а не швидкість власного `expect` проти дебаунсу.
      if (init?.method === 'PATCH') return await new Promise<Response>(() => undefined);

      if (url.includes('/tables/status')) return json([]);

      // Зріз таблиці: саме за ним звіряються версії рядків.
      if (/\/tables\/\d+$/u.test(url)) {
        return json({
          columns: [],
          rows: [
            { cells: {}, isOrphaned: false, label: null, ordinal: 0, rowKey: 'r1', rowKind: 'Item', rowVersion: BaseVersion },
            { cells: {}, isOrphaned: false, label: null, ordinal: 1, rowKey: 'r2', rowKind: 'Item', rowVersion: BaseVersion },
            { cells: {}, isOrphaned: false, label: null, ordinal: 2, rowKey: 'r3', rowKind: 'Item', rowVersion: ChangedVersion },
          ],
          tableInstanceId: TableInstanceId,
        });
      }

      if (url.includes('/validation')) {
        return json(
          { title: 'Not found', status: 404, detail: 'not validated', errorCode: 'ECR-DOC-0404' },
          404,
        );
      }

      if (url.includes('/tables')) {
        return json([
          {
            allowsDynamicRows: false,
            maxDynamicRows: null,
            sheetCode: 'GEN',
            sheetDefId: 1,
            sheetNameL10n: { values: { en: 'General' } },
            sheetOrdinal: 0,
            tableCode: 'T1',
            tableDefId: 1,
            tableInstanceId: TableInstanceId,
            tableNameL10n: { values: { en: 'Table 1' } },
            tableOrdinal: 0,
          },
        ]);
      }

      if (url.includes(`/api/v1/documents/${String(DocumentId)}`)) {
        return json({
          businessKey: 'DOC-0007',
          createdAt: '2026-01-01T00:00:00Z',
          id: DocumentId,
          nameL10n: { values: {} },
          projectId: 1,
          sheetCount: 1,
          sheetStates: { GEN: 'Draft' },
        });
      }

      return json(null);
    }),
  );
}

function wrapper({ children }: { children: ReactNode }): JSX.Element {
  return <QueryClientProvider client={new QueryClient()}>{children}</QueryClientProvider>;
}

/**
 * Власник правив три комірки, жодна не встигла піти, сесія впала.
 *
 * ⚠ Усі три введені з однією версією рядка — саме так їх і бачив редактор.
 * Розходиться з ними лише те, що станеться на СЕРВЕРІ, доки людина
 * входитиме заново.
 */
async function loseThreeEdits(): Promise<void> {
  const { unmount } = renderHook(() => useDocumentPending(DocumentId, Owner), { wrapper });

  const edits = [
    { rowKey: 'r1', columnCode: 'A', value: '5' },
    { rowKey: 'r2', columnCode: 'B', value: '6' },
    { rowKey: 'r3', columnCode: 'C', value: '7' },
  ];

  for (const edit of edits) {
    putPendingEdit(TableInstanceId, PeriodKey, { ...edit, isEmpty: false, baseVersion: BaseVersion });
  }

  await apiFetch(`/api/v1/documents/${String(DocumentId)}`).catch(() => undefined);

  // Перезавантаження сторінки: пам'ять зникає.
  unmount();
  resetPending();
}

function show(entry: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <MemoryRouter initialEntries={[entry]}>
        <QueryClientProvider client={client}>
          <Routes>
            <Route path="/login" element={<LoginPage />} />
            <Route path="/" element={<p>home</p>} />
            <Route path="/documents/:id" element={<DocumentPage />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

async function signIn(): Promise<void> {
  show('/login');

  fireEvent.change(await screen.findByLabelText('User name'), { target: { value: 'op' } });
  const password = document.querySelector('input[type="password"]');
  if (password === null) throw new Error('немає поля пароля');
  fireEvent.change(password, { target: { value: 'pw' } });
  fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
}

/** Ключі сховища, що стосуються сліду. */
function traceKeys(store: Storage): string[] {
  return Array.from({ length: store.length }, (_, i) => store.key(i) ?? '').filter((key) =>
    key.includes('lostEdits'),
  );
}

/** Mantine у jsdom іде довго — той самий поріг, що в наборах сторінки документа. */
const SlowEnvTimeout = 400_000;
const SettleTimeout = 30_000;

/** Чекає, доки сторінка документа справді з'явилася (її власна дія). */
async function documentShown(): Promise<void> {
  await screen.findByRole('button', { name: '⟦document.validate⟧' }, { timeout: SlowEnvTimeout });
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

describe('відновлення незбережених правок після обриву сесії (ФВ-3.6)', () => {
  it(
    '401 → вхід → документ: правки повертаються, конфлікт названо окремо',
    async () => {
      stubServer(Owner);
      await loseThreeEdits();

      // ⛔ Слід — у вкладці (`sessionStorage`), не в браузері назавжди: у ньому
      // тепер лежать ВВЕДЕНІ ЧИСЛА, а не лише їх кількість.
      expect(traceKeys(localStorage)).toEqual([]);
      expect(traceKeys(sessionStorage)).toHaveLength(1);

      await signIn();

      // Вхід пропонує ПОВЕРНУТИ, а не повідомляє про втрату.
      const offer = await screen.findByRole('alert');
      expect(offer.textContent).toContain(
        `⟦login.restoreEdits.text (count=3, documentId=${String(DocumentId)})⟧`,
      );

      fireEvent.click(screen.getByRole('button', { name: '⟦login.restoreEdits.continue⟧' }));

      await documentShown();

      const banner = await screen.findByTestId('restore-edits', {}, { timeout: SettleTimeout });
      expect(banner.textContent).toContain('⟦document.restoreEdits.text (count=3)⟧');

      fireEvent.click(screen.getByRole('button', { name: '⟦document.restoreEdits.apply⟧' }));

      await screen.findByTestId('restore-edits-result', {}, { timeout: SettleTimeout });

      /*
       * ⛔ Головне твердження: правка стала звичайною незбереженою зміною
       * документа — тією самою, яку введе автозбереження. Не «банер зник» і не
       * «запит пішов»: сховище незбереженого і є те місце, з якого правка
       * зникла на `401`.
       */
      const restored = pendingSlices();
      expect(restored).toHaveLength(1);
      expect(restored[0]?.tableInstanceId).toBe(TableInstanceId);
      expect(restored[0]?.periodKey).toBe(PeriodKey);
      expect(
        restored[0]?.edits.map((edit) => `${edit.rowKey}.${edit.columnCode}=${String(edit.value)}`).sort(),
      ).toEqual(['r1.A=5', 'r2.B=6']);

      // ⛔ Конфліктну правку НЕ застосовано мовчки: її названо на екрані.
      const conflicts = screen.getAllByTestId('restore-edits-conflict');
      expect(conflicts).toHaveLength(1);
      expect(conflicts[0]?.textContent).toContain('r3');

      // Слід витрачено: повторного показу вже нема чим зробити.
      expect(traceKeys(sessionStorage)).toEqual([]);
    },
    SlowEnvTimeout,
  );

  it(
    '«Відхилити» на вході стирає слід — на документі пропонувати вже нічого',
    async () => {
      stubServer(Owner);
      await loseThreeEdits();
      await signIn();

      await screen.findByRole('alert');
      fireEvent.click(screen.getByRole('button', { name: '⟦login.restoreEdits.discard⟧' }));
      await screen.findByText('home');

      expect(traceKeys(sessionStorage)).toEqual([]);

      // ⚠ Доказ не в сховищі, а на екрані: відкриваємо той самий документ.
      //
      // ⛔ `cleanup()`, а не `document.body.innerHTML = ''`: другий лишає
      // `@testing-library` з контейнерами, яких уже немає в документі, і її
      // власне прибирання після тесту падає `NotFoundError` — кожен тест із
      // цього файлу «провалювався» рівно так, нічого не довівши.
      cleanup();
      stubServer(Owner, true);
      show(`/documents/${String(DocumentId)}`);
      await documentShown();

      expect(screen.queryByTestId('restore-edits')).toBeNull();
      expect(pendingSlices()).toHaveLength(0);
    },
    SlowEnvTimeout,
  );

  it(
    '«Відхилити» на документі стирає слід і нічого не повертає',
    async () => {
      stubServer(Owner);
      await loseThreeEdits();

      cleanup();
      stubServer(Owner, true);
      show(`/documents/${String(DocumentId)}`);
      await documentShown();

      await screen.findByTestId('restore-edits', {}, { timeout: SettleTimeout });
      fireEvent.click(screen.getByRole('button', { name: '⟦document.restoreEdits.discard⟧' }));

      await waitFor(() => {
        expect(screen.queryByTestId('restore-edits')).toBeNull();
      });

      // ⛔ Відхилення — це відмова від правок, а не відкладання: у сховищі
      // незбереженого порожньо, і слід витрачено.
      expect(pendingSlices()).toHaveLength(0);
      expect(traceKeys(sessionStorage)).toEqual([]);
    },
    SlowEnvTimeout,
  );

  it(
    'чужий слід не пропонується і не застосовується',
    async () => {
      stubServer(Owner);
      await loseThreeEdits();

      // Той самий комп'ютер, та сама вкладка — інша людина.
      cleanup();
      stubServer(Stranger, true);
      show(`/documents/${String(DocumentId)}`);
      await documentShown();

      expect(screen.queryByTestId('restore-edits')).toBeNull();
      expect(pendingSlices()).toHaveLength(0);

      // ⚠ І слід власника на місці: чужий сеанс його не витратив.
      expect(traceKeys(sessionStorage)).toHaveLength(1);
    },
    SlowEnvTimeout,
  );

  it(
    'без незбережених правок 401 нічого не лишає',
    async () => {
      stubServer(Owner);
      const { unmount } = renderHook(() => useDocumentPending(DocumentId, Owner), { wrapper });
      await apiFetch(`/api/v1/documents/${String(DocumentId)}`).catch(() => undefined);
      unmount();

      await waitFor(() => {
        expect(traceKeys(sessionStorage)).toEqual([]);
      });
    },
    SettleTimeout,
  );
});

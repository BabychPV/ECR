import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnapshotsPage } from '@/pages/admin/SnapshotsPage';
import { testTheme } from '@/test/render';

/**
 * Параметри звіту `@Name` у діалозі побудови зрізу (`R6`).
 *
 * ⛔ Головне тут — деградація в бік ЗАБОРОНИ (`D15` §0, `L10`) і ТРИ стани,
 * які легко злити у два:
 *   А — обов'язковий параметр без значення: побудова недоступна, причина
 *       названа. Сервер однаково відмовить `422`, але дізнатися про це з
 *       екрана ДО кліку дешевше, ніж із невдалої задачі;
 *   Б — заповнили: побудова доступна, і в ТІЛІ запиту саме ці значення. `Year`
 *       (`Number`) іде РЯДКОМ, як увів користувач (`R6`, `b0045915`,
 *       2026-09-22): сервер приймає значення параметра і числом JSON, і
 *       рядком, і сам перевіряє формат — клієнт `Number()` не робить;
 *   В — оголошення прочитати не вдалося: побудова недоступна, і причина ІНША,
 *       ніж у А. Побудова наосліп або впаде `422`, або — гірше — пройде без
 *       параметра й дасть зріз, який виглядає нормальним;
 *   Г (дзеркало) — параметрів справді немає: на екрані нічого зайвого, поля
 *       `parameters` в тілі немає зовсім, побудова працює як раніше.
 *
 * ⚠ Каталог у тестах не вантажиться, тож `t()` віддає `⟦ключ⟧`. Закриваюча
 * дужка в регекспі обов'язкова: `snapshots.parameters` інакше збігається і з
 * `snapshots.parametersBlocked`.
 */

const Wait = { timeout: 10_000 } as const;
const TestTimeout = 60_000;

const project = { id: 42, code: 'KASH_2026', status: 'Active' as const };

/** Опис звіту з тим `rulesJson`, який перевіряє конкретний випадок. */
function definition(rulesJson: string): unknown {
  return {
    id: 1,
    code: 'IEC',
    isActive: true,
    isRegulatory: true,
    nameL10n: { values: { en: 'Industrial Environmental Control' } },
    versions: [
      {
        id: 2,
        columnsJson: '[]',
        rulesJson,
        status: 'Published',
        createdAt: '2026-01-01T00:00:00Z',
      },
    ],
  };
}

const WithParameters = JSON.stringify({
  rowSource: 'CalculationResults',
  parameters: [
    { code: 'Year', type: 'Number', required: true },
    { code: 'Site', type: 'Text', required: false, default: 'ALL' },
    { code: 'Draft', type: 'Boolean', required: false },
    { code: 'Since', type: 'Date', required: true, default: '2026-03-01' },
  ],
});

const WithoutParameters = JSON.stringify({ rowSource: 'CalculationResults' });

/** Валідний JSON із цього рядка не виходить — саме випадок В. */
const Unreadable = '{"rowSource":"CalculationResults","parameters":[';

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

/** Ставить заглушки й повертає тіло, з яким пішла побудова. */
function mockApi(rulesJson: string): { body: unknown } {
  const sent: { body: unknown } = { body: undefined };

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Report.ViewRegulatory', 'Report.BuildSnapshot'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (url.includes('/build') && method === 'POST') {
        sent.body = JSON.parse(String(init?.body ?? 'null'));

        return json({ jobId: 'job-1' }, 202);
      }

      if (url.includes('/api/v1/jobs/')) {
        return json({ jobId: 'job-1', state: 'Succeeded', percent: 100, message: null, error: null });
      }

      if (url.includes('/api/v1/reports/snapshots')) return json([]);
      if (url.includes('/api/v1/reports')) return json([definition(rulesJson)]);

      if (url.includes('/api/v1/projects')) {
        return json({ items: [project], nextCursor: null, totalCount: 1 });
      }

      return json(null);
    }),
  );

  return sent;
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

/** Відкриває діалог побудови з обраним проєктом і обраним звітом. */
async function openDialogWithReport(): Promise<HTMLElement> {
  fireEvent.click(await screen.findByLabelText(/documents\.project⟧/, {}, Wait));
  fireEvent.click(await screen.findByRole('option', { name: 'KASH_2026' }, Wait));

  fireEvent.click(await screen.findByRole('button', { name: /snapshots\.build⟧/ }, Wait));

  const dialog = await screen.findByRole('dialog', {}, Wait);

  // ⚠ Опції випадного списку рендеряться в порталі ПОЗА `dialog`.
  fireEvent.click(within(dialog).getByLabelText(/snapshots\.code⟧/));
  fireEvent.click(
    await screen.findByRole('option', { name: /Industrial Environmental Control/ }, Wait),
  );

  return dialog;
}

function buildButton(dialog: HTMLElement): HTMLButtonElement {
  return within(dialog).getByRole('button', { name: /snapshots\.build⟧/ }) as HTMLButtonElement;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SnapshotsPage: параметри звіту при побудові зрізу (R6)', () => {
  it(
    'А: обов\'язковий параметр без значення блокує побудову й каже, чому',
    async () => {
      mockApi(WithParameters);
      show();

      const dialog = await openDialogWithReport();

      // ⚠ Спершу дочекатися самих полів: без цього «кнопка заблокована» було б
      // зеленим просто тому, що опис ще не приїхав.
      await within(dialog).findByText(/snapshots\.parameters⟧/, {}, Wait);

      expect(within(dialog).getByText(/snapshots\.parametersBlocked⟧/)).toBeDefined();
      expect(buildButton(dialog).disabled).toBe(true);

      // Обов'язковість названа словом, а не самою зірочкою — на ОБОХ
      // обов'язкових полях (`Year` і `Since`).
      //
      // ⚠ Через `waitFor`: поле дати їде окремим чанком (`import()` заради
      // бюджету маршруту), тож у першому кадрі діалогу його ще немає.
      await waitFor(() => {
        expect(within(dialog).getAllByText(/snapshots\.parameterRequired⟧/).length).toBe(2);
      }, Wait);
    },
    TestTimeout,
  );

  it(
    'Б: заповнили — побудова доступна, і в тілі запиту ті самі значення потрібних типів',
    async () => {
      const sent = mockApi(WithParameters);
      show();

      const dialog = await openDialogWithReport();

      fireEvent.change(await within(dialog).findByLabelText(/^Year/, {}, Wait), {
        target: { value: '2026' },
      });
      fireEvent.click(within(dialog).getByLabelText(/^Draft/));

      await waitFor(() => {
        expect(buildButton(dialog).disabled).toBe(false);
      }, Wait);

      expect(within(dialog).queryByText(/snapshots\.parametersBlocked⟧/)).toBeNull();

      fireEvent.click(buildButton(dialog));

      await waitFor(() => {
        expect(sent.body).toBeDefined();
      }, Wait);

      /*
       * ⛔ Перевіряється ТІЛО, а не факт виклику: побудова з порожнім
       * `parameters` виглядала б так само успішно, а сервер відмовив би `422`
       * — або, гірше, побудував зріз на замовчуваннях.
       *
       * ⚠ `Since` — рядок `2026-03-01`, а не мить у UTC: `toISOString()` зсунув
       * би дату на добу для всіх, хто західніше за Гринвіч.
       *
       * ⚠ `Year` — теж рядок (`R6`, `b0045915`, 2026-09-22): поле параметра
       * `Number` тепер `TextInput`, і значення йде як увів користувач, без
       * `Number()`.
       */
      expect(sent.body).toEqual({
        projectId: 42,
        periodKey: expect.any(Number) as number,
        parameters: { Year: '2026', Site: 'ALL', Draft: true, Since: '2026-03-01' },
      });

      const parameters = (sent.body as { parameters: Record<string, unknown> }).parameters;

      expect(typeof parameters['Year']).toBe('string');
      expect(typeof parameters['Draft']).toBe('boolean');
      expect(typeof parameters['Since']).toBe('string');
    },
    TestTimeout,
  );

  it(
    'Б2: 16 знаків дробу в полі Number ідуть у тіло рядком, як є, без округлення чи Number()',
    async () => {
      /*
       * ⛔ Це доказ саме проти поля, а не проти `parameters.ts`: `coerce` там
       * і раніше просто повертав значення без змін — небезпека була в самому
       * `NumberInput`, який ганяє введене через IEEE-754 ще ДО того, як
       * значення взагалі доходить до `parameters.ts`. `NumberInput` на цей
       * рядок або показав би `Infinity`/обрізане число в самому полі, або
       * округлив би 16-й знак дробу — обидва варіанти провалили б перевірку
       * нижче.
       */
      const sent = mockApi(WithParameters);
      show();

      const dialog = await openDialogWithReport();

      const precise = '1234.1234567890123456';

      fireEvent.change(await within(dialog).findByLabelText(/^Year/, {}, Wait), {
        target: { value: precise },
      });
      fireEvent.click(within(dialog).getByLabelText(/^Draft/));

      await waitFor(() => {
        expect(buildButton(dialog).disabled).toBe(false);
      }, Wait);

      fireEvent.click(buildButton(dialog));

      await waitFor(() => {
        expect(sent.body).toBeDefined();
      }, Wait);

      const parameters = (sent.body as { parameters: Record<string, unknown> }).parameters;

      expect(parameters['Year']).toBe(precise);
      expect(typeof parameters['Year']).toBe('string');
    },
    TestTimeout,
  );

  it(
    'В: оголошення прочитати не вдалося — побудова заблокована з ІНШОЇ причини',
    async () => {
      mockApi(Unreadable);
      show();

      const dialog = await openDialogWithReport();

      await within(dialog).findByText(/snapshots\.parametersUnknown⟧/, {}, Wait);

      expect(buildButton(dialog).disabled).toBe(true);

      // ⛔ Причина саме ця, а не «заповніть обов'язкові»: порада заповнити поле,
      // якого немає на екрані, відправила б людину шукати неіснуюче.
      expect(within(dialog).queryByText(/snapshots\.parametersBlocked⟧/)).toBeNull();

      // ⛔ І порожньої секції параметрів теж немає: вона читалась би як
      // «параметрів немає» — тобто як протилежне твердження.
      expect(within(dialog).queryByText(/snapshots\.parameters⟧/)).toBeNull();
    },
    TestTimeout,
  );

  it(
    'Г (дзеркало): параметрів немає — нічого зайвого, і поля parameters в тілі немає',
    async () => {
      const sent = mockApi(WithoutParameters);
      show();

      const dialog = await openDialogWithReport();

      // ⚠ Дзеркальні твердження — ПІСЛЯ приходу даних: дочекатися стану, у
      // якому звіт уже обрано (кнопка ожила), і лише тоді казати «немає».
      await waitFor(() => {
        expect(buildButton(dialog).disabled).toBe(false);
      }, Wait);

      expect(within(dialog).queryByText(/snapshots\.parameters⟧/)).toBeNull();
      expect(within(dialog).queryByText(/snapshots\.parametersBlocked⟧/)).toBeNull();
      expect(within(dialog).queryByText(/snapshots\.parametersUnknown⟧/)).toBeNull();

      fireEvent.click(buildButton(dialog));

      await waitFor(() => {
        expect(sent.body).toBeDefined();
      }, Wait);

      expect(sent.body !== null && typeof sent.body === 'object' && 'parameters' in sent.body).toBe(
        false,
      );
    },
    TestTimeout,
  );
});

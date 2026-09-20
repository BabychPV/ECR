import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PeriodsPage } from '@/pages/admin/PeriodsPage';
import { testTheme } from '@/test/render';

/**
 * Строк перевідкриття періоду: обіцянка, якої сервер не виконував.
 *
 * ⛔ Кнопка «Відкрити період» слала жорсткий `until: null` із коментарем
 * «Безстроково». Сервер так не робить НІКОЛИ: за порожнього `until`
 * `ReopenPeriodHandler` бере `EndOfSiteDay` (`D-68`), тобто період закривається
 * сам опівночі в поясі майданчика. Людина натискала «відкрити», думала, що
 * відкрила назавжди, і дізнавалася про межу наступного ранку.
 *
 * ⚠ Перевіряється ТІЛО запиту, а не факт виклику: дефект був саме в тілі, і
 * тест на «запит пішов» лишався б зеленим на зламаному коді.
 *
 * ⚠ Каталог у прогоні не вантажиться, тож `t()` віддає `⟦ключ⟧`. У регекспах
 * стоїть закриваюча дужка — без неї `⟦periods.reopenUntil` збігається і з
 * `⟦periods.reopenUntilHint⟧`.
 */

/**
 * Пояс майданчика — `Asia/Aqtau` (+05:00, без переходу на літній час).
 *
 * ⛔ Не `Europe/Kyiv` і не пояс машини, що жене тест: очікуваний момент нижче
 * порахований РУКАМИ з цього зсуву, і зона з переходом зробила б це число
 * залежним від пори року.
 */
const SiteZone = 'Asia/Aqtau';

/** Дата, яку набирає користувач: майбутня, тож саму дію нічого не блокує. */
const FutureDay = '2030-06-15';

/**
 * Що має поїхати в тілі за `FutureDay`.
 *
 * ⚠ Число порахувало не наше ж перетворення, а рука: кінець доби майданчика для
 * 15 червня — це північ 16 червня за `+05:00`, тобто `2030-06-15T19:00:00Z`.
 * Очікування, зібране тим самим кодом, який воно перевіряє, не доводить нічого.
 */
const FutureDeadline = '2030-06-15T19:00:00.000Z';

/** Дата, що вже минула: 2020 рік не настане вдруге за жодного зсуву. */
const PastDay = '2020-01-01';

const project = {
  id: 7,
  code: 'PRJ-7',
  status: 'Active' as const,
  periodKind: 'Monthly' as const,
  periodCount: 1,
  currentPeriodId: null,
  timeZoneId: SiteZone,
};

const calendar = {
  projectId: 7,
  periodKind: 'Monthly' as const,
  currentPeriodMode: 'Auto' as const,
  timeZoneId: SiteZone,
  policy: {
    id: 1,
    code: 'ECR-Standard',
    openOffsetDays: 0,
    graceOffsetDays: 15,
    hardCloseOffsetDays: 45,
    yearGraceOffsetDays: 45,
  },
  periods: [
    {
      id: 42,
      periodKey: 202601,
      sequence: 1,
      startsAt: '2026-01-01',
      endsAt: '2026-02-01',
      state: 'Closed',
      graceEndsAt: null,

      // ⚠ `null` навмисно: інакше рядок таблиці малює бейдж тим самим ключем
      // (`periods.reopenedUntil`), яким діалог називає причину відмови, і
      // твердження про видиму причину стало б неоднозначним.
      reopenedUntil: null,
      isCurrent: false,
    },
  ],
};

/** Тіла запитів на перевідкриття — саме те, заради чого цей файл існує. */
function respond(): { bodies: Record<string, unknown>[] } {
  const state = { bodies: [] as Record<string, unknown>[] };

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return new Response(
          JSON.stringify({
            denies: [],
            grants: {},
            isSimulation: false,
            language: 'en',
            mustChangePassword: false,
            permissions: ['Period.Reopen'],
            simulatedForUserId: null,
            userId: 1,
            userName: 'tester',
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      if (url.includes('/reopen')) {
        state.bodies.push(JSON.parse(String(init?.body ?? '{}')) as Record<string, unknown>);

        return new Response(JSON.stringify(null), { status: 200 });
      }

      if (url.includes('/api/v1/projects') && url.includes('/periods')) {
        return new Response(JSON.stringify(calendar), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.includes('/api/v1/projects')) {
        return new Response(JSON.stringify({ items: [project], nextCursor: null, totalCount: 1 }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      return new Response(JSON.stringify(null), { status: 200 });
    }),
  );

  return state;
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/admin/periods?projectId=7']}>
        <QueryClientProvider client={client}>
          <PeriodsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

const Ceiling = 10_000;
const TestTimeout = 30_000;

type Session = {
  user: ReturnType<typeof userEvent.setup>;
  dialog: HTMLElement;
  until: HTMLElement;
  confirm: HTMLButtonElement;
};

/**
 * Відкриває діалог перевідкриття і заповнює обов'язкову причину.
 *
 * ⚠ Поле строку чекається через `find*`: воно їде окремим чанком
 * (`lazy(() => import('@mantine/dates'))`), тобто в першому кадрі діалогу його
 * ще немає.
 */
async function openReopen(): Promise<Session> {
  const user = userEvent.setup();
  show();

  const open = await screen.findByRole(
    'button',
    { name: /⟦periods\.reopen⟧/ },
    { timeout: Ceiling },
  );
  await user.click(open);

  const dialog = await screen.findByRole('dialog', {}, { timeout: Ceiling });

  await user.type(
    within(dialog).getByLabelText(/⟦workflow\.reason⟧/),
    'Уточнення даних за січень',
  );

  const until = await within(dialog).findByLabelText(
    /⟦periods\.reopenUntil⟧/,
    {},
    { timeout: Ceiling },
  );

  const buttons = within(dialog).getAllByRole('button', { name: /⟦periods\.reopen⟧/ });
  const confirm = buttons[buttons.length - 1] as HTMLButtonElement;

  return { user, dialog, until, confirm };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('PeriodsPage: строк перевідкриття періоду', () => {
  it(
    'А: порожнє поле — у тілі `until: null`, і межу названо на екрані',
    async () => {
      const state = respond();
      const { user, dialog, confirm } = await openReopen();

      // ⛔ Підпис під полем каже те, що СПРАВДІ зробить сервер за порожнього
      // `until`. Слова «безстроково» на екрані немає і бути не може.
      expect(within(dialog).getByText(/⟦periods\.reopenUntilHint⟧/)).toBeDefined();

      await user.click(confirm);

      await waitFor(() => expect(state.bodies).toHaveLength(1), { timeout: Ceiling });

      // ⛔ Саме ТІЛО. `null` тут — значення, а не замовчування, про яке мовчать.
      expect(state.bodies[0]).toHaveProperty('until', null);
      expect(state.bodies[0]).toHaveProperty('reason', 'Уточнення даних за січень');
    },
    TestTimeout,
  );

  it(
    'Б: обрана дата — у тілі кінець ТІЄЇ доби в поясі майданчика',
    async () => {
      const state = respond();
      const { user, until, confirm } = await openReopen();

      await user.type(until, FutureDay);
      await user.tab();

      await waitFor(() => expect(confirm.disabled).toBe(false), { timeout: Ceiling });
      await user.click(confirm);

      await waitFor(() => expect(state.bodies).toHaveLength(1), { timeout: Ceiling });

      /*
       * ⛔ Головне твердження файлу. Жорсткий `until: null` у коді робить його
       * червоним; `getDate()` замість `getDate() + 1` у перетворенні — теж
       * (вийшло б `2030-06-14T19:00:00.000Z`, тобто вікно коротше на добу);
       * північ у поясі ГЛЯДАЧА замість поясу майданчика — теж, бо прогін не
       * зобов'язаний іти на `+05:00`.
       */
      expect(state.bodies[0]).toHaveProperty('until', FutureDeadline);
    },
    TestTimeout,
  );

  it(
    'В: дата в минулому — зберегти не можна, і причину видно',
    async () => {
      const state = respond();
      const { user, dialog, until, confirm } = await openReopen();

      await user.type(until, PastDay);
      await user.tab();

      // ⛔ Кнопка вимкнена. Сервер такий строк не відхиляє — він прийме його й
      // закриє період наступним прогоном `PeriodStateJob`, тобто мовчки.
      await waitFor(() => expect(confirm.disabled).toBe(true), { timeout: Ceiling });

      /*
       * ⛔ Причина ВИДИМА, і це власний рядок каталогу, а не підпис стану:
       * `periods.reopenedUntil` («open until …») читався б як «період уже
       * відкрито до», хоча його ще не відкривали — опис наслідку замість
       * причини відмови. Прибрати `error={…}` — і цей рядок падає.
       */
      expect(within(dialog).getByText('⟦periods.reopenUntilPast⟧')).toBeDefined();

      await user.click(confirm);

      expect(state.bodies).toHaveLength(0);
    },
    TestTimeout,
  );

  it(
    'Г (дзеркало): коректна дата — зберегти МОЖНА',
    async () => {
      respond();
      const { user, dialog, until, confirm } = await openReopen();

      /*
       * ⚠ Без цього випадку «полагодити» В можна було б назавжди вимкненою
       * кнопкою: заборона, яка не вмикається назад, не є запобіжником.
       */
      await user.type(until, FutureDay);
      await user.tab();

      await waitFor(() => expect(confirm.disabled).toBe(false), { timeout: Ceiling });

      expect(within(dialog).queryByText('⟦periods.reopenUntilPast⟧')).toBeNull();
    },
    TestTimeout,
  );
});

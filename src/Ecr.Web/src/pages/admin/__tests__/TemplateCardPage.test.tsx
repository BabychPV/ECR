import { readFileSync } from 'node:fs';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { Notifications, notifications } from '@mantine/notifications';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { JSX } from 'react';
import { TemplateCardPage } from '@/pages/admin/TemplateCardPage';
import { useSession } from '@/shared/session/useSession';
import { testTheme } from '@/test/render';

/**
 * Картка шаблону (`UI-09`, директива №15 §4).
 *
 * ⛔ Тест написаний ДО сторінки й на порожньому дереві був червоний рівно
 * так: `Failed to resolve import "@/pages/admin/TemplateCardPage"` — файлу не
 * існувало, і жодне твердження нижче не виконувалося.
 *
 * ⛔ Що саме тут доводиться, а не ілюструється:
 *
 *  1. **Кнопка архівування не існує, доки не приїхало число залежних.** Дія,
 *     показана раніше за число, заради якого її натискають, — це рішення
 *     наосліп (`templateApi.ts`: «екран архівування не має показувати кнопку
 *     раніше, ніж число»). І ОБИДВА боки: коли число приїхало — кнопка Є,
 *     інакше «полагодити» можна було б назавжди схованою кнопкою.
 *  2. **Відмова ≠ порожньо** (`L10`): `404 ECR-TMPL-0404` дає `ErrorAlert` із
 *     кодом і «повторити», а не порожню картку з кнопками.
 *  3. **Код незмінний**: він на екрані як `CodeText`, і поля введення для
 *     нього немає ніде.
 *  4. **Архівування — через підтвердження** (`L6`): сам клік не шле нічого,
 *     у заголовку діалогу стоїть НАЗВА шаблону, фокус — на «Скасувати».
 *  5. **Undo не мовчазний**: після архівування «назад» справді кличе
 *     `POST …/restore` — сервер уміє цю дію, тому §6 її тут і дозволяє.
 *  6. **`L1`**: рівно одна `filled`-кнопка на екрані.
 *
 * ⚠ Підписи звіряються з КЛЮЧАМИ каталогу (`⟦templates.archive⟧`): каталог у
 * цьому наборі не завантажується, і `t()` повертає позначений ключ — той
 * самий прийом, що й у `PeriodsPage.archiveConfirm.test.tsx`.
 */
const Card = {
  code: 'TPL-EMIS',
  createdAt: '2026-01-01T00:00:00Z',
  dependents: { documents: 5, projects: 2, publishedVersions: 1, versions: 3 },
  id: 7,
  isActive: true,
  nameL10n: { values: { en: 'Stationary sources' } },
};

/**
 * ⛔ Сума залежних — 7, а `projects` — 2 і `documents` — 5: ЖОДНЕ зі
 * складових не дорівнює сумі. Інакше мутація `projects + documents` →
 * `projects` лишила б тест зеленим — тобто доказом він не був би (урок дня:
 * мутація, що не змінює шлях перевірюваного значення, нічого не доводить).
 */
const DependentWorkTotal = '7';

interface Stub {
  readonly calls: string[];
  archiveCalls: number;
  restoreCalls: number;
  renameBodies: string[];
  /** Наступна відповідь на `GET /templates/{id}`: картка або відмова. */
  cardFails: boolean;
  /** Скільки запитів картки затримано (не розв'язано) — для стану «ще їде». */
  holdCard: boolean;
  archiveConflict: boolean;
}

function problem(status: number, errorCode: string, messageKey: string, detail: string): Response {
  return new Response(
    JSON.stringify({
      title: errorCode === 'ECR-TMPL-0404' ? 'Template not found' : 'Template is frozen',
      status,
      errorCode,
      correlationId: 'cid-test-0001',
      detail,
      messageKey,
    }),
    { status, headers: { 'Content-Type': 'application/problem+json' } },
  );
}

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function respond(options?: Partial<Pick<Stub, 'cardFails' | 'holdCard' | 'archiveConflict'>>): Stub {
  const state: Stub = {
    calls: [],
    archiveCalls: 0,
    restoreCalls: 0,
    renameBodies: [],
    cardFails: options?.cardFails ?? false,
    holdCard: options?.holdCard ?? false,
    archiveConflict: options?.archiveConflict ?? false,
  };

  let archived = false;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      state.calls.push(`${method} ${url}`);

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Template.Edit'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (url.endsWith('/archive')) {
        state.archiveCalls += 1;

        // ⚠ Код відмови — із каталогу `ErrorCodes.cs`: саме `ECR-TMPL-0409`
        // сервер повертає на повторне архівування.
        if (state.archiveConflict) {
          return problem(
            409,
            'ECR-TMPL-0409',
            'err.ECR-TMPL-0409.templateAlreadyArchived',
            'Template "TPL-EMIS" is already archived.',
          );
        }

        archived = true;

        return json({ ...Card, isActive: false });
      }

      if (url.endsWith('/restore')) {
        state.restoreCalls += 1;
        archived = false;

        return json({ ...Card, isActive: true });
      }

      if (/\/api\/v1\/templates\/\d+$/.test(url)) {
        if (method === 'PUT') {
          state.renameBodies.push(String(init?.body ?? ''));

          return json({ ...Card, nameL10n: { values: { en: 'Renamed' } } });
        }

        if (state.cardFails) {
          return problem(
            404,
            'ECR-TMPL-0404',
            'err.ECR-TMPL-0404.template',
            'Template 7 was not found.',
          );
        }

        // ⚠ Відповідь, що НЕ приходить ніколи: стан «число залежних ще їде».
        if (state.holdCard) return new Promise<Response>(() => {});

        return json({ ...Card, isActive: !archived });
      }

      if (url.includes('/api/v1/languages')) {
        return json([{ code: 'en', nameNative: 'English', isDefault: true }]);
      }

      return json(null);
    }),
  );

  return state;
}

/**
 * Зонд сесії — видимий доказ того, що `/me` уже в кеші.
 *
 * ⛔ Не зручність. Перевірка «кнопки архівування ще немає» БЕЗ нього зелена з
 * неправильної причини: доки `/me` не доїхав, `can(...)` віддає `false`, і
 * кнопки немає через брак ПРАВА, а не через брак числа залежних. Мутація
 * `showActions = data !== undefined && editable` → `= editable` це й показала:
 * та перевірка лишилася зеленою, тобто доказом не була.
 */
function SessionProbe(): JSX.Element {
  const session = useSession();

  return <span data-testid="session">{session.data === undefined ? 'pending' : 'ready'}</span>;
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      {/* ⚠ `Notifications` — не декорація набору: тост «назад» і є єдиним
          місцем, де живе дія скасування, і без цього вузла перевірка
          «Undo справді кличе restore» перевіряла б порожнечу. */}
      <Notifications />
      <MemoryRouter initialEntries={['/admin/templates/7']}>
        <QueryClientProvider client={client}>
          <SessionProbe />
          <Routes>
            <Route path="/admin/templates/:id" element={<TemplateCardPage />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

beforeEach(() => {
  notifications.cleanQueue();
  notifications.clean();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

// ⚠ Те саме, що в сусідніх наборах цієї теки: середовище прогону повільне.
const SlowEnvTimeout = 400_000;

describe('TemplateCardPage: картка шаблону (UI-09)', () => {
  it(
    'показує назву, код як CodeText і СУМУ залежних робіт',
    async () => {
      respond();
      show();

      expect(
        await screen.findByRole('heading', { name: 'Stationary sources' }, { timeout: SlowEnvTimeout }),
      ).toBeDefined();

      // ⛔ Код — бізнес-ключ: він видимий і НЕ редагується. Поля введення з
      // кодом на екрані немає взагалі.
      const code = document.querySelector('[data-code-text]');
      expect(code?.textContent).toBe('TPL-EMIS');
      expect(
        screen.queryAllByRole('textbox').some((field) => (field as HTMLInputElement).value === 'TPL-EMIS'),
      ).toBe(false);

      // ⛔ Саме СУМА (2 + 5), а не будь-яка зі складових.
      expect(screen.getByText(DependentWorkTotal)).toBeDefined();
    },
    SlowEnvTimeout,
  );

  it(
    'доки число залежних не приїхало — кнопки архівування немає; коли приїхало — Є',
    async () => {
      respond({ holdCard: true });
      show();

      // Сторінка вже змонтована, і ПРАВО вже прочитане — кнопки немає рівно
      // тому, що не приїхало число залежних, а не тому, що бракує права.
      await screen.findByRole('status', {}, { timeout: SlowEnvTimeout });
      await waitFor(
        () => {
          expect(screen.getByTestId('session').textContent).toBe('ready');
        },
        { timeout: SlowEnvTimeout },
      );

      expect(screen.queryByRole('button', { name: /templates\.archive/i })).toBeNull();
      expect(screen.queryByRole('button', { name: /templates\.rename/i })).toBeNull();

      vi.unstubAllGlobals();
      respond();
      show();

      // ⛔ Дзеркало: інакше «полагодити» можна було б назавжди схованою кнопкою.
      expect(
        await screen.findByRole(
          'button',
          { name: /templates\.archive/i },
          { timeout: SlowEnvTimeout },
        ),
      ).toBeDefined();
    },
    SlowEnvTimeout,
  );

  it(
    'L10: відмова картки — ErrorAlert із кодом і «повторити», а не порожня картка',
    async () => {
      respond({ cardFails: true });
      show();

      const alert = await screen.findByRole('alert', {}, { timeout: SlowEnvTimeout });

      expect(within(alert).getByText('ECR-TMPL-0404')).toBeDefined();
      expect(within(alert).getByRole('button', { name: /common\.retry/i })).toBeDefined();

      // ⛔ Порожня картка була б гіршою за відмову: ані коду, ані дій.
      expect(document.querySelector('[data-code-text]')).toBeNull();
      expect(screen.queryByRole('button', { name: /templates\.archive/i })).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'L6: клік «архівувати» САМ не шле запит; у заголовку діалогу — назва шаблону; фокус на Cancel',
    async () => {
      const state = respond();
      const user = userEvent.setup();
      show();

      const button = await screen.findByRole(
        'button',
        { name: /templates\.archive/i },
        { timeout: SlowEnvTimeout },
      );
      await user.click(button);

      expect(state.archiveCalls).toBe(0);

      const dialog = await screen.findByRole('dialog', {}, { timeout: SlowEnvTimeout });

      // ⛔ Назва об'єкта в заголовку, а не «ви впевнені?».
      expect(within(dialog).getByText(/Stationary sources/)).toBeDefined();

      // ⛔ Фокус — на безпечній дії (`ConfirmModal`, правило L6).
      await waitFor(
        () => {
          expect(document.activeElement).toBe(within(dialog).getByTestId('confirm-cancel'));
        },
        { timeout: SlowEnvTimeout },
      );
    },
    SlowEnvTimeout,
  );

  it(
    'підтвердження архівує один раз, а «назад» кличе restore',
    async () => {
      const state = respond();
      const user = userEvent.setup();
      show();

      await user.click(
        await screen.findByRole(
          'button',
          { name: /templates\.archive/i },
          { timeout: SlowEnvTimeout },
        ),
      );

      const dialog = await screen.findByRole('dialog', {}, { timeout: SlowEnvTimeout });
      await user.click(within(dialog).getByTestId('confirm-verb'));

      await waitFor(() => expect(state.archiveCalls).toBe(1), { timeout: SlowEnvTimeout });

      // ⛔ Undo не мовчазний: кнопка справді кличе серверну дію «назад».
      const undo = await screen.findByRole(
        'button',
        { name: /templates\.undo/i },
        { timeout: SlowEnvTimeout },
      );
      await user.click(undo);

      await waitFor(() => expect(state.restoreCalls).toBe(1), { timeout: SlowEnvTimeout });
    },
    SlowEnvTimeout,
  );

  it(
    'ECR-TMPL-0409 на архівуванні не робить вигляду, що шаблон архівовано',
    async () => {
      const state = respond({ archiveConflict: true });
      const user = userEvent.setup();
      show();

      await user.click(
        await screen.findByRole(
          'button',
          { name: /templates\.archive/i },
          { timeout: SlowEnvTimeout },
        ),
      );

      const dialog = await screen.findByRole('dialog', {}, { timeout: SlowEnvTimeout });
      await user.click(within(dialog).getByTestId('confirm-verb'));

      await waitFor(() => expect(state.archiveCalls).toBe(1), { timeout: SlowEnvTimeout });

      // ⛔ Відмова — не «назад»: пропонувати скасування дії, якої не сталося,
      // означало б обіцяти запит, що ніколи не мав сенсу.
      expect(state.restoreCalls).toBe(0);
      expect(
        await screen.findByRole(
          'button',
          { name: /templates\.archive/i },
          { timeout: SlowEnvTimeout },
        ),
      ).toBeDefined();
      expect(screen.queryByRole('button', { name: /templates\.restore/i })).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'перейменування надсилає nameL10n і живе в адресі (?dialog=rename)',
    async () => {
      const state = respond();
      const user = userEvent.setup();
      show();

      const renameButton = await screen.findByRole(
        'button',
        { name: /templates\.rename/i },
        { timeout: SlowEnvTimeout },
      );
      await user.click(renameButton);

      const dialog = await screen.findByRole('dialog', {}, { timeout: SlowEnvTimeout });
      const field = await within(dialog).findByRole('textbox', {}, { timeout: SlowEnvTimeout });

      await user.clear(field);
      await user.type(field, 'Renamed');
      await user.click(within(dialog).getByRole('button', { name: /common\.save/i }));

      await waitFor(() => expect(state.renameBodies).toHaveLength(1), { timeout: SlowEnvTimeout });
      expect(JSON.parse(state.renameBodies[0] ?? '{}')).toEqual({ nameL10n: { en: 'Renamed' } });
    },
    SlowEnvTimeout,
  );

  it('маршрут `/admin/templates/:id` справді МОНТУЄ цю сторінку', () => {
    /*
     * ⛔ Сторінка, не підвішена в роутері, проходить усі тести цього файлу:
     * вони монтують компонент напряму. Сторож дрейфу (`app/__tests__/routes
     * .test.ts`) теж мовчить — він відкидає шляхи з `:`. Тобто без цієї
     * перевірки картка могла б лишитися недосяжною з продукту, і жоден гейт
     * цього не побачив би — рівно той стан, у якому до цього PR жив сам
     * `templateApi.ts`.
     *
     * ⚠ Джерело читається ТЕКСТОМ, а не імпортується — з тієї самої причини,
     * що названа в `routes.test.ts`: `createBrowserRouter` виконується на
     * рівні модуля й тягне `AppLayout` з усім його деревом, тож перевірка
     * червоніла б від будь-якої несумісності в чужому компоненті.
     */
    const router = readFileSync(path.resolve(process.cwd(), 'src/app/router.tsx'), 'utf8');

    expect(router).toContain("path: 'templates/:id'");
    expect(router).toMatch(/index:\s*true,\s*\n\s*element:\s*guarded\([^)]*<TemplateCardPage \/>\)/);
  });

  it(
    'L1: на екрані рівно одна головна (filled) кнопка',
    async () => {
      respond();
      show();

      await screen.findByRole('heading', { name: 'Stationary sources' }, { timeout: SlowEnvTimeout });

      await waitFor(
        () => {
          expect(document.querySelectorAll('button[data-variant="filled"]')).toHaveLength(1);
        },
        { timeout: SlowEnvTimeout },
      );
    },
    SlowEnvTimeout,
  );
});

import type { JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { Notifications, notifications } from '@mantine/notifications';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { SaveRegistryDefinitionDto } from '@/api/types';
import { RegistryDraftPanel } from '@/features/registries/RegistryDraftPanel';
import { RegistryConstructorPage } from '@/pages/admin/RegistryConstructorPage';
import { useSession } from '@/shared/session/useSession';
import { testTheme } from '@/test/render';

/**
 * Конструктор довідника на схемі «чернетка → Опублікувати» (`BE-24` крок 2).
 *
 * ⛔ Що саме тут доводиться, а не ілюструється:
 *
 *  1. **Публікація йде лише через підтвердження** (`L6`): сам клік не шле
 *     НІЧОГО. Мутація «`onClick` кличе `publish.mutate()` напряму» робить цей
 *     набір червоним.
 *  2. **Версія чернетки в рядку запиту закодована.** Перевіряється СИРА
 *     адреса `DELETE`, а не факт виклику: `rowVersion` — base64, і `+` у
 *     рядку запиту означає ПРОБІЛ. Мутація «прибрати `encodeURIComponent`»
 *     червоніє тут і більш ніде: сервер у цьому наборі замокнений і відповів
 *     би `204` на будь-яку адресу.
 *  3. **`definitionDraftChanged` пояснений ПРИЧИНОЮ**, а не загальною
 *     плашкою. Мутація «показати `ErrorAlert`» прибирає вузол
 *     `[data-registry-draft-conflict]` — червоне.
 *  4. **Право на публікацію перевіряється окремо від права правити.**
 *     Мутація `mayPublish = mayEdit` дає кнопку «Опублікувати» редакторові
 *     без `Registry.Publish` — червоне.
 *  5. **Дзеркало: чернетки немає.** Форма показує ОПУБЛІКОВАНУ версію, кнопок
 *     публікації й скасування немає. Без цього рядка «полагодити» перші
 *     чотири можна було б назавжди схованими кнопками.
 *  6. **Форма показує ЧЕРНЕТКУ, а не опубліковану версію**, і каже, коли і
 *     хто її змінив.
 *
 * ⚠ Каталог рядків у наборі не завантажений, тому підписи приходять ключами
 * в `⟦…⟧` — той самий прийом, що й у `TemplateCardPage.test.tsx`.
 */
const Code = 'FUEL';

/**
 * ⛔ Плюс і скісна в base64 — НЕ випадковість. Рівно ці два символи рядок
 * запиту тлумачить інакше за шлях (`+` → пробіл, `/` — розділювач сегментів),
 * і версія без кодування доїхала б до сервера зміненою.
 */
const RowVersion = 'AQID+f/9Ng==';

const EncodedRowVersion = 'AQID%2Bf%2F9Ng%3D%3D';

const PublishedRuleCode = 'PublishedRule';
const DraftRuleCode = 'DraftRule';
const DraftFieldCode = 'DraftField';
const DraftReason = 'ліміт перенесено з Configuration!J3';

const Request: SaveRegistryDefinitionDto = {
  fields: [],
  rules: [
    {
      id: null,
      code: DraftRuleCode,
      ruleKind: 'Expression',
      expression: '[Value] > 0',
      severity: 'Error',
      messageL10n: { values: { en: 'Must be positive' } },
      parametersJson: null,
      isActive: true,
    },
  ],
  reason: DraftReason,
};

interface Stub {
  readonly calls: string[];
  readonly draftBodies: string[];
  saveConflict: 'none' | 'changed' | 'stale';
  hasDraft: boolean;
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

/**
 * Відмова у формі, яку справді віддає сервер: код із `ErrorCodes.cs`,
 * `messageKey` — той, що стоїть у `RegistryDefinitionDraftHandlers.cs`.
 */
function problem(status: number, errorCode: string, extensions: Record<string, unknown>): Response {
  return new Response(
    JSON.stringify({
      title: 'Conflict',
      status,
      errorCode,
      correlationId: 'cid-test-0001',
      detail: 'server sentence',
      ...extensions,
    }),
    { status, headers: { 'Content-Type': 'application/problem+json' } },
  );
}

function draftBody(): unknown {
  return {
    baseDefinitionVersion: 3,
    fields: [
      {
        id: null,
        code: DraftFieldCode,
        nameL10n: { values: { en: 'Draft field' } },
        dataType: 'String',
        ordinal: 2,
        isRequired: false,
        isKey: false,
        lookupRegistryDefId: null,
        unitId: null,
      },
    ],
    rules: [
      {
        id: null,
        code: DraftRuleCode,
        ruleKind: 'Expression',
        expression: '[Value] > 0',
        severity: 'Error',
        messageL10n: { values: { en: 'Must be positive' } },
        parametersJson: null,
        isActive: true,
      },
    ],
    reason: DraftReason,
    updatedAt: '2026-09-21T08:30:00Z',
    updatedByUserId: 9,
    rowVersion: RowVersion,
  };
}

function respond(options?: Partial<Pick<Stub, 'saveConflict' | 'hasDraft'>>, permissions?: string[]): Stub {
  const state: Stub = {
    calls: [],
    draftBodies: [],
    saveConflict: options?.saveConflict ?? 'none',
    hasDraft: options?.hasDraft ?? true,
  };

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      state.calls.push(`${method} ${url}`);

      if (/\/api\/v1\/me(\?|$)/.test(url)) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: permissions ?? ['Registry.View', 'Registry.EditDefinition', 'Registry.Publish'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      // ⛔ Перевіряється ПЕРЕД `/definition`: чернетка живе під тим самим
      // префіксом, і зворотний порядок віддав би їй опублікований опис.
      if (url.includes('/definition/draft')) {
        if (method === 'PUT') {
          state.draftBodies.push(String(init?.body ?? ''));

          if (state.saveConflict === 'changed') {
            return problem(409, 'ECR-REG-0409', {
              messageKey: 'err.ECR-REG-0409.definitionDraftChanged',
              registryCode: Code,
              rowVersion: 'OTHER+VERSION==',
            });
          }

          if (state.saveConflict === 'stale') {
            return problem(409, 'ECR-REG-0409', {
              messageKey: 'err.ECR-REG-0409.definitionDraftStale',
              registryCode: Code,
              baseVersion: '3',
              currentVersion: '4',
            });
          }

          state.hasDraft = true;

          return json(draftBody());
        }

        if (method === 'DELETE') {
          state.hasDraft = false;

          return new Response(null, { status: 204 });
        }

        return json({ definitionVersion: 3, draft: state.hasDraft ? draftBody() : null });
      }

      if (url.includes('/definition/publish')) {
        state.hasDraft = false;

        return json({ definitionVersion: 4 });
      }

      if (url.includes('/definition')) {
        // Прямий `PUT …/definition` — «зберегти й одразу опублікувати».
        if (method === 'PUT') return json({ definitionVersion: 4 });

        return json({
          id: 4,
          code: Code,
          nameL10n: { values: { en: 'Fuel types' } },
          isTemporal: false,
          sourceKind: 'Local',
          definitionVersion: 3,
          dataRevision: 11,
          fields: [
            {
              id: 41,
              code: 'Number',
              nameL10n: { values: { en: 'Permit number' } },
              dataType: 'String',
              isRequired: true,
              isScopeField: true,
              lookupRegistryDefId: null,
              unitId: null,
            },
          ],
          relations: [],
          rules: [
            {
              id: 101,
              code: PublishedRuleCode,
              ruleKind: 'Expression',
              expression: '[Value] > 1',
              severity: 'Error',
              messageL10n: { values: { en: 'Published rule' } },
              parametersJson: null,
              isActive: true,
            },
          ],
          mappings: [],
        });
      }

      if (url.includes('/history')) return json([]);
      if (url.includes('/api/v1/users')) return json({ items: [], total: 0 });
      if (url.includes('/api/v1/languages')) return json([{ code: 'en', nameNative: 'English', isDefault: true }]);
      if (/\/api\/v1\/registries(\?|$)/.test(url)) return json([]);

      return json(null);
    }),
  );

  return state;
}

function client(): QueryClient {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } });
}

/**
 * Зонд сесії — видимий доказ того, що `/me` уже в кеші.
 *
 * ⛔ Без нього перевірка «кнопки публікації немає» зелена з НЕПРАВИЛЬНОЇ
 * причини: доки профіль не доїхав, `can(...)` віддає `false` для будь-якого
 * права, і кнопки немає через порожній профіль, а не через брак саме
 * `Registry.Publish`.
 */
function SessionProbe(): JSX.Element {
  const session = useSession();

  return <span data-testid="session">{session.data === undefined ? 'pending' : 'ready'}</span>;
}

function showPanel(): void {
  render(
    <MantineProvider theme={testTheme}>
      <Notifications />
      <MemoryRouter>
        <QueryClientProvider client={client()}>
          <SessionProbe />
          <RegistryDraftPanel code={Code} request={Request} reason={DraftReason} onReasonChange={vi.fn()} />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

function showPage(): void {
  render(
    <MantineProvider theme={testTheme}>
      <Notifications />
      <MemoryRouter initialEntries={[`/admin/registries/${Code}/definition`]}>
        <QueryClientProvider client={client()}>
          <SessionProbe />
          <Routes>
            <Route path="/admin/registries/:code/definition" element={<RegistryConstructorPage />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

/**
 * Поле причини — Mantine розкладає зайві пропси на сам `input`, але це не
 * контракт: шукаємо вузол і, якщо це не поле вводу, поле всередині нього.
 */
function reasonInput(): HTMLInputElement {
  const node = document.querySelector('[data-draft-reason]');
  expect(node).not.toBeNull();

  const input = node instanceof HTMLInputElement ? node : (node as HTMLElement).querySelector('input');
  expect(input).not.toBeNull();

  return input as HTMLInputElement;
}

// ⚠ Те саме, що в сусідніх наборах: середовище прогону повільне.
const SlowEnvTimeout = 400_000;

/**
 * Для вузлів, які мають з'явитися ВІД КЛІКУ, а не від мережі: діалог
 * підтвердження малюється тим самим рендером, що й зміна стану.
 *
 * ⛔ Не SlowEnvTimeout: перевірка «публікація лише через підтвердження»
 * падає саме відсутністю діалогу, і з чотирма сотнями секунд мутація
 * червоніла б ЧЕРЕЗ ТАЙМАУТ — тобто повідомлення не називало б причини.
 */
const PromptTimeout = 30_000;

async function ready(): Promise<void> {
  await waitFor(
    () => {
      expect(screen.getByTestId('session').textContent).toBe('ready');
    },
    { timeout: SlowEnvTimeout },
  );
}

beforeEach(() => {
  notifications.cleanQueue();
  notifications.clean();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('BE-24 крок 2: чернетка опису довідника в конструкторі', () => {
  it(
    'публікація йде лише через підтвердження: сам клік не шле нічого',
    async () => {
      const state = respond();
      showPanel();
      await ready();

      const publish = await waitFor(
        () => {
          const node = document.querySelector('[data-publish-definition]');
          expect(node).not.toBeNull();
          return node as HTMLElement;
        },
        { timeout: SlowEnvTimeout },
      );

      await userEvent.click(publish);

      // ⛔ Саме ПІСЛЯ кліку: діалог уже на екрані, а запиту ще немає.
      expect(await screen.findByTestId('confirm-verb', {}, { timeout: PromptTimeout })).toBeDefined();
      expect(state.calls.some((call) => call.includes('/definition/publish'))).toBe(false);

      // ⛔ `L6`: фокус стоїть на БЕЗПЕЧНІЙ дії, а не на публікації.
      expect(document.activeElement).toBe(screen.getByTestId('confirm-cancel'));

      await userEvent.click(screen.getByTestId('confirm-verb'));

      await waitFor(
        () => {
          expect(state.calls.some((call) => call.startsWith(`POST /api/v1/registries/${Code}/definition/publish`))).toBe(true);
        },
        { timeout: SlowEnvTimeout },
      );
    },
    SlowEnvTimeout,
  );

  it(
    'скасування шле версію чернетки закодованою: у сирій адресі немає ані «+», ані «/»',
    async () => {
      const state = respond();
      showPanel();
      await ready();

      const discard = await waitFor(
        () => {
          const node = document.querySelector('[data-discard-draft]');
          expect(node).not.toBeNull();
          return node as HTMLElement;
        },
        { timeout: SlowEnvTimeout },
      );

      await userEvent.click(discard);
      await userEvent.click(await screen.findByTestId('confirm-verb', {}, { timeout: PromptTimeout }));

      const call = await waitFor(
        () => {
          const found = state.calls.find((item) => item.startsWith('DELETE '));
          expect(found).toBeDefined();
          return found as string;
        },
        { timeout: SlowEnvTimeout },
      );

      expect(call).toBe(
        `DELETE /api/v1/registries/${Code}/definition/draft?rowVersion=${EncodedRowVersion}`,
      );

      // ⛔ Друге твердження про ту саму адресу, і воно не зайве: збіг рядка
      // вище зламався б і від зміни самого шляху, а це — рівно про кодування.
      expect(call.includes(RowVersion)).toBe(false);
    },
    SlowEnvTimeout,
  );

  it(
    'конфлікт definitionDraftChanged названий причиною і дає взяти поточну версію',
    async () => {
      const state = respond({ saveConflict: 'changed' });
      showPanel();
      await ready();

      const save = await waitFor(
        () => {
          const node = document.querySelector('[data-save-draft]');
          expect(node).not.toBeNull();
          return node as HTMLElement;
        },
        { timeout: SlowEnvTimeout },
      );

      await userEvent.click(save);

      const note = await waitFor(
        () => {
          const node = document.querySelector('[data-registry-draft-conflict="changed"]');
          expect(node).not.toBeNull();
          return node as HTMLElement;
        },
        { timeout: PromptTimeout },
      );

      // ⛔ ПРИЧИНА, а не «не вдалося зберегти»: рядок каталогу за `messageKey`
      // сервера разом із кодом довідника.
      expect(note.textContent).toContain('err.ECR-REG-0409.definitionDraftChanged');
      expect(note.textContent).toContain(Code);

      // ⛔ Поверх нерозв'язаного конфлікту не зберігаємо: та сама версія дала
      // б ту саму відмову.
      expect(document.querySelector('[data-save-draft]')?.hasAttribute('disabled')).toBe(true);

      const before = state.calls.filter((call) => call.startsWith('GET ') && call.includes('/definition/draft')).length;

      state.saveConflict = 'none';
      await userEvent.click(document.querySelector('[data-take-current-draft]') as HTMLElement);

      await waitFor(
        () => {
          const after = state.calls.filter((call) => call.startsWith('GET ') && call.includes('/definition/draft')).length;
          expect(after).toBeGreaterThan(before);
        },
        { timeout: PromptTimeout },
      );

      await waitFor(
        () => {
          expect(document.querySelector('[data-registry-draft-conflict]')).toBeNull();
        },
        { timeout: PromptTimeout },
      );
    },
    SlowEnvTimeout,
  );

  it(
    'definitionDraftStale лишає чернетку на екрані і не пропонує її перечитати',
    async () => {
      respond({ saveConflict: 'stale' });
      showPanel();
      await ready();

      const save = await waitFor(
        () => {
          const node = document.querySelector('[data-save-draft]');
          expect(node).not.toBeNull();
          return node as HTMLElement;
        },
        { timeout: SlowEnvTimeout },
      );

      await userEvent.click(save);

      const note = await waitFor(
        () => {
          const node = document.querySelector('[data-registry-draft-conflict="stale"]');
          expect(node).not.toBeNull();
          return node as HTMLElement;
        },
        { timeout: PromptTimeout },
      );

      expect(note.textContent).toContain('err.ECR-REG-0409.definitionDraftStale');

      // ⛔ Кнопки «взяти поточну» тут НЕМАЄ: версія чернетки правильна,
      // розійшовся опублікований опис, і перечитування нічого не полагодить.
      expect(document.querySelector('[data-take-current-draft]')).toBeNull();

      // ⛔ Чернетка лишається на екрані, і публікувати попри це можна: рішення
      // за людиною, а відмовити чи ні — вирішує сервер.
      expect(document.querySelector('[data-testid="registry-draft-present"]')).not.toBeNull();
      expect(document.querySelector('[data-publish-definition]')?.hasAttribute('disabled')).toBe(false);
    },
    SlowEnvTimeout,
  );

  it(
    'право на публікацію перевіряється окремо від права правити опис',
    async () => {
      respond({}, ['Registry.View', 'Registry.EditDefinition']);
      showPanel();
      await ready();

      // Скасування — право `Registry.EditDefinition`, воно є: кнопка стоїть.
      await waitFor(
        () => {
          expect(document.querySelector('[data-discard-draft]')).not.toBeNull();
        },
        { timeout: SlowEnvTimeout },
      );

      // ⛔ А публікації немає: `Registry.Publish` — ІНШЕ право, і кнопка за
      // ним вела б редактора у відому `403`.
      expect(document.querySelector('[data-publish-definition]')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'дзеркало: чернетки немає — кнопок публікації і скасування теж немає',
    async () => {
      respond({ hasDraft: false });
      showPanel();
      await ready();

      await waitFor(
        () => {
          expect(document.querySelector('[data-save-draft]')).not.toBeNull();
        },
        { timeout: SlowEnvTimeout },
      );

      expect(document.querySelector('[data-testid="registry-draft-present"]')).toBeNull();
      expect(document.querySelector('[data-publish-definition]')).toBeNull();
      expect(document.querySelector('[data-discard-draft]')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'без чернетки «зберегти й опублікувати» йде ПРЯМИМ PUT …/definition',
    async () => {
      const state = respond({ hasDraft: false });
      showPanel();
      await ready();

      const direct = await waitFor(
        () => {
          const node = document.querySelector('[data-save-and-publish]');
          expect(node).not.toBeNull();
          return node as HTMLElement;
        },
        { timeout: SlowEnvTimeout },
      );

      await userEvent.click(direct);

      // ⛔ І ця дія — теж через підтвердження: вона міняє опублікований опис
      // негайно, без проміжної чернетки.
      expect(await screen.findByTestId('confirm-verb', {}, { timeout: PromptTimeout })).toBeDefined();
      expect(state.calls.some((call) => call.startsWith('PUT '))).toBe(false);

      await userEvent.click(screen.getByTestId('confirm-verb'));

      const call = await waitFor(
        () => {
          const found = state.calls.find((item) => item.startsWith('PUT '));
          expect(found).toBeDefined();
          return found as string;
        },
        { timeout: PromptTimeout },
      );

      // ⛔ Саме `…/definition`, а НЕ `…/definition/draft`: це інша дія і інші
      // права (сервер вимагає обох). Підрядковий збіг сховав би підміну.
      expect(call).toBe(`PUT /api/v1/registries/${Code}/definition`);
    },
    SlowEnvTimeout,
  );

  it(
    'поруч із наявною чернеткою прямого «зберегти й опублікувати» немає',
    async () => {
      respond();
      showPanel();
      await ready();

      // ⛔ Пастка, якої тут бути не може: прямий `PUT` підняв би версію повз
      // чернетку, і та мовчки стала б непублікованою.
      await waitFor(
        () => {
          expect(document.querySelector('[data-publish-definition]')).not.toBeNull();
        },
        { timeout: SlowEnvTimeout },
      );

      expect(document.querySelector('[data-save-and-publish]')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'сторінка засіває форму ЧЕРНЕТКОЮ і каже, коли й хто її змінив',
    async () => {
      respond();
      showPage();
      await ready();

      // ⛔ Поле з ЧЕРНЕТКИ, якого в опублікованому описі немає взагалі. Нове
      // поле — рядок, що редагується, тож це значення поля вводу, не текст.
      expect(await screen.findByDisplayValue(DraftFieldCode, {}, { timeout: SlowEnvTimeout })).toBeDefined();

      const banner = document.querySelector('[data-testid="registry-draft-present"]');
      expect(banner).not.toBeNull();

      // ⚠ Автор — саме ідентифікатор із відповіді: банер без нього не
      // відповідає на питання «чию роботу я зараз бачу».
      expect(banner?.textContent).toContain('9');

      expect(reasonInput().value).toBe(DraftReason);
    },
    SlowEnvTimeout,
  );

  it(
    'дзеркало сторінки: чернетки немає — форма показує опубліковану версію',
    async () => {
      respond({ hasDraft: false });
      showPage();
      await ready();

      // Наявне поле опублікованого опису — текстом, як і має бути (`D2-202`).
      expect(await screen.findByText('Number', {}, { timeout: SlowEnvTimeout })).toBeDefined();

      expect(screen.queryByDisplayValue(DraftFieldCode)).toBeNull();
      expect(reasonInput().value).toBe('');
    },
    SlowEnvTimeout,
  );
});

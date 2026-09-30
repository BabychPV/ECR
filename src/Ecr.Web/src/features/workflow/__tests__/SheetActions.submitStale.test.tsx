import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SheetActions } from '../SheetActions';

/**
 * F-05 (коміти `d62b3065`, `4d1259ca`): сервер відмовляє в поданні аркуша із
 * застарілими результатами методологій — `422 ECR-SUB-4221` з
 * `messageKey = err.ECR-SUB-4221.staleMethodologyResults`.
 *
 * ⛔ Що доводить цей тест: відмова доходить до тосту КОНКРЕТНИМ текстом ключа,
 * а не загальною назвою коду. Шлях: `apiFetch` (`api/client.ts`,
 * `problemBodyOf`: `messageKey` — плоске розширення тіла → `extensions2`) →
 * `useMutation.onError = showApiError` (`SheetActions.tsx`) → `problemText`
 * (`messageKey` є ⇒ `detail` уже зібрано сервером із каталогу мовою
 * користувача) → `notifications.show({ message: detail })`.
 *
 * ⚠ Клієнт НЕ кличе `t(messageKey)`: текст за ключем резолвить СЕРВЕР
 * (`ExceptionHandlingMiddleware.ResolveGenericMessageAsync`), а для ключа, якого
 * в каталозі немає, віддає сире речення розробника. Тому мок нижче моделює
 * саме цей резолвер по справжньому `09-seed.sql`: `detail` — рядок `en` із
 * блоку MERGE за `messageKey`, або сире речення з `SubmitSheetHandler`, якщо
 * ключа там немає. Так тест ловить і розбіжність ключа між кодом і сідом, і
 * загальний тост на клієнті.
 *
 * ⚠ Заголовок `err.ECR-SUB-4221` у каталозі — загальний «Submission is
 * blocked» (раніше «Orphaned rows block submission», хибний для цієї причини).
 * Тест перевіряє, що в тості його НЕМАЄ: загальна назва не каже, що робити.
 */

const StaleKey = 'err.ECR-SUB-4221.staleMethodologyResults';

/** Сире речення `SubmitSheetHandler` — те, що сервер віддає без ключа в каталозі. */
const RawHandlerMessage =
  'Подання неможливе: результати методологій застаріли — входи документа змінилися після прогону розрахунку.';

const GenericTitle = 'Submission is blocked';

/** Блок MERGE каталогу — той самий прийом, що в `sheet-fill-summary.label.test.ts`. */
function seedMergeBlock(): string {
  const seed = readFileSync(
    path.resolve(process.cwd(), '../../src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql'),
    'utf8',
  );

  const start = seed.indexOf('MERGE sys_ecr.UiString AS t');
  const end = seed.indexOf(') AS s ([Key], Lang, Val, Scope)', start);

  expect(start, '09-seed.sql: не знайдено блоку MERGE sys_ecr.UiString').toBeGreaterThan(-1);
  expect(end, '09-seed.sql: не знайдено кінця блоку MERGE').toBeGreaterThan(start);

  return seed.slice(start, end);
}

/** Текст `en` за ключем у MERGE; `null` — ключа немає. */
function catalogText(key: string): string | null {
  const escaped = key.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const row = new RegExp(`\\(N'${escaped}',\\s*N'en',\\s*N'((?:[^']|'')*)'`).exec(seedMergeBlock());

  return row?.[1]?.replace(/''/g, "'") ?? null;
}

const CurrentUser = {
  denies: [],
  grants: { 'Project:7': 'Submit' },
  isSimulation: false,
  language: 'en',
  mustChangePassword: false,
  permissions: [],
  simulatedForUserId: null,
  userId: 9,
  userName: 'author',
};

const Summary = {
  businessKey: 'DOC-1',
  createdAt: '2026-01-01T00:00:00Z',
  id: 1,
  nameL10n: null,
  projectId: 7,
  sheetCount: 1,
  sheetStates: { S1: 'Draft' },
};

const json = (body: unknown, status = 200, type = 'application/json'): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': type } });

/**
 * Відповідь сервера на Submit у формі `ExceptionHandlingMiddleware`:
 * подробиці лежать плоско поруч зі стандартними членами `problem+json`.
 *
 * @param messageKey Ключ, з яким кидає обробник; `null` — без ключа.
 */
function mockFetch(messageKey: string | null): void {
  const detail = (messageKey === null ? null : catalogText(messageKey)) ?? RawHandlerMessage;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) return json(CurrentUser);

      if (url.includes('/api/v1/documents/1/submit') && init?.method === 'POST') {
        const problem: Record<string, unknown> = {
          type: 'https://ecr.ncoc.kz/errors/ECR-SUB-4221',
          title: GenericTitle,
          status: 422,
          detail,
          instance: '/api/v1/documents/1/submit',
          errorCode: 'ECR-SUB-4221',
          correlationId: 'corr-stale-1',
          calculatedAt: '2026-09-25T10:00:00Z',
          inputsChangedAt: '2026-09-25T11:00:00Z',
        };
        if (messageKey !== null) problem['messageKey'] = messageKey;

        return json(problem, 422, 'application/problem+json');
      }

      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(['document', 1, 202601], Summary);

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <SheetActions documentId={1} sheetDefId={42} periodKey={202601} state="Draft" />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Повідомлення всіх тостів, показаних за тест. */
function toastMessages(spy: { mock: { calls: unknown[][] } }): string[] {
  return spy.mock.calls.map((call) => String((call[0] as { message: unknown }).message));
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('SheetActions: відмова Submit через застарілі результати методологій (F-05)', () => {
  it('ключ є в каталозі й каже, що робити: перерахувати перед поданням', () => {
    const text = catalogText(StaleKey);

    expect(text, `09-seed.sql: у MERGE немає ключа ${StaleKey} (en)`).not.toBeNull();
    expect(text?.toLowerCase()).toContain('stale');
    expect(text?.toLowerCase()).toContain('recalculate');
  });

  it('422 з messageKey staleMethodologyResults — тост несе текст ключа, а не загальну назву', async () => {
    const spy = vi.spyOn(notifications, 'show');
    mockFetch(StaleKey);
    show();

    fireEvent.click(await screen.findByRole('button', { name: /submit/i }));

    const expected = catalogText(StaleKey);
    expect(expected).not.toBeNull();

    await waitFor(() => {
      expect(toastMessages(spy)).toContain(expected);
    });

    const shown = toastMessages(spy).join('\n');
    expect(shown).not.toContain(GenericTitle);
    expect(shown).not.toContain(RawHandlerMessage);
  });

  it('контроль: без messageKey подробиця ховається, і тост показує лише загальне', async () => {
    // ⚠ Контрольна гілка: доводить, що попередній тест залежить саме від
    // `messageKey`, а не від того, що тост показує будь-який `detail`.
    const spy = vi.spyOn(notifications, 'show');
    vi.spyOn(console, 'debug').mockImplementation(() => undefined);
    mockFetch(null);
    show();

    fireEvent.click(await screen.findByRole('button', { name: /submit/i }));

    await waitFor(() => {
      expect(spy).toHaveBeenCalled();
    });

    const shown = toastMessages(spy);
    expect(shown).not.toContain(RawHandlerMessage);
    expect(shown).toContain(`${GenericTitle} · ECR-SUB-4221`);
  });
});

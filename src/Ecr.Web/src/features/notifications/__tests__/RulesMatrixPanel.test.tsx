import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RulesMatrixPanel } from '@/features/notifications/RulesMatrixPanel';
import type { NotificationChannel, NotificationRuleMatrix } from '@/features/notifications/api';
import { testTheme } from '@/test/render';

/**
 * Директива №15 §0, правило `L10`: **«немає прав» ≠ «порожньо» ≠ «нічого не
 * заведено»** — на матриці правил сповіщень (`BE-33b`).
 *
 * ⛔ Чому саме тут ціна найвища. `PUT /notifications/rules` замінює матрицю
 * ЦІЛКОМ: клітинка, якої немає в тілі, зникає. Отже панель, яка домалювала б
 * таблицю з НЕВІДОМОГО стану (запит правил упав, каналів — ні, або навпаки),
 * дає користувачеві кнопку, що збереже порожню матрицю поверх наявних правил,
 * — тобто мовчки вимкне всі сповіщення системи. Наслідок побачать не одразу, а
 * тоді, коли впаде задача і про це ніхто не дізнається.
 *
 * ⚠ П'ять випадків нижче — доказ у зборі, і три з них існують саме для того,
 * щоб «полагодити» це не можна було банером на все й завжди:
 *   А, Б — кожна з двох відмов окремо (у них РІЗНІ коди, і банер шукається
 *          саме за кодом, бо `role="alert"` у дереві може бути не один);
 *   В    — дзеркало: обидві відповіді на місці, матриця малюється, банера
 *          немає, кнопка доступна;
 *   Г    — каналів нуль УСПІШНОЮ відповіддю: це третій стан, не відмова;
 *   Д    — правка доїжджає в тіло `PUT` рівно тією, що на екрані.
 */

/** Канали — вісь матриці. */
const Channels: NotificationChannel[] = [
  {
    id: 7,
    name: 'Пошта чергового',
    kind: 'Smtp',
    isEnabled: true,
    hasSecret: true,
    modifiedAt: '2026-09-01T10:00:00Z',
    settings: { host: 'smtp.local', port: 25 },
  },
  {
    id: 9,
    name: 'Teams: черговий',
    kind: 'TeamsWebhook',
    isEnabled: true,
    hasSecret: true,
    modifiedAt: '2026-09-01T10:00:00Z',
    settings: { title: 'ECR' },
  },
];

/**
 * Матриця сервера.
 *
 * ⚠ `eventKinds` — УСІ п'ять видів подій, а `rules` — одна заповнена клітинка.
 * Саме так описаний контракт: клітинка без правила порожня, а не відсутня.
 */
const Matrix: NotificationRuleMatrix = {
  eventKinds: [
    'JobFailed',
    'ConsistencyIssuesFound',
    'PartitionsRunningOut',
    'CollectionFailed',
    'ExportFailed',
  ],
  rules: [{ channelId: 7, eventKind: 'JobFailed', isEnabled: true, minSeverity: 'Warning' }],
};

/**
 * Відмова читання правил.
 *
 * ⚠ `messageKey` обов'язковий: без нього `ErrorAlert` подробиці НЕ показує
 * взагалі (`problemText.mayShowDetail` — сире серверне речення не позначене як
 * зібране з каталогу), і тест перевіряв би сам лише заголовок.
 */
const RulesFailure = {
  title: 'Internal error',
  status: 500,
  detail: 'матрицю правил прочитати не вдалося',
  errorCode: 'ECR-SYS-0500',
  correlationId: 'corr-rules-1',
  messageKey: 'err.ECR-SYS-0500.contactAdmin',
};

/** Відмова читання каналів — «немає прав», інший код і інша кореляція. */
const ChannelsFailure = {
  title: 'Access denied',
  status: 403,
  detail: 'потрібне право NotificationsManage',
  errorCode: 'ECR-ACCS-0403',
  correlationId: 'corr-channels-1',
  messageKey: 'err.ECR-ACCS-0403.permission',
};

/** Чим відповідають два запити панелі. */
interface Plan {
  readonly rules: 'ok' | 'fail';
  readonly channels: 'ok' | 'fail' | 'empty';
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

/** Тіло `PUT /notifications/rules`, як його склав клієнт. */
interface SavedBody {
  rules: NotificationRuleMatrix['rules'];
}

/**
 * Заглушка сервера.
 *
 * ⚠ Підмінений саме `fetch`, а не модуль `api.ts`: доказ має пройти крізь
 * справжній `apiFetch` — інакше відмова не стала б `EcrApiError`, і код із
 * кореляцією на екрані з'явилися б із тесту, а не з продукту.
 *
 * ⚠ Шлях звіряється БЕЗ рядка запиту і на повне співпадіння.
 */
function mockServer(plan: Plan): SavedBody[] {
  const saved: SavedBody[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = String(input).split('?')[0] ?? '';
      const method = init?.method ?? 'GET';

      if (path === '/api/v1/notifications/rules' && method === 'PUT') {
        const body = JSON.parse(String(init?.body)) as SavedBody;
        saved.push(body);

        // ⚠ Сервер відповідає ВСІЄЮ матрицею — саме її панель і кладе в кеш.
        return jsonResponse({ eventKinds: Matrix.eventKinds, rules: body.rules });
      }

      if (path === '/api/v1/notifications/rules') {
        return plan.rules === 'fail' ? jsonResponse(RulesFailure, 500) : jsonResponse(Matrix);
      }

      if (path === '/api/v1/notifications/channels') {
        if (plan.channels === 'fail') return jsonResponse(ChannelsFailure, 403);

        return jsonResponse(plan.channels === 'empty' ? [] : Channels);
      }

      throw new Error(`Непередбачена адреса: ${method} ${path}`);
    }),
  );

  return saved;
}

/** Рендерить панель і віддає клієнт запитів — за ним видно, коли відповідь прийшла. */
function show(): QueryClient {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <RulesMatrixPanel />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return client;
}

/**
 * Стеля очікувань.
 *
 * ⛔ Десять секунд, а не 400 000 мс «як у сусідів»: очікування на умову, яка НЕ
 * настане, — саме так виглядає RED цього тесту — висить рівно свій ліміт, а
 * повідомлення «Unable to find …» корисніше за «Test timed out».
 */
const SettleTimeout = 10_000;
const TestTimeout = 30_000;

/** Чекає, поки ОБИДВА запити відповіли: доти дзеркальні твердження порожні. */
async function awaitSettled(
  client: QueryClient,
  rules: 'error' | 'success',
  channels: 'error' | 'success',
): Promise<void> {
  await waitFor(
    () => {
      expect(client.getQueryState(['notifications', 'rules'])?.status).toBe(rules);
      expect(client.getQueryState(['notifications', 'channels'])?.status).toBe(channels);
    },
    { timeout: SettleTimeout },
  );
}

/**
 * Банер відмови — шукається за КОДОМ, а не за роллю.
 *
 * ⛔ `role="alert"` у дереві може бути не один (`Alert` Mantine малює її й там,
 * де відмови немає), тож «є банер» перевіряється тим, що є лише в банері.
 */
async function alertWithCode(code: string): Promise<HTMLElement> {
  return waitFor(
    () => {
      const found = screen
        .getAllByRole('alert')
        .find((node) => (node.textContent ?? '').includes(code));

      expect(found).toBeTruthy();

      return found as HTMLElement;
    },
    { timeout: SettleTimeout },
  );
}

/** Прапорець клітинки «подія × канал». */
function cellBox(eventKind: string, channelName: string): HTMLElement {
  return screen.getByRole('checkbox', {
    name: `⟦notifications.event.${eventKind}⟧ · ${channelName}`,
  });
}

/**
 * Поле межі серйозності тієї самої клітинки.
 *
 * ⚠ За ПІДПИСОМ, не за роллю: полів у матриці десять, і роль у них спільна.
 */
function severityField(eventKind: string, channelName: string): HTMLElement {
  return screen.getByLabelText(
    `⟦notifications.minSeverity⟧ · ⟦notifications.event.${eventKind}⟧ · ${channelName}`,
  );
}

function saveButton(): HTMLElement | null {
  return screen.queryByRole('button', { name: '⟦notifications.saveRules⟧' });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('RulesMatrixPanel: відмова ≠ порожня матриця (L10)', () => {
  it(
    'А. GET /notifications/rules відмовляє — причина з кодом на екрані, матриці немає, зберегти не можна',
    async () => {
      mockServer({ rules: 'fail', channels: 'ok' });
      show();

      const alert = await alertWithCode('ECR-SYS-0500');

      expect(alert.textContent ?? '').toContain(RulesFailure.detail);
      expect(alert.textContent ?? '').toContain('corr-rules-1');

      // ⛔ Тупикових екранів не буває (`ФВ-14.24`).
      expect(within(alert).getByRole('button', { name: '⟦common.retry⟧' })).toBeTruthy();

      // ⛔ Головне твердження: матриці НЕМАЄ, і кнопки, яка збереже порожню
      // матрицю поверх наявних правил, — теж.
      expect(screen.queryByRole('table')).toBeNull();
      expect(saveButton()).toBeNull();
    },
    TestTimeout,
  );

  it(
    'Б. GET /notifications/channels відмовляє — те саме, і код у банері ІНШИЙ',
    async () => {
      mockServer({ rules: 'ok', channels: 'fail' });
      show();

      const alert = await alertWithCode('ECR-ACCS-0403');

      expect(alert.textContent ?? '').toContain(ChannelsFailure.detail);
      expect(alert.textContent ?? '').toContain('corr-channels-1');

      // ⚠ Правила при цьому прочиталися успішно — і це НЕ привід малювати
      // матрицю: без каналів у неї немає осі, тобто збереження стерло б усе.
      expect(screen.queryByRole('table')).toBeNull();
      expect(saveButton()).toBeNull();
    },
    TestTimeout,
  );

  it(
    'В. ДЗЕРКАЛО: обидві відповіді на місці — матриця з УСІХ подій, банера немає, кнопка доступна',
    async () => {
      mockServer({ rules: 'ok', channels: 'ok' });
      const client = show();

      // Спершу — що відповіді СПРАВДІ прийшли: інакше «банера немає» зелене
      // просто тому, що малювати ще нічого.
      await awaitSettled(client, 'success', 'success');

      expect(screen.getByRole('table')).toBeTruthy();

      // ⛔ Рядок є на КОЖЕН вид події сервера, а не лише на ті, де правило вже
      // заведене: інакше перше правило на подію завести нічим.
      for (const eventKind of Matrix.eventKinds) {
        expect(cellBox(eventKind, 'Пошта чергового')).toBeTruthy();
        expect(cellBox(eventKind, 'Teams: черговий')).toBeTruthy();
      }

      // Заповнена клітинка прийшла ввімкненою, порожня — ні.
      expect((cellBox('JobFailed', 'Пошта чергового') as HTMLInputElement).checked).toBe(true);
      expect((cellBox('JobFailed', 'Teams: черговий') as HTMLInputElement).checked).toBe(false);

      expect(saveButton()).toBeTruthy();
      expect((saveButton() as HTMLButtonElement).disabled).toBe(false);
      expect(screen.queryByRole('button', { name: '⟦common.retry⟧' })).toBeNull();
      expect(screen.queryByText(/ECR-SYS-0500|ECR-ACCS-0403/)).toBeNull();
    },
    TestTimeout,
  );

  it(
    'Г. Каналів нуль УСПІШНОЮ відповіддю — пояснення, а не «матриця без колонок»',
    async () => {
      mockServer({ rules: 'ok', channels: 'empty' });
      const client = show();

      await awaitSettled(client, 'success', 'success');

      expect(screen.getByText('⟦notifications.noChannels⟧')).toBeTruthy();
      expect(screen.getByText('⟦notifications.noChannelsHint⟧')).toBeTruthy();

      // ⛔ Це НЕ відмова: жодного банера й жодної дії «повторити».
      expect(screen.queryByRole('table')).toBeNull();
      expect(screen.queryByRole('button', { name: '⟦common.retry⟧' })).toBeNull();
    },
    TestTimeout,
  );

  it(
    'Д. Збереження шле в тілі PUT рівно те, що на екрані — і вимкнена клітинка з нього ЗНИКАЄ',
    async () => {
      const saved = mockServer({ rules: 'ok', channels: 'ok' });
      const client = show();

      await awaitSettled(client, 'success', 'success');

      // Знімаємо правило, яке прийшло з сервера…
      fireEvent.click(cellBox('JobFailed', 'Пошта чергового'));

      // …і заводимо нове на іншому каналі.
      fireEvent.click(cellBox('JobFailed', 'Teams: черговий'));

      // Межа серйозності — з набору, підписами каталогу, а не кодами сервера.
      fireEvent.click(severityField('JobFailed', 'Teams: черговий'));
      fireEvent.click(await screen.findByRole('option', { name: '⟦status.severity.Error⟧' }));

      fireEvent.click(saveButton() as HTMLElement);

      await waitFor(() => expect(saved).toHaveLength(1), { timeout: SettleTimeout });

      /*
       * ⛔ Перевіряється ТІЛО, а не факт виклику: «зберегли» й «зберегли те, що
       * бачив користувач» — різні твердження, і друге тут єдине варте уваги.
       *
       * ⚠ Знятої клітинки в тілі НЕМАЄ (а не `isEnabled: false`): сервер
       * замінює матрицю цілком, тож відсутність — це і є «правило не діє».
       */
      expect(saved[0]).toEqual({
        rules: [{ channelId: 9, eventKind: 'JobFailed', isEnabled: true, minSeverity: 'Error' }],
      });
    },
    TestTimeout,
  );
});

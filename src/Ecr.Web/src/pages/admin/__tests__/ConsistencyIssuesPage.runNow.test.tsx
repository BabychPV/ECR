import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ConsistencyIssuesPage } from '@/pages/admin/ConsistencyIssuesPage';
import { testTheme } from '@/test/render';

/**
 * Дія «перевірити зараз» на журналі узгодженості.
 *
 * ⛔ Чому вона існує: знахідку НЕ МОЖНА «взяти до відома» (`Q15-03`) — вона
 * зникає, лише коли наступна перевірка проходить. `POST /consistency/run`
 * існував, `runConsistencyCheck` існував, але споживачем виклику був тільки
 * власний тест: полагоджене вранці лишалося в переліку до наступної ночі.
 *
 * ⚠ Каталог рядків тут не вантажиться, тож `t()` дає `⟦ключ⟧` — підписи
 * шукаються саме так.
 *
 * ⚠ Код відмови в заглушках — лише з `ErrorCodes.cs` (`ECR-AUTH-0403` =
 * `ErrorCodes.Forbidden`): вигаданий код валить сторожів у чужому пуші.
 */

const RunNow = '⟦consistency.runNow⟧';

const Finding = {
  id: 7,
  detectedAt: '2026-09-18T03:00:00Z',
  severity: 3,
  ruleCode: 'BROKEN_FK',
  entityType: 'doc.TableRow',
  entityId: 4021,
  message: 'Рядок 4021 посилається на екземпляр таблиці 77 періоду 202601, якого не існує.',
  resolvedAt: null,
  resolvedByUserId: null,
};

const JobId = 'IConsistencyCheckJob#42';

type JobReply = { status: number; body: unknown };

interface Scenario {
  permissions: string[];
  enqueue?: JobReply;
  /** Відповіді на `GET /jobs/{id}` по черзі; остання повторюється. */
  job?: JobReply[];
}

interface Recorder {
  issuesCalls: () => number;
  sentBodies: string[];
  meAnswered: () => boolean;
  jobUrls: string[];
}

function problem(status: number, errorCode: string): unknown {
  return {
    type: 'about:blank',
    title: 'Forbidden',
    status,
    detail: 'Бракує права.',
    errorCode,
    correlationId: 'corr-1',
  };
}

/** Задача, яку поставив хтось інший (або нічний розклад) раніше за нас. */
const RunningJobId = 'IConsistencyCheckJob#41';

/**
 * `409` «перевірка вже йде» — у тій формі, яку пише сервер.
 *
 * ⚠ Розширення лежать ПЛОСКО на верхньому рівні тіла (RFC 9457 §3.2):
 * `ExceptionHandlingMiddleware` копіює `Details` обробника
 * (`RunConsistencyCheckHandler`: `jobId`, `state`, `messageKey`) у
 * `problem.Extensions`. `jobId === undefined` — сервер задачу не назвав.
 */
function alreadyRunning(jobId: string | undefined): JobReply {
  return {
    status: 409,
    body: {
      type: 'https://ecr.ncoc.kz/errors/ECR-JOB-0409',
      title: 'Conflict',
      status: 409,
      detail: 'A consistency check is already in progress.',
      errorCode: 'ECR-JOB-0409',
      correlationId: 'corr-2',
      messageKey: 'err.ECR-JOB-0409.consistencyCheckRunning',
      state: 'Running',
      ...(jobId === undefined ? {} : { jobId }),
    },
  };
}

function jobState(state: string, error: string | null = null): JobReply {
  return { status: 200, body: { jobId: JobId, state, percent: 0, message: null, error } };
}

function mockApi(scenario: Scenario): Recorder {
  let issuesCalls = 0;
  let meAnswered = false;
  let jobCall = 0;
  let resolved = false;
  const sentBodies: string[] = [];
  const jobUrls: string[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';

      const json = (body: unknown, status = 200): Response =>
        new Response(JSON.stringify(body), {
          status,
          headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
        });

      if (url.endsWith('/api/v1/me')) {
        meAnswered = true;

        return json({
          userId: 1,
          userName: 'admin',
          language: 'en',
          permissions: scenario.permissions,
          isSimulation: false,
          denies: [],
          grants: {},
          mustChangePassword: false,
          simulatedForUserId: null,
        });
      }

      if (url.includes('/api/v1/consistency/issues')) {
        issuesCalls += 1;

        // ⚠ Після успішного прогону знахідки вже немає — саме це й має
        // побачити адміністратор без перезавантаження сторінки.
        return json({ items: resolved ? [] : [Finding], nextCursor: null, totalCount: null });
      }

      if (url.endsWith('/api/v1/consistency/run') && method === 'POST') {
        sentBodies.push(typeof init?.body === 'string' ? init.body : '');
        const reply = scenario.enqueue ?? { status: 202, body: { jobId: JobId } };

        return json(reply.body, reply.status);
      }

      if (url.includes('/api/v1/jobs/')) {
        jobUrls.push(url);
        const replies = scenario.job ?? [jobState('Succeeded')];
        const reply = replies[Math.min(jobCall, replies.length - 1)]!;
        jobCall += 1;

        if ((reply.body as { state?: string }).state === 'Succeeded') resolved = true;

        return json(reply.body, reply.status);
      }

      throw new Error(`Немає заглушки для ${method} ${url}`);
    }),
  );

  return { issuesCalls: () => issuesCalls, sentBodies, meAnswered: () => meAnswered, jobUrls };
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/admin/consistency']}>
        <QueryClientProvider client={client}>
          <ConsistencyIssuesPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

/** Натискає «перевірити зараз», вводить причину й підтверджує. */
async function runWithReason(reason: string): Promise<void> {
  fireEvent.click(await screen.findByRole('button', { name: RunNow }));

  const dialog = await screen.findByRole('dialog');

  fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: reason } });
  fireEvent.click(within(dialog).getByRole('button', { name: RunNow }));
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ConsistencyIssuesPage: «перевірити зараз»', () => {
  it('ставить перевірку з причиною, стежить за задачею і перечитує перелік на успіху', async () => {
    const api = mockApi({
      permissions: ['System.ViewHealth', 'System.RunJob'],
      job: [jobState('Running'), jobState('Succeeded')],
    });
    show();

    expect(await screen.findByText(Finding.message)).toBeTruthy();
    const before = api.issuesCalls();

    await runWithReason('Полагодили довідник');

    // Причина їде в тілі — без неї сервер відповідає `422` на кожне натискання.
    await waitFor(() => expect(api.sentBodies).toHaveLength(1));
    expect(JSON.parse(api.sentBodies[0]!)).toEqual({ reason: 'Полагодили довідник' });

    // Спершу — «виконується», таким, яким його каже сервер.
    const status = await screen.findByRole('status');
    await waitFor(() => expect(status.getAttribute('data-outcome')).toBe('running'));
    expect(within(status).getByText('⟦consistency.runRunning⟧')).toBeTruthy();

    // `#` у ідентифікаторі закодований: інакше шлях обрізався б фрагментом.
    expect(api.jobUrls[0]).toBe('/api/v1/jobs/IConsistencyCheckJob%2342');

    // Далі спільне опитування (`pollInterval`, 1.5 с) доводить до кінцевого стану.
    await waitFor(
      () => expect(screen.getByRole('status').getAttribute('data-outcome')).toBe('succeeded'),
      { timeout: 5000 },
    );
    expect(screen.getByText('⟦consistency.runSucceeded⟧')).toBeTruthy();

    // ⛔ Головне: перелік ПЕРЕЧИТАНО, і полагоджена знахідка зникла з очей.
    await waitFor(() => expect(api.issuesCalls()).toBeGreaterThan(before));
    await waitFor(() => expect(screen.queryByText(Finding.message)).toBeNull());
    expect(await screen.findByText('⟦consistency.empty⟧')).toBeTruthy();
  }, 10000);

  it('збій задачі показано з текстом сервера, а не «успіх» і не вічне «виконується»', async () => {
    mockApi({
      permissions: ['System.ViewHealth', 'System.RunJob'],
      job: [jobState('Failed', 'Партиція 202601 недоступна.')],
    });
    show();

    await screen.findByText(Finding.message);
    await runWithReason('Полагодили довідник');

    const status = await screen.findByRole('status');
    await waitFor(() => expect(status.getAttribute('data-outcome')).toBe('failed'));
    expect(within(status).getByText('⟦consistency.runFailed⟧')).toBeTruthy();
    expect(within(status).getByText('Партиція 202601 недоступна.')).toBeTruthy();

    // Знахідка лишається: невдалий прогін нічого не знімає.
    expect(screen.getByText(Finding.message)).toBeTruthy();
  });

  it('відмову постановки видно з кодом, і це не «порожньо» і не «виконується» (L10)', async () => {
    const api = mockApi({
      permissions: ['System.ViewHealth', 'System.RunJob'],
      enqueue: { status: 403, body: problem(403, 'ECR-AUTH-0403') },
    });
    show();

    await screen.findByText(Finding.message);
    await runWithReason('Полагодили довідник');

    const alert = await screen.findByRole('alert');
    expect(within(alert).getByText('ECR-AUTH-0403')).toBeTruthy();

    // Задачі немає — стежити нема за чим, і рядка стану теж немає.
    expect(screen.queryByRole('status')).toBeNull();
    expect(api.jobUrls).toHaveLength(0);

    // Перелік не підмінено ні порожнечею, ні відмовою.
    expect(screen.getByText(Finding.message)).toBeTruthy();
  });

  it('409 «вже йде» з jobId — не відмова: стежить за ТІЄЮ задачею і перечитує перелік на успіху', async () => {
    const api = mockApi({
      permissions: ['System.ViewHealth', 'System.RunJob'],
      enqueue: alreadyRunning(RunningJobId),
      job: [jobState('Running'), jobState('Succeeded')],
    });
    show();

    expect(await screen.findByText(Finding.message)).toBeTruthy();
    const before = api.issuesCalls();

    await runWithReason('Полагодили довідник');

    // Стан — «виконується», і рядок пояснює, чому ми стежимо за чужою задачею.
    const status = await screen.findByRole('status');
    await waitFor(() => expect(status.getAttribute('data-outcome')).toBe('running'));
    expect(within(status).getByText('⟦consistency.runJoined⟧')).toBeTruthy();

    // ⛔ Стежимо саме за задачею з відповіді `409`, а не за нічим.
    expect(api.jobUrls[0]).toBe('/api/v1/jobs/IConsistencyCheckJob%2341');

    // І це не відмова: червоної панелі немає.
    expect(screen.queryByRole('alert')).toBeNull();

    await waitFor(
      () => expect(screen.getByRole('status').getAttribute('data-outcome')).toBe('succeeded'),
      { timeout: 5000 },
    );

    // Перелік перечитано — так само, як після власної задачі.
    await waitFor(() => expect(api.issuesCalls()).toBeGreaterThan(before));
    await waitFor(() => expect(screen.queryByText(Finding.message)).toBeNull());
    expect(screen.queryByRole('alert')).toBeNull();
  }, 10000);

  it('409 без jobId — відмова з кодом, стежити нема за чим', async () => {
    const api = mockApi({
      permissions: ['System.ViewHealth', 'System.RunJob'],
      enqueue: alreadyRunning(undefined),
    });
    show();

    await screen.findByText(Finding.message);
    await runWithReason('Полагодили довідник');

    const alert = await screen.findByRole('alert');
    expect(within(alert).getByText('ECR-JOB-0409')).toBeTruthy();

    expect(screen.queryByRole('status')).toBeNull();
    expect(screen.queryByText('⟦consistency.runJoined⟧')).toBeNull();
    expect(api.jobUrls).toHaveLength(0);
    expect(screen.getByText(Finding.message)).toBeTruthy();
  });

  it('«стан прочитати не вдалося» — окремий стан, а не «виконується»', async () => {
    const api = mockApi({
      permissions: ['System.RunJob'],
      job: [{ status: 403, body: problem(403, 'ECR-AUTH-0403') }],
    });
    show();

    await screen.findByText(Finding.message);
    await runWithReason('Полагодили довідник');

    const status = await screen.findByRole('status');
    await waitFor(() => expect(status.getAttribute('data-outcome')).toBe('unknown'));
    expect(within(status).getByText('⟦consistency.runUnknown⟧')).toBeTruthy();
    expect(within(status).queryByText('⟦consistency.runRunning⟧')).toBeNull();

    // ⚠ Стан не читається, отже й «успіху» немає: перелік не перечитували
    // як після завершення, а кнопка знову доступна.
    expect(api.jobUrls).toHaveLength(1);

    // ⚠ `waitFor`: доки діалог причини закривається, решта сторінки прихована
    // від дерева доступності, і кнопку не знайти за роллю.
    await waitFor(() =>
      expect(
        (screen.getByRole('button', { name: RunNow }) as HTMLButtonElement).disabled,
      ).toBe(false),
    );
  });

  describe('дзеркало права System.RunJob', () => {
    it('з правом — дія є', async () => {
      mockApi({ permissions: ['System.ViewHealth', 'System.RunJob'] });
      show();

      expect(await screen.findByRole('button', { name: RunNow })).toBeTruthy();
    });

    it('без права (лише System.ViewHealth, яким відкрито екран) — дії немає', async () => {
      const api = mockApi({ permissions: ['System.ViewHealth'] });
      show();

      await screen.findByText(Finding.message);
      await waitFor(() => expect(api.meAnswered()).toBe(true));

      // ⚠ Профіль отримано, і перелік відмальовано тим самим рендером: кнопка,
      // якби мала з'явитися, вже була б тут — див. сусідній випадок.
      await new Promise((resolve) => setTimeout(resolve, 50));
      expect(screen.queryByRole('button', { name: RunNow })).toBeNull();
    });
  });
});

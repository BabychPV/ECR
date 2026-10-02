import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { NotificationTemplatesPanel } from '@/features/notifications/NotificationTemplatesPanel';
import type { NotificationChannel, NotificationRuleMatrix } from '@/features/notifications/api';
import { placeholdersOf, templateProblem } from '@/features/notifications/notificationTemplates';
import { renderWithQuery } from '@/test/render';

/**
 * Шаблони повідомлень і адресати події (`CL-6`).
 *
 * ⛔ Заради чого файл:
 *   1. стани відмова / очікування / порожньо (ключа немає в каталозі) / немає права — різні, і жоден
 *      не малює форму, яка записала б не той рядок;
 *   2. переклад, якого немає, — ПОРОЖНЄ поле з поясненням, а не англійський текст під виглядом
 *      перекладу;
 *   3. плейсхолдер, якого сервер не заповнить, або загублений `{period}` у перекладі не доходять до
 *      `PUT` (сервер відповів би `422`, а лист — фігурними дужками);
 *   4. запис іде рівно в ключ шаблону вибраної події й мови, і лише змінене поле;
 *   5. «хто отримає» виводиться з матриці: вимкнене правило — «ніхто», межа вище `Info` — сказано.
 */

const Refusal = {
  type: 'about:blank',
  title: 'Internal Server Error',
  status: 500,
  detail: 'каталог прочитати не вдалося',
  errorCode: 'ECR-SYS-0500',
  correlationId: 'cid-templates-1',
  messageKey: 'err.ECR-SYS-0500.unexpected',
};

const Languages = [
  { code: 'en', nameNative: 'English', isDefault: true },
  { code: 'ru', nameNative: 'Русский', isDefault: false },
  { code: 'kz', nameNative: 'Қазақша', isDefault: false },
];

const EnSubject = 'ECR: period {period} opened, project {project}';
const EnBody = 'A new reporting period {period} has opened for project {project}.';

function rows(lang: string): { key: string; reference: string; value: string | null }[] {
  return [
    { key: 'notifications.periodOpened.subject', reference: EnSubject, value: lang === 'en' ? EnSubject : null },
    { key: 'notifications.periodOpened.body', reference: EnBody, value: lang === 'en' ? EnBody : null },
    {
      key: 'notifications.periodGraceStarted.subject',
      reference: 'ECR: period {period} is past its deadline, project {project}',
      value: null,
    },
    {
      key: 'notifications.periodGraceStarted.body',
      reference: 'The deadline of {period} for {project} has passed.',
      value: null,
    },
  ];
}

const Mail: NotificationChannel = {
  id: 7,
  name: 'Ops mailbox',
  kind: 'Smtp',
  isEnabled: true,
  hasSecret: false,
  modifiedAt: '2026-10-01T10:00:00Z',
  settings: { recipientRoleIds: [3] },
  transportFromConfiguration: true,
  transportConfigured: true,
};

const Teams: NotificationChannel = {
  ...Mail,
  id: 8,
  name: 'Teams ops',
  kind: 'TeamsWebhook',
  settings: {},
  transportConfigured: false,
};

const Matrix: NotificationRuleMatrix = {
  eventKinds: ['PeriodOpened', 'PeriodGraceStarted'],
  rules: [
    { eventKind: 'PeriodOpened', channelId: 7, minSeverity: 'Info', isEnabled: true },
    { eventKind: 'PeriodOpened', channelId: 8, minSeverity: 'Warning', isEnabled: true },
  ],
};

interface Call {
  readonly method: string;
  readonly path: string;
  readonly body: unknown;
}

interface Options {
  readonly permissions?: string[];
  readonly strings?: 'ok' | 'refuse' | 'pending' | 'missing';
  readonly rules?: 'ok' | 'refuse';
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

function mockServer(options: Options = {}): Call[] {
  const calls: Call[] = [];
  const permissions = options.permissions ?? ['System.ManageLocalization', 'Security.ManageRoles'];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const path = url.split('?')[0] ?? '';
      const method = (init?.method ?? 'GET').toUpperCase();
      calls.push({ method, path: url, body: typeof init?.body === 'string' ? JSON.parse(init.body) : null });

      if (path === '/api/v1/me') return json({ userId: 1, userName: 'admin', language: 'en', permissions });
      if (path === '/api/v1/languages') return json(Languages);
      if (path === '/api/v1/roles') return json([{ id: 3, code: 'ECOLOGIST' }]);
      if (path === '/api/v1/notifications/channels') {
        return options.rules === 'refuse' ? json(Refusal, 500) : json([Mail, Teams]);
      }
      if (path === '/api/v1/notifications/rules') {
        return options.rules === 'refuse' ? json(Refusal, 500) : json(Matrix);
      }
      if (path === '/api/v1/ui-strings' && method === 'GET') {
        const lang = new URL(url, 'http://x').searchParams.get('lang') ?? 'en';
        if (options.strings === 'refuse') return json(Refusal, 500);
        if (options.strings === 'pending') return new Promise<Response>(() => undefined);
        if (options.strings === 'missing') return json({ languageCode: lang, items: [] });

        return json({ languageCode: lang, items: rows(lang) });
      }
      if (path.startsWith('/api/v1/ui-strings/') && method === 'PUT') return json({ revision: 42 });

      throw new Error(`Непередбачена адреса: ${method} ${path}`);
    }),
  );

  return calls;
}

function show(): void {
  renderWithQuery(<NotificationTemplatesPanel />);
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('NotificationTemplatesPanel: стани', () => {
  it('без права System.ManageLocalization — пояснення, і каталог навіть не питається', async () => {
    const calls = mockServer({ permissions: ['Notification.Manage'] });
    show();

    expect(await screen.findByText('⟦notificationTemplates.noPermission⟧')).toBeDefined();
    expect(calls.some((call) => call.path.startsWith('/api/v1/ui-strings'))).toBe(false);
    expect(screen.queryByLabelText(/notificationTemplates\.subject⟧/)).toBeNull();
  });

  it('очікування — скелет із aria-busy, форми ще немає', async () => {
    mockServer({ strings: 'pending' });
    const { container } = renderWithQuery(<NotificationTemplatesPanel />);

    await waitFor(() =>
      expect(container.querySelector('[data-notification-templates="pending"][aria-busy="true"]')).not.toBeNull(),
    );
    expect(screen.queryByLabelText(/notificationTemplates\.subject⟧/)).toBeNull();
  });

  it('відмова каталогу — alert з повтором, а не порожня форма', async () => {
    mockServer({ strings: 'refuse' });
    show();

    expect(await screen.findByRole('alert')).toBeDefined();
    expect(screen.queryByLabelText(/notificationTemplates\.subject⟧/)).toBeNull();
    expect(screen.queryByText('⟦notificationTemplates.save⟧')).toBeNull();
  });

  it('ключа шаблону немає в каталозі — названо, форми немає', async () => {
    mockServer({ strings: 'missing' });
    show();

    expect(await screen.findByText('⟦notificationTemplates.notInCatalog⟧')).toBeDefined();
    expect(screen.queryByLabelText(/notificationTemplates\.subject⟧/)).toBeNull();
  });
});

describe('NotificationTemplatesPanel: хто отримає', () => {
  it('канали з увімкнених правил, ролі за кодом, межа вище Info і канал без транспорту — словами', async () => {
    mockServer();
    show();

    const mail = await screen.findByText(/Ops mailbox/, {}, { timeout: 5000 });
    await waitFor(() => expect(mail.textContent).toContain('ECOLOGIST'));

    const teams = screen.getByText(/Teams ops/);
    expect(teams.textContent).toContain('⟦notificationTemplates.recipientsFiltered⟧');
    expect(teams.textContent).toContain('⟦notificationTemplates.recipientsNoTransport⟧');
    expect(teams.textContent).toContain('⟦notificationTemplates.recipientsNoRoles⟧');
    expect(mail.textContent).not.toContain('⟦notificationTemplates.recipientsFiltered⟧');
  });

  it('подія без жодного увімкненого правила — «ніхто не отримає», а не порожній перелік', async () => {
    mockServer();
    show();

    await screen.findByText(/Ops mailbox/, {}, { timeout: 5000 });
    fireEvent.click(screen.getByLabelText('⟦notificationTemplates.event⟧'));
    fireEvent.click(screen.getByRole('option', { name: '⟦notifications.event.PeriodGraceStarted⟧' }));

    expect(await screen.findByText('⟦notificationTemplates.recipientsNone⟧')).toBeDefined();
    expect(screen.queryByText(/Ops mailbox/)).toBeNull();
  });

  it('відмова матриці не валить редактор: alert у блоці адресатів, поле теми лишається', async () => {
    mockServer({ rules: 'refuse' });
    show();

    const subject = await screen.findByLabelText(/notificationTemplates\.subject⟧/, {}, { timeout: 5000 });
    expect((subject as HTMLInputElement).value).toBe(EnSubject);
    expect(await screen.findByRole('alert')).toBeDefined();
  });
});

describe('NotificationTemplatesPanel: редагування', () => {
  it('зміна теми мовою за замовчуванням — PUT рівно цього ключа, тіло не надсилається', async () => {
    const calls = mockServer();
    show();

    const subject = await screen.findByLabelText(/notificationTemplates\.subject⟧/, {}, { timeout: 5000 });
    fireEvent.change(subject, { target: { value: 'ECR: {project} — period {period} is open' } });
    expect(screen.getByTestId('notification-templates-unsaved')).toBeDefined();

    fireEvent.click(screen.getByText('⟦notificationTemplates.save⟧'));

    await waitFor(() => expect(calls.filter((call) => call.method === 'PUT')).toHaveLength(1));
    const put = calls.find((call) => call.method === 'PUT');
    expect(put?.path).toBe('/api/v1/ui-strings/en/notifications.periodOpened.subject');
    expect(put?.body).toEqual({ value: 'ECR: {project} — period {period} is open', scope: 'Private' });
    await waitFor(() => expect(screen.queryByTestId('notification-templates-unsaved')).toBeNull());
  });

  it('невідомий плейсхолдер — помилка поля, кнопка неактивна, PUT немає', async () => {
    const calls = mockServer();
    show();

    const subject = await screen.findByLabelText(/notificationTemplates\.subject⟧/, {}, { timeout: 5000 });
    fireEvent.change(subject, { target: { value: 'ECR: {period} {site}' } });

    expect(screen.getByText(/notificationTemplates\.problemUnknownPlaceholder/)).toBeDefined();
    const save = screen.getByText('⟦notificationTemplates.save⟧').closest('button');
    expect(save?.disabled).toBe(true);
    fireEvent.click(save as HTMLButtonElement);
    expect(calls.some((call) => call.method === 'PUT')).toBe(false);
  });

  it('казахська без перекладу — порожнє поле і пояснення; загублений {period} не записується', async () => {
    const calls = mockServer();
    show();

    await screen.findByLabelText(/notificationTemplates\.subject⟧/, {}, { timeout: 5000 });
    fireEvent.click(screen.getByLabelText('⟦notificationTemplates.language⟧'));
    fireEvent.click(screen.getByRole('option', { name: 'Қазақша' }));

    expect(await screen.findByText('⟦notificationTemplates.untranslated⟧')).toBeDefined();
    const subject = screen.getByLabelText(/notificationTemplates\.subject⟧/) as HTMLInputElement;
    // ⛔ Не англійський еталон під виглядом перекладу.
    expect(subject.value).toBe('');

    fireEvent.change(subject, { target: { value: 'ECR: {project} жобасы' } });
    expect(screen.getByText(/err\.ECR-REQ-0422\.placeholderMismatch/)).toBeDefined();
    expect(screen.getByText('⟦notificationTemplates.save⟧').closest('button')?.disabled).toBe(true);

    fireEvent.change(subject, { target: { value: 'ECR: {period} кезеңі ашылды, {project} жобасы' } });
    fireEvent.click(screen.getByText('⟦notificationTemplates.save⟧'));

    await waitFor(() => expect(calls.some((call) => call.method === 'PUT')).toBe(true));
    expect(calls.find((call) => call.method === 'PUT')?.path).toBe(
      '/api/v1/ui-strings/kz/notifications.periodOpened.subject',
    );
  });
});

describe('notificationTemplates: правила сервера', () => {
  it('плейсхолдери — набір без повторів у порядку ordinal, як UiStringResolver.Placeholders', () => {
    expect(placeholdersOf('{project} {period} {project} {Period}')).toEqual(['Period', 'period', 'project']);
  });

  it('мова за замовчуванням: порожньо — ні; переклад: порожньо — так (лист піде еталоном)', () => {
    expect(templateProblem('  ', EnSubject, true)).toEqual({ kind: 'empty' });
    expect(templateProblem('', EnSubject, false)).toBeNull();
  });

  it('переклад мусить нести РІВНО набір еталона', () => {
    expect(templateProblem('{period}', EnSubject, false)).toMatchObject({ kind: 'placeholderMismatch' });
    expect(templateProblem('{project} — {period}', EnSubject, false)).toBeNull();
  });
});

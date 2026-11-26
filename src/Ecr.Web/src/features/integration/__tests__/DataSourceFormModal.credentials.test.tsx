import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { DataSourceFormModal } from '@/features/integration/DataSourceFormModal';
import type { DataSource } from '@/features/integration/dataSourceApi';
import { renderKeepMounted } from '@/test/closedDialogProbe';

/**
 * Облікові дані джерела у формі (P-1126-SRCCRED, AC-1): перемикач Windows / логін і пароль, пароль лише для запису,
 * «•••• set» + Change/Clear, повторне введення при зміні цілі, стани «відхилено / не читається», PiWebApi без
 * перемикача, «Test connection» з тіла форми. Каталог у тестах віддає `⟦ключ⟧`.
 *
 * МУТАЦІЯ (доведено вручну): `credentialsDraftOf` кладе в чернетку `source.password` (значення з даних сервера в
 * `value` поля) — червоний «значення пароля не потрапляє в DOM».
 */

/** Мітка, якої в DOM не має бути ніколи; випадкова, не реальний пароль. */
const Sentinel = 'ZZpwd-3f9c1d7e-sentinel';

interface Call {
  readonly method: string;
  readonly path: string;
  readonly body: Record<string, unknown> | null;
}

let calls: Call[] = [];

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function serve(answer: (call: Call) => Response = () => json({})): void {
  calls = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const call: Call = {
        method: String(init?.method ?? 'GET').toUpperCase(),
        path: new URL(String(input), 'http://localhost').pathname,
        body: init?.body === undefined ? null : (JSON.parse(String(init.body)) as Record<string, unknown>),
      };
      if (call.method !== 'GET') calls.push(call);

      return call.method === 'GET' ? json({}) : answer(call);
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const Base = {
  id: 5,
  code: 'PI_MAIN',
  nameL10n: { en: 'Main' },
  transport: 'PiSqlClient',
  endpoint: 'Driver={PI SQL Client};AF Server=pi.example.local;AF Database=ECR',
  secondaryEndpoint: null,
  catalog: null,
  maxParallel: 4,
  isActive: true,
  hasSecret: false,
  rowVersion: 'AAAAAAAAB9E=',
  sourceEntities: 0,
  collectionSchedules: 0,
  authMode: 'Windows',
  userName: null,
  hasPassword: false,
  credentialsState: 'Ok',
  credentialsRejectedAt: null,
} as unknown as DataSource;

/** Збережене джерело з логіном і паролем; `password` у даних сервера — навмисна пастка для мутації. */
const Stored = { ...Base, authMode: 'UserPassword', userName: 'svc_ecr', hasPassword: true, password: Sentinel } as unknown as DataSource;

function open(source: DataSource | null): void {
  renderKeepMounted(<DataSourceFormModal opened source={source} onClose={() => undefined} />);
}

const mode = (): HTMLElement => screen.getByLabelText('⟦sources.authMode⟧');
const passwordInput = (): HTMLInputElement => screen.getByLabelText(/sources\.password⟧/) as HTMLInputElement;
const userNameInput = (): HTMLInputElement => screen.getByLabelText(/sources\.userName⟧/) as HTMLInputElement;

function pick(label: string): void {
  fireEvent.click(mode());
  fireEvent.click(screen.getByRole('option', { name: label }));
}

function type(input: HTMLElement, value: string): void {
  fireEvent.change(input, { target: { value } });
}

function fillNew(): void {
  type(screen.getByLabelText(/sources\.code⟧/), 'NEW_ONE');
  type(screen.getByLabelText(/sources\.name⟧/), 'New one');
  type(screen.getByLabelText(/sources\.endpoint⟧/), 'Driver={PI SQL Client};AF Server=x.example.local');
}

const saveButton = (name: string): HTMLButtonElement => screen.getByRole('button', { name }) as HTMLButtonElement;

describe('Облікові дані джерела у формі', () => {
  it('значення пароля не потрапляє в DOM, навіть коли сервер каже hasPassword', () => {
    serve();
    open(Stored);

    // Збережений пароль — лише статичний знак, без поля вводу.
    expect(screen.getByText('⟦sources.passwordSet⟧')).toBeTruthy();
    expect(screen.queryByLabelText(/sources\.password⟧/)).toBeNull();
    expect(document.body.innerHTML).not.toContain(Sentinel);

    // «Change password» відкриває ПОРОЖНЄ поле.
    fireEvent.click(screen.getByRole('button', { name: '⟦sources.changePassword⟧' }));
    expect(passwordInput().value).toBe('');
    expect(passwordInput().type).toBe('password');
    expect(passwordInput().getAttribute('autocomplete')).toBe('new-password');
    expect(document.body.innerHTML).not.toContain(Sentinel);
  });

  it('перемикач режиму: Windows не має полів логіна й пароля, «логін і пароль» їх показує й шле в тілі', async () => {
    serve();
    open(null);
    fillNew();

    expect(screen.queryByLabelText(/sources\.userName⟧/)).toBeNull();
    expect(screen.queryByLabelText(/sources\.password⟧/)).toBeNull();

    pick('⟦sources.authMode.UserPassword⟧');
    // Створення: пароль обов'язковий — кнопка недоступна, доки його немає.
    expect(saveButton('⟦sources.create⟧').disabled).toBe(true);

    type(userNameInput(), 'DOMAIN\\ecr_reader');
    type(passwordInput(), 'test-pw 1 ');
    expect(saveButton('⟦sources.create⟧').disabled).toBe(false);

    fireEvent.click(saveButton('⟦sources.create⟧'));
    await waitFor(() => expect(calls).toHaveLength(1));

    expect(calls[0]?.method).toBe('POST');
    expect(calls[0]?.body).toMatchObject({
      authMode: 'UserPassword',
      userName: 'DOMAIN\\ecr_reader',
      password: 'test-pw 1 ',
      clearPassword: false,
    });
  });

  it('Windows у новому з\'єднанні: логін і пароль у тіло не потрапляють', async () => {
    serve();
    open(null);
    fillNew();

    fireEvent.click(saveButton('⟦sources.create⟧'));
    await waitFor(() => expect(calls).toHaveLength(1));

    expect(calls[0]?.body).toMatchObject({ authMode: 'Windows', userName: null, password: null });
  });

  it('збереження без зміни пароля: поле не шле password, лише логін; пароль сервер лишає', async () => {
    serve();
    open(Stored);

    fireEvent.click(saveButton('⟦common.save⟧'));
    await waitFor(() => expect(calls).toHaveLength(1));

    expect(calls[0]?.body).toMatchObject({ authMode: 'UserPassword', userName: 'svc_ecr', password: null, clearPassword: false });
  });

  it('перехід на Windows просить підтвердження, а підтверджений — стирає логін і пароль', async () => {
    serve();
    open(Stored);

    pick('⟦sources.authMode.Windows⟧');
    expect(screen.getByText('⟦sources.switchToWindowsConfirm⟧')).toBeTruthy();
    // Поки не підтверджено — режим не змінився.
    expect(calls).toHaveLength(0);

    fireEvent.click(screen.getByTestId('confirm-verb'));
    await waitFor(() => expect(screen.queryByLabelText(/sources\.userName⟧/)).toBeNull());

    fireEvent.click(saveButton('⟦common.save⟧'));
    await waitFor(() => expect(calls).toHaveLength(1));

    expect(calls[0]?.body).toMatchObject({ authMode: 'Windows', userName: null, password: null, clearPassword: true });
  });

  it('«Clear» теж іде через підтвердження переходу на Windows', async () => {
    serve();
    open(Stored);

    fireEvent.click(screen.getByRole('button', { name: '⟦sources.clearPassword⟧' }));
    expect(screen.getByText('⟦sources.switchToWindowsConfirm⟧')).toBeTruthy();
  });

  it('зміна адреси на джерелі зі збереженим паролем вимагає пароль ще раз', async () => {
    serve();
    open(Stored);

    type(screen.getByLabelText(/sources\.endpoint⟧/), 'Driver={PI SQL Client};AF Server=other.example.local');

    // «•••• set» зникає, з'являється порожнє обов'язкове поле; без нього зберегти не можна.
    expect(screen.queryByText('⟦sources.passwordSet⟧')).toBeNull();
    expect(passwordInput().value).toBe('');
    expect(saveButton('⟦common.save⟧').disabled).toBe(true);

    type(passwordInput(), 'test-new-pw');
    fireEvent.click(saveButton('⟦common.save⟧'));
    await waitFor(() => expect(calls).toHaveLength(1));

    expect(calls[0]?.body).toMatchObject({ password: 'test-new-pw', userName: 'svc_ecr' });
  });

  it('зміна логіна теж вимагає пароль; відмова сервера reentry стоїть біля поля пароля', async () => {
    serve(() =>
      json(
        {
          title: 'Unprocessable',
          status: 422,
          errorCode: 'ECR-REQ-0422',
          correlationId: 'c-1',
          messageKey: 'err.ECR-REQ-0422.dataSourcePasswordReentryRequired',
        },
        422,
      ),
    );
    open(Stored);

    type(userNameInput(), 'svc_other');
    expect(saveButton('⟦common.save⟧').disabled).toBe(true);

    type(passwordInput(), 'test-pw');
    fireEvent.click(saveButton('⟦common.save⟧'));

    const message = await screen.findByText('⟦err.ECR-REQ-0422.dataSourcePasswordReentryRequired⟧');
    expect(passwordInput().getAttribute('aria-describedby')).toContain(message.id);
  });

  it('PiWebApi: перемикача й полів логіна немає', () => {
    serve();
    open({ ...Base, transport: 'PiWebApi', endpoint: 'https://pi.example.local/piwebapi' } as unknown as DataSource);

    expect(screen.queryByLabelText('⟦sources.authMode⟧')).toBeNull();
    expect(screen.queryByLabelText(/sources\.userName⟧/)).toBeNull();
    expect(screen.queryByLabelText(/sources\.password⟧/)).toBeNull();
  });

  it('збережене з логіном джерело, що переїхало на PiWebApi, шле Windows і стирає пароль', async () => {
    serve();
    open(Stored);

    fireEvent.click(screen.getByLabelText(/sources\.transport⟧/));
    fireEvent.click(screen.getByRole('option', { name: 'PiWebApi' }));
    // Режим після збереження — Windows, а транспорт змінився: потрібне підтвердження зміни адреси.
    fireEvent.click(document.querySelector('input[data-field="confirmEndpointChange"]') as HTMLElement);
    fireEvent.click(saveButton('⟦common.save⟧'));
    await waitFor(() => expect(calls).toHaveLength(1));

    expect(calls[0]?.body).toMatchObject({ authMode: 'Windows', userName: null, password: null, clearPassword: true });
  });

  it('«вхід відхилено» при збереженні: показує текст стану й відкриває поле пароля', async () => {
    serve(() =>
      json(
        {
          title: 'Conflict',
          status: 409,
          errorCode: 'ECR-INT-0502',
          correlationId: 'c-2',
          messageKey: 'err.ECR-INT-0502.credentialsRejected',
        },
        409,
      ),
    );
    open(Stored);

    fireEvent.click(saveButton('⟦common.save⟧'));

    expect(await screen.findByText('⟦sources.credentialsRejected⟧')).toBeTruthy();
    expect(passwordInput().value).toBe('');
  });

  it('Test connection з тіла форми шле значення форми, а відповідь показує через каталог', async () => {
    serve(() => json({ ok: false, entities: 0, error: 'auth', messageKey: 'integration.test.failed.credentialsUnreadable' }));
    open(Stored);

    type(screen.getByLabelText(/sources\.testReason⟧/), 'check');
    fireEvent.click(screen.getByRole('button', { name: '⟦sources.testConnection⟧' }));

    await waitFor(() => expect(calls).toHaveLength(1));
    expect(calls[0]?.path).toBe('/api/v1/data-sources/test-connection');
    expect(calls[0]?.body).toMatchObject({
      sourceId: 5,
      transport: 'PiSqlClient',
      authMode: 'UserPassword',
      userName: 'svc_ecr',
      password: null,
      reason: 'check',
    });

    // «Не читається» — з єдиною дією: ввести пароль заново.
    expect(await screen.findByText('⟦integration.test.failed.credentialsUnreadable (dataSource=Main)⟧')).toBeTruthy();
    fireEvent.click(document.querySelector('[data-enter-password]') as HTMLElement);
    expect(passwordInput().value).toBe('');
  });

  it('Test connection недоступний для PiWebApi (сервер перевіряє лише SQL-транспорти)', () => {
    serve();
    open({ ...Base, transport: 'PiWebApi', endpoint: 'https://pi.example.local/piwebapi' } as unknown as DataSource);

    expect(screen.queryByRole('button', { name: '⟦sources.testConnection⟧' })).toBeNull();
  });
  it('credentialsState Rejected з сервера: стан видно одразу, пароль просять заново', () => {
    serve();
    open({ ...Stored, credentialsState: 'Rejected', credentialsRejectedAt: '2026-11-26T10:00:00Z' } as unknown as DataSource);

    expect(screen.getByText('⟦sources.credentialsRejected⟧')).toBeTruthy();
    expect(passwordInput().value).toBe('');
    expect(saveButton('⟦common.save⟧').disabled).toBe(true);
  });

  it('429 при Test connection: ліміт проб і пауза цілі показують власні тексти біля кнопки', async () => {
    const throttled = (key: string): Response =>
      json({ title: 'Too many', status: 429, errorCode: 'ECR-REQ-0429', correlationId: 'c-3', messageKey: key }, 429);

    serve(() => throttled('err.ECR-REQ-0429.tooManySourceTests'));
    open(Stored);
    type(screen.getByLabelText(/sources\.testReason⟧/), 'check');
    fireEvent.click(screen.getByRole('button', { name: '⟦sources.testConnection⟧' }));
    expect(await screen.findByText('⟦err.ECR-REQ-0429.tooManySourceTests⟧')).toBeTruthy();

    serve(() => throttled('err.ECR-REQ-0429.sourceTestTargetPaused'));
    fireEvent.click(screen.getByRole('button', { name: '⟦sources.testConnection⟧' }));
    expect(await screen.findByText('⟦err.ECR-REQ-0429.sourceTestTargetPaused⟧')).toBeTruthy();
  });

  it('підтвердження зміни адреси: потрібне, коли режим після збереження Windows, і не потрібне при UserPassword', () => {
    serve();
    open(Base);
    type(screen.getByLabelText(/sources\.endpoint⟧/), 'Driver={PI SQL Client};AF Server=moved.example.local');
    expect(document.querySelector('[data-field="confirmEndpointChange"]')).not.toBeNull();

    // Windows → UserPassword з новою адресою: пароль, а не підтвердження.
    pick('⟦sources.authMode.UserPassword⟧');
    expect(document.querySelector('[data-field="confirmEndpointChange"]')).toBeNull();
  });

  it('UserPassword → Windows з новою адресою: потрібні і підтвердження переходу, і підтвердження адреси', async () => {
    serve();
    open(Stored);
    type(screen.getByLabelText(/sources\.endpoint⟧/), 'Driver={PI SQL Client};AF Server=moved.example.local');
    expect(document.querySelector('[data-field="confirmEndpointChange"]')).toBeNull();

    pick('⟦sources.authMode.Windows⟧');
    fireEvent.click(screen.getByTestId('confirm-verb'));
    await waitFor(() => expect(document.querySelector('[data-field="confirmEndpointChange"]')).not.toBeNull());
    expect(saveButton('⟦common.save⟧').disabled).toBe(true);
  });
  it('PiSqlClient: «{», «}», «;» у логіні чи паролі — відмова до відправки, текст біля поля', () => {
    serve();
    open(null);
    fillNew();
    pick('⟦sources.authMode.UserPassword⟧');
    type(userNameInput(), 'svc');
    type(passwordInput(), 'ok-pw');
    expect(saveButton('⟦sources.create⟧').disabled).toBe(false);

    type(passwordInput(), 'a;b');
    expect(screen.getByText('⟦err.ECR-REQ-0422.dataSourceCredentialsCharacters⟧')).toBeTruthy();
    expect(saveButton('⟦sources.create⟧').disabled).toBe(true);

    type(passwordInput(), 'ok-pw');
    type(userNameInput(), 'sv{c');
    expect(screen.getByText('⟦err.ECR-REQ-0422.dataSourceCredentialsCharacters⟧')).toBeTruthy();
    expect(saveButton('⟦sources.create⟧').disabled).toBe(true);
    expect(calls).toHaveLength(0);
  });

  it('422 dataSourcePasswordUnencrypted стоїть біля поля адреси', async () => {
    serve(() =>
      json(
        {
          title: 'Unprocessable',
          status: 422,
          errorCode: 'ECR-REQ-0422',
          correlationId: 'c-4',
          messageKey: 'err.ECR-REQ-0422.dataSourcePasswordUnencrypted',
        },
        422,
      ),
    );
    open({ ...Stored, transport: 'Sql', endpoint: 'Server=a;Database=b' } as unknown as DataSource);
    fireEvent.click(saveButton('⟦common.save⟧'));

    const message = await screen.findByText('⟦err.ECR-REQ-0422.dataSourcePasswordUnencrypted⟧');
    expect(screen.getByLabelText(/sources\.endpoint⟧/).getAttribute('aria-describedby')).toContain(message.id);
  });

  it('422 dataSourceCredentialsCharacters від сервера стоїть біля поля пароля', async () => {
    serve(() =>
      json(
        {
          title: 'Unprocessable',
          status: 422,
          errorCode: 'ECR-REQ-0422',
          correlationId: 'c-5',
          messageKey: 'err.ECR-REQ-0422.dataSourceCredentialsCharacters',
        },
        422,
      ),
    );
    open(Stored);
    fireEvent.click(saveButton('⟦common.save⟧'));

    expect(await screen.findByText('⟦err.ECR-REQ-0422.dataSourceCredentialsCharacters⟧')).toBeTruthy();
  });

  it('Test connection: connectFailedNoRetry показує текст каталогу з назвою зʼєднання', async () => {
    serve(() => json({ ok: false, entities: 0, error: 'other', messageKey: 'err.ECR-INT-0503.connectFailedNoRetry' }));
    open(Stored);
    type(screen.getByLabelText(/sources\.testReason⟧/), 'check');
    fireEvent.click(screen.getByRole('button', { name: '⟦sources.testConnection⟧' }));

    expect(await screen.findByText('⟦err.ECR-INT-0503.connectFailedNoRetry (dataSource=Main)⟧')).toBeTruthy();
  });
});

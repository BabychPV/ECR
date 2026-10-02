import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SmtpSettingsPanel } from '@/features/notifications/SmtpSettingsPanel';
import { testTheme } from '@/test/render';

/**
 * SMTP-панель (`D-263`): стани «очікування / відмова → повтор / не налаштовано», доступні імена
 * полів, і подвійний клік, що не шле другий запит.
 *
 * Мутаційні докази (лише локально): без `if (save.isPending) return` — два `PUT` замість одного;
 * без `if (test.isPending) return` — дві проби; `t(key)` замість категорії у відмові проби — сирий
 * текст сервера в сповіщенні.
 */

const Refusal = {
  type: 'about:blank',
  title: 'Internal Server Error',
  status: 500,
  detail: 'налаштування SMTP прочитати не вдалося',
  errorCode: 'ECR-SYS-0500',
  correlationId: 'cid-smtp-states',
};

const Empty = {
  host: '',
  port: 587,
  encryptionMode: 'StartTls',
  fromAddress: '',
  fromName: null,
  authMode: 'None',
  userName: null,
  hasPassword: false,
  isEnabled: false,
  source: 'none',
  configured: false,
  updatedAt: null,
};

interface Call {
  readonly method: string;
  readonly path: string;
}

interface Script {
  /** Відповіді на `GET` по черзі; остання повторюється. */
  readonly reads: (() => Promise<Response>)[];
  readonly write?: () => Promise<Response>;
  readonly test?: () => Promise<Response>;
}

const json = (body: unknown, status = 200): Promise<Response> =>
  Promise.resolve(
    new Response(JSON.stringify(body), {
      status,
      headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
    }),
  );

/** Відповідь, що приходить лише після `release()`: запит «у дорозі» під час другого кліку. */
function later(body: unknown): { readonly reply: () => Promise<Response>; readonly release: () => void } {
  let release: () => void = () => undefined;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });

  return { reply: async () => gate.then(() => json(body)), release: () => release() };
}

function mockServer(script: Script): Call[] {
  const calls: Call[] = [];
  let reads = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = String(input).split('?')[0] ?? '';
      const method = init?.method ?? 'GET';
      calls.push({ method, path });

      if (path.endsWith('/api/v1/notifications/smtp/test')) return (script.test ?? (() => json({ ok: true })))();
      if (path.endsWith('/api/v1/notifications/smtp')) {
        if (method === 'PUT') return (script.write ?? (() => json(Empty)))();
        const next = script.reads[Math.min(reads, script.reads.length - 1)] ?? (() => json(Empty));
        reads += 1;

        return next();
      }

      return json(null);
    }),
  );

  return calls;
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <Notifications />
      <QueryClientProvider client={client}>
        <SmtpSettingsPanel />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Дати другому кліку час дійти до мережі, якби обробник його пропустив. */
const settle = async (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 60));

const count = (calls: Call[], method: string, suffix: string): number =>
  calls.filter((c) => c.method === method && c.path.endsWith(suffix)).length;

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SmtpSettingsPanel — стани', () => {
  it('очікування: заголовок і скелет, але жодного поля, яке можна було б «зберегти» порожнім', async () => {
    mockServer({ reads: [() => new Promise<Response>(() => undefined)] });
    show();

    expect(screen.getByRole('heading', { name: /smtp\.title/ })).toBeDefined();
    await waitFor(() => expect(document.querySelector('[data-smtp="pending"]')).not.toBeNull());
    expect(screen.queryByRole('textbox', { name: /smtp\.host/ })).toBeNull();
    expect(screen.queryByRole('button', { name: /smtp\.save/ })).toBeNull();
  });

  it('відмова → «Повторити» перечитує, і лише тоді з\'являється форма', async () => {
    const calls = mockServer({ reads: [() => json(Refusal, 500), () => json(Empty)] });
    show();

    const alert = await screen.findByRole('alert');
    expect(alert.textContent ?? '').toContain('ECR-SYS-0500');
    expect(screen.queryByRole('textbox', { name: /smtp\.host/ })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: /common\.retry/ }));

    expect(await screen.findByRole('textbox', { name: /smtp\.host/ })).toBeDefined();
    expect(count(calls, 'GET', '/notifications/smtp')).toBe(2);
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('не налаштовано: джерело «немає» словами, поля мають імена, кнопки неактивні без змін', async () => {
    mockServer({ reads: [() => json(Empty)] });
    show();

    await screen.findByRole('textbox', { name: /smtp\.host/ });
    expect(document.querySelector('[data-smtp-source="none"]')?.textContent).toContain('smtp.source.none');

    // Кожне поле — з доступним ім'ям: читалка не оголошує «поле редагування» без підпису.
    for (const name of [/smtp\.port/, /smtp\.from⟧/, /smtp\.fromName/, /smtp\.testTo/]) {
      expect(screen.getByRole('textbox', { name })).toBeDefined();
    }
    expect(screen.getByRole('switch', { name: /smtp\.enabled/ })).toBeDefined();
    // Без пароля в способі входу полів облікового запису немає.
    expect(screen.queryByLabelText(/smtp\.password/)).toBeNull();

    expect(screen.getByRole('button', { name: /smtp\.save/ })).toHaveProperty('disabled', true);
    expect(screen.getByRole('button', { name: /smtp\.testSend/ })).toHaveProperty('disabled', true);

    // Незбережена правка: проба пішла б зі СТАРИМИ налаштуваннями — кнопка проби неактивна.
    fireEvent.change(screen.getByRole('textbox', { name: /smtp\.testTo/ }), { target: { value: 'me@corp.example' } });
    expect(screen.getByRole('button', { name: /smtp\.testSend/ })).toHaveProperty('disabled', false);
    fireEvent.change(screen.getByRole('textbox', { name: /smtp\.host/ }), { target: { value: 'relay.corp.example' } });
    expect(screen.getByRole('button', { name: /smtp\.testSend/ })).toHaveProperty('disabled', true);
    expect(screen.getByRole('button', { name: /smtp\.save/ })).toHaveProperty('disabled', false);
  });

  it('подвійний клік «Зберегти», поки PUT у дорозі, — рівно один PUT', async () => {
    const saved = later({ ...Empty, host: 'relay.corp.example', source: 'database', configured: true });
    const calls = mockServer({ reads: [() => json(Empty)], write: saved.reply });
    show();

    fireEvent.change(await screen.findByRole('textbox', { name: /smtp\.host/ }), {
      target: { value: 'relay.corp.example' },
    });
    const save = screen.getByRole('button', { name: /smtp\.save/ });
    fireEvent.click(save);
    await waitFor(() => expect(count(calls, 'PUT', '/notifications/smtp')).toBe(1));

    // Другий клік до порогу спінера: кнопка ще активна, стримує лише обробник.
    expect(save).toHaveProperty('disabled', false);
    fireEvent.click(save);
    await settle();
    expect(count(calls, 'PUT', '/notifications/smtp')).toBe(1);
    saved.release();
    await waitFor(() => expect(save).toHaveProperty('disabled', true));
    expect(count(calls, 'PUT', '/notifications/smtp')).toBe(1);
  });

  it('подвійний клік проби — один лист; відмова транспорту — категорією з каталогу, а не сирим текстом', async () => {
    const result = later({ ok: false, error: 'socket closed by 10.0.0.5', messageKey: 'notifications.test.smtp.dns' });
    const calls = mockServer({ reads: [() => json(Empty)], test: result.reply });
    show();

    fireEvent.change(await screen.findByRole('textbox', { name: /smtp\.testTo/ }), {
      target: { value: 'me@corp.example' },
    });
    const send = screen.getByRole('button', { name: /smtp\.testSend/ });
    fireEvent.click(send);
    await waitFor(() => expect(count(calls, 'POST', '/smtp/test')).toBe(1));

    fireEvent.click(send);
    await settle();
    expect(count(calls, 'POST', '/smtp/test')).toBe(1);
    result.release();

    expect(await screen.findByText(/notifications\.test\.smtp\.dns/)).toBeDefined();
    expect(screen.queryByText(/socket closed/)).toBeNull();
    expect(count(calls, 'POST', '/smtp/test')).toBe(1);
  });

  it('відмова проби без відомої категорії — загальне «канал відхилив», сирий текст транспорту не показано', async () => {
    mockServer({ reads: [() => json(Empty)], test: () => json({ ok: false, error: 'EHLO rejected by 10.0.0.5', messageKey: null }) });
    show();

    fireEvent.change(await screen.findByRole('textbox', { name: /smtp\.testTo/ }), {
      target: { value: 'me@corp.example' },
    });
    fireEvent.click(screen.getByRole('button', { name: /smtp\.testSend/ }));

    expect(await screen.findByText(/notifications\.testFailed/)).toBeDefined();
    expect(screen.queryByText(/EHLO rejected/)).toBeNull();
  });
});

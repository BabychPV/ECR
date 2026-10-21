import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { testTheme } from '@/test/render';
import { PermitLifecycleDialogs, type PermitActionTarget } from '../PermitLifecycleDialogs';
import { makePermit } from './permitFixture';

/**
 * Діалоги життєвого циклу дозволу з переліку (PM-07): кожна дія кличе свій ендпоінт з правильним тілом,
 * відмова сервера лишає діалог відкритим і показується через `showApiError`, поновлення веде на нову чернетку.
 *
 * Мутаційний доказ (вручну, відновлено): без `if (busy.current) return` подвійний клік шле два запити (валить
 * «подвійний клік»); `endDate: null` на `endDate: '2026-01-01'` валить тіло завершення; `onClose()` в `onError`
 * валить «відмова лишає діалог відкритим»; `navigate(...)` після поновлення прибрати — валить «нова чернетка».
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    params === undefined ? key : `${key} ${JSON.stringify(params)}`,
  language: () => 'en',
}));

const notify = vi.hoisted(() => ({ showApiError: vi.fn(), showDone: vi.fn() }));
vi.mock('@/shared/ui/notify', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/ui/notify')>()),
  showApiError: notify.showApiError,
  showDone: notify.showDone,
}));

interface Call {
  readonly method: string;
  readonly url: string;
  readonly body: string | null;
}

let calls: Call[] = [];

function stub(reply: (call: Call) => Response): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const call = { method: init?.method ?? 'GET', url: String(input), body: (init?.body as string | undefined) ?? null };
      calls.push(call);

      return reply(call);
    }),
  );
}

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const conflict = (): Response =>
  json({ title: 'Permit validity overlaps', status: 409, errorCode: 'ECR-PRM-4092', correlationId: 'c' }, 409);

function Where(): null {
  (globalThis as { __where?: string }).__where = useLocation().pathname;

  return null;
}

const onClose = vi.fn();

function show(target: PermitActionTarget | null): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/admin/permits']}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <Where />
          <Routes>
            <Route path="*" element={<PermitLifecycleDialogs target={target} onClose={onClose} />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.clearAllMocks();
  calls = [];
  delete (globalThis as { __where?: string }).__where;
});

const draft = makePermit(7, { number: 'DRAFT-1', status: 'Draft' });
const active = makePermit(5, { number: 'KZ-5' });

describe('PermitLifecycleDialogs: без цілі', () => {
  it('жодного діалогу, жодного запиту', () => {
    stub(() => json({}));
    show(null);

    expect(screen.queryByRole('dialog')).toBeNull();
    expect(calls).toEqual([]);
  });
});

describe('PermitLifecycleDialogs: активація', () => {
  it('діалог називає дозвіл і чинність; підтвердження кличе POST …/activate, тост і закриття', async () => {
    stub(() => json({ id: 7 }));
    show({ item: draft, action: 'activate' });

    const dialog = await screen.findByRole('dialog');
    expect(dialog.textContent).toContain('permits.list.activate.title {"number":"DRAFT-1"}');
    expect(dialog.textContent).toContain('permits.list.activate.reads');

    fireEvent.click(screen.getByRole('button', { name: 'permits.list.activate.verb' }));
    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(calls[0]).toEqual({ method: 'POST', url: '/api/v1/permits/7/activate', body: null });
    expect(notify.showDone).toHaveBeenCalledWith('permits.list.done.activated {"number":"DRAFT-1"}');
  });

  it('відмова сервера (перетин): показано помилку, діалог лишається відкритим', async () => {
    stub(() => conflict());
    show({ item: draft, action: 'activate' });

    fireEvent.click(await screen.findByRole('button', { name: 'permits.list.activate.verb' }));
    await waitFor(() => expect(notify.showApiError).toHaveBeenCalledTimes(1));
    expect(onClose).not.toHaveBeenCalled();
    expect(notify.showDone).not.toHaveBeenCalled();
  });

  it('подвійний клік шле один запит', async () => {
    stub(() => json({ id: 7 }));
    show({ item: draft, action: 'activate' });

    const button = await screen.findByRole('button', { name: 'permits.list.activate.verb' });
    fireEvent.click(button);
    fireEvent.click(button);
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(calls.filter((call) => call.method === 'POST')).toHaveLength(1);
  });
});

describe('PermitLifecycleDialogs: дострокове завершення', () => {
  it('«End permit» вимкнено, доки причина коротша за 10 символів; потім POST …/end з причиною', async () => {
    stub(() => json({ id: 5 }));
    show({ item: active, action: 'end' });

    const reason = await screen.findByRole('textbox', { name: 'permits.list.end.reason' });
    const submit = screen.getByRole('button', { name: 'permits.list.end.verb' });
    fireEvent.change(reason, { target: { value: 'too short' } });
    expect((submit as HTMLButtonElement).disabled).toBe(true);

    fireEvent.change(reason, { target: { value: '  Permit surrendered  ' } });
    expect((submit as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(submit);

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(calls[0]?.url).toBe('/api/v1/permits/5/end');
    expect(JSON.parse(calls[0]?.body ?? '{}')).toEqual({ reason: 'Permit surrendered', endDate: null });
    expect(notify.showDone).toHaveBeenCalledWith('permits.list.done.ended {"number":"KZ-5"}');
  });

  it('відмова сервера: діалог відкритий, помилку показано', async () => {
    stub(() =>
      json({ title: 'Permit state does not allow this', status: 409, errorCode: 'ECR-PRM-4096', correlationId: 'c' }, 409),
    );
    show({ item: active, action: 'end' });

    fireEvent.change(await screen.findByRole('textbox', { name: 'permits.list.end.reason' }), {
      target: { value: 'Permit surrendered' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'permits.list.end.verb' }));

    await waitFor(() => expect(notify.showApiError).toHaveBeenCalledTimes(1));
    expect(onClose).not.toHaveBeenCalled();
  });
});

describe('PermitLifecycleDialogs: поновлення', () => {
  it('POST …/renew і перехід на нову чернетку', async () => {
    stub(() => json({ id: 99 }));
    show({ item: active, action: 'renew' });

    const dialog = await screen.findByRole('dialog');
    expect(dialog.textContent).toContain('permits.list.renew.copy {"from":"');
    fireEvent.click(screen.getByRole('button', { name: 'permits.list.renew.verb' }));

    await waitFor(() => expect((globalThis as { __where?: string }).__where).toBe('/admin/permits/99'));
    expect(calls[0]).toEqual({ method: 'POST', url: '/api/v1/permits/5/renew', body: null });
    expect(notify.showDone).toHaveBeenCalledWith('permits.list.done.renewed');
    expect(onClose).toHaveBeenCalled();
  });
});

describe('PermitLifecycleDialogs: видалення чернетки', () => {
  it('DELETE …/{id}, тост, закриття; кнопка небезпечна дія з назвою чернетки в заголовку', async () => {
    stub(() => new Response(null, { status: 204 }));
    show({ item: draft, action: 'delete' });

    const dialog = await screen.findByRole('dialog');
    expect(dialog.textContent).toContain('permits.list.delete.title {"number":"DRAFT-1"}');
    fireEvent.click(screen.getByRole('button', { name: 'permits.list.delete.verb' }));

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(calls[0]).toEqual({ method: 'DELETE', url: '/api/v1/permits/7', body: null });
    expect(notify.showDone).toHaveBeenCalledWith('permits.list.done.deleted {"number":"DRAFT-1"}');
  });
});

describe('PermitLifecycleDialogs: End early у день старту (PUI-5)', () => {
  it('422 endDateOutOfRange: причина людськими датами в діалозі, тосту немає, діалог відкритий', async () => {
    stub(() =>
      json(
        {
          title: 'Unprocessable',
          status: 422,
          errorCode: 'ECR-PRM-0422',
          correlationId: 'c',
          messageKey: 'err.ECR-PRM-0422.endDateOutOfRange',
          endDate: '2026-10-20',
          from: '2026-10-20',
          to: '2027-10-20',
        },
        422,
      ),
    );
    show({ item: active, action: 'end' });

    fireEvent.change(await screen.findByRole('textbox', { name: 'permits.list.end.reason' }), {
      target: { value: 'Permit surrendered' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'permits.list.end.verb' }));

    const text = await screen.findByText(/err\.ECR-PRM-0422\.endDateOutOfRange/);
    expect(text.textContent).toContain('"endDate":"Oct 20, 2026"');
    expect(text.textContent).not.toMatch(/\d{4}-\d{2}-\d{2}/);
    expect(notify.showApiError).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
    expect(screen.getByRole('dialog')).toBeTruthy();
  });
});

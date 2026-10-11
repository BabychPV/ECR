import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { screen, waitFor, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Notifications } from '@mantine/notifications';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render } from '@testing-library/react';
import { TestProviders } from '@/test/render';
import { AssignmentOwnerActions } from '../AssignmentOwnerActions';
import type { OwnerAssignmentTarget } from '../types';

/**
 * EA-5d: «Валідувати» і «Повернути з коментарем» власника. Каталогу в тестах
 * немає, тож `t()` віддає ключ (можливо, з маркером) - імена кнопок шукаємо
 * за ключами.
 */

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

const filled: OwnerAssignmentTarget = {
  id: 7,
  sliceLabel: 'January',
  validation: 'NotValidated',
  version: 3,
  recipients: [
    { userId: 1, displayName: 'Ivanov', status: 'Filled', isActive: true },
    { userId: 2, displayName: 'Petrenko', status: 'Filled', isActive: true },
  ],
};

const waiting: OwnerAssignmentTarget = {
  ...filled,
  recipients: [filled.recipients[0]!, { userId: 2, displayName: 'Petrenko', status: 'Assigned', isActive: true }],
};

interface Call {
  readonly url: string;
  readonly body: unknown;
}
let calls: Call[] = [];
let reply: () => Response;

beforeEach(() => {
  calls = [];
  reply = () => new Response(null, { status: 204 });
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      calls.push({ url: String(input), body: init?.body === undefined ? null : JSON.parse(String(init.body)) });

      return reply();
    }),
  );
});

afterEach(() => vi.unstubAllGlobals());

function renderActions(target: OwnerAssignmentTarget) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const invalidate = vi.spyOn(client, 'invalidateQueries');
  const view = render(
    <QueryClientProvider client={client}>
      <Notifications />
      <AssignmentOwnerActions documentId={11} target={target} />
    </QueryClientProvider>,
    { wrapper: TestProviders },
  );

  return { ...view, invalidate };
}

const validateButton = () => screen.getByRole('button', { name: /editAssign\.validate\W?$/ });
const returnButton = () => screen.getByRole('button', { name: /editAssign\.return\W?$/ });

describe('Валідувати', () => {
  it('недоступне, поки не всі Filled: клік не відкриває діалог', async () => {
    renderActions(waiting);

    expect(validateButton().getAttribute('aria-disabled')).toBe('true');
    fireEvent.click(validateButton());

    expect(screen.queryByRole('dialog')).toBeNull();
    expect(calls).toHaveLength(0);
  });

  it('підтвердження шле baseVersion, перечитує дані й повертає фокус на кнопку', async () => {
    const user = userEvent.setup();
    const { invalidate } = renderActions(filled);

    await user.click(validateButton());
    const dialog = await screen.findByRole('dialog');
    // L6: фокус на безпечній дії, не на підтвердженні.
    expect(calls).toHaveLength(0);

    const confirm = Array.from(dialog.querySelectorAll('button')).find((b) => /editAssign\.validate\W?$/.test(b.textContent ?? ''));
    await user.click(confirm!);

    await waitFor(() => expect(calls).toHaveLength(1));
    expect(calls[0]).toEqual({ url: '/api/v1/documents/11/edit-assignments/7/validate', body: { baseVersion: 3 } });
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    await waitFor(() => expect(document.activeElement).toBe(validateButton()));
    expect(invalidate).toHaveBeenCalled();
  });

  it('Esc закриває без запиту й повертає фокус на відкривач', async () => {
    const user = userEvent.setup();
    renderActions(filled);

    await user.click(validateButton());
    await screen.findByRole('dialog');
    await user.keyboard('{Escape}');

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    await waitFor(() => expect(document.activeElement).toBe(validateButton()));
    expect(calls).toHaveLength(0);
  });

  it('відмова сервера лишає діалог відкритим і все одно перечитує стан', async () => {
    reply = () =>
      json(
        { title: 'Unprocessable', status: 422, errorCode: 'ECR-REQ-0422', detail: 'Not all executors have filled.' },
        422,
      );
    const user = userEvent.setup();
    const { invalidate } = renderActions(filled);

    await user.click(validateButton());
    const dialog = await screen.findByRole('dialog');
    const confirm = Array.from(dialog.querySelectorAll('button')).find((b) => /editAssign\.validate\W?$/.test(b.textContent ?? ''));
    await user.click(confirm!);

    await waitFor(() => expect(calls).toHaveLength(1));
    await waitFor(() => expect(invalidate).toHaveBeenCalled());
    expect(screen.getByRole('dialog')).toBeTruthy();
  });
});

describe('Повернути з коментарем', () => {
  it('підтвердження вимкнене, доки коментар порожній чи з пробілів', async () => {
    const user = userEvent.setup();
    renderActions(filled);

    await user.click(returnButton());
    const dialog = await screen.findByRole('dialog');
    const confirm = () => Array.from(dialog.querySelectorAll('button')).find((b) => /editAssign\.returnConfirm/.test(b.textContent ?? ''))!;

    expect(confirm().disabled).toBe(true);
    await user.type(screen.getByRole('textbox'), '   ');
    expect(confirm().disabled).toBe(true);
  });

  it('поле не приймає більше 500 символів', async () => {
    const user = userEvent.setup();
    renderActions(filled);

    await user.click(returnButton());
    await screen.findByRole('dialog');
    const field = screen.getByRole('textbox');

    expect(field.getAttribute('maxlength')).toBe('500');
  });

  it('шле обрізаний коментар і baseVersion, закриває діалог, повертає фокус', async () => {
    const user = userEvent.setup();
    const { invalidate } = renderActions(filled);

    await user.click(returnButton());
    const dialog = await screen.findByRole('dialog');
    await user.type(screen.getByRole('textbox'), '  Check row 4  ');
    await user.click(Array.from(dialog.querySelectorAll('button')).find((b) => /editAssign\.returnConfirm/.test(b.textContent ?? ''))!);

    await waitFor(() => expect(calls).toHaveLength(1));
    expect(calls[0]).toEqual({
      url: '/api/v1/documents/11/edit-assignments/7/return',
      body: { reason: 'Check row 4', baseVersion: 3 },
    });
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    await waitFor(() => expect(document.activeElement).toBe(returnButton()));
    expect(invalidate).toHaveBeenCalled();
  });

  it('при повторному відкритті поле порожнє (коментар не переходить між діями)', async () => {
    const user = userEvent.setup();
    renderActions(filled);

    await user.click(returnButton());
    await screen.findByRole('dialog');
    await user.type(screen.getByRole('textbox'), 'old');
    await user.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());

    await user.click(returnButton());
    await screen.findByRole('dialog');

    expect((screen.getByRole('textbox') as HTMLTextAreaElement).value).toBe('');
  });

  it('недоступне, коли ніхто не Filled', async () => {
    renderActions({ ...filled, recipients: [{ userId: 1, displayName: 'Ivanov', status: 'Assigned', isActive: true }] });

    expect(returnButton().getAttribute('aria-disabled')).toBe('true');
  });
});

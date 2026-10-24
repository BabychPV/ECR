import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { EcrApiError } from '@/api/client';
import { testTheme } from '@/test/render';
import * as permitsApi from '../../api/permitsApi';
import { PermitLifecycleDialogs } from '../../list/PermitLifecycleDialogs';
import { makePermit } from '../../list/__tests__/permitFixture';
import { LifecycleDialogs } from '../LifecycleDialogs';
import type { Permit } from '../../api/types';
import { permit } from './permitFixtures';

/**
 * Беклог P3 пермітів, життєвий цикл: PUI-5 (End early у день старту - причина людськими датами В ДІАЛОЗІ, діалог не
 * закривається) і PUI-10 (ru: «по 10 янв. 2027 г..» - дві крапки після дати наприкінці речення).
 *
 * Мутаційний доказ (вручну, відновлено): `onError: fail` замість розбору `endDateOutOfRangeText` у `LifecycleDialogs` валить
 * «закривається» і «людські дати»; `to: sentenceDate(to)` -> `to` валить обидва тести «ru: …без подвійної крапки».
 */
const lang = vi.hoisted(() => ({ value: 'en' }));
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    params === undefined ? key : `${key} ${JSON.stringify(params)}`,
  language: () => lang.value,
}));
vi.mock('@/features/permits/api/permitsApi', () => ({
  getPermit: vi.fn(),
  updatePermit: vi.fn(),
  activatePermit: vi.fn(),
  endPermit: vi.fn(),
  renewPermit: vi.fn(),
  deletePermit: vi.fn(),
}));

const endPermit = vi.mocked(permitsApi.endPermit);

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
  lang.value = 'en';
});

function wrap(children: React.ReactNode): React.ReactNode {
  return (
    <MantineProvider theme={testTheme}>
      <MemoryRouter>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>{children}</QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>
  );
}

function outOfRange(): EcrApiError {
  return new EcrApiError({
    title: 'Unprocessable',
    status: 422,
    errorCode: 'ECR-PRM-0422',
    correlationId: 'c',
    detail: 'The end date 2026-10-20 must be after the start 2026-10-20 and not later than the current end 2027-10-20.',
    extensions2: { messageKey: 'err.ECR-PRM-0422.endDateOutOfRange', endDate: '2026-10-20', from: '2026-10-20', to: '2027-10-20' },
  });
}

describe('PUI-5: End early у день старту (екран дозволу)', () => {
  it('422 endDateOutOfRange: діалог НЕ закривається, причина в діалозі людськими датами (не ISO)', async () => {
    endPermit.mockRejectedValue(outOfRange());
    const onClose = vi.fn();
    render(wrap(<LifecycleDialogs permit={permit({ status: 'Active' })} dialog="end" onClose={onClose} />));

    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByRole('textbox', { name: /permits\.page\.end\.reason/ }), {
      target: { value: 'Operations stopped for good' },
    });
    fireEvent.click(within(dialog).getByRole('button', { name: 'permits.page.end.verb' }));

    await waitFor(() => expect(endPermit).toHaveBeenCalledTimes(1));
    const text = await within(dialog).findByText(/err\.ECR-PRM-0422\.endDateOutOfRange/);
    expect(onClose).not.toHaveBeenCalled();
    expect(screen.getByRole('dialog')).toBeTruthy();
    // Людські дати за локаллю, а не сирі ISO-дати сервера.
    expect(text.textContent).toContain('"endDate":"Oct 20, 2026"');
    expect(text.textContent).toContain('"to":"Oct 20, 2027"');
    expect(text.textContent).not.toMatch(/\d{4}-\d{2}-\d{2}/);
    // Поле причини позначено помилковим і зв'язано з текстом; причина не загубилась.
    const reason = within(dialog).getByRole('textbox', { name: /permits\.page\.end\.reason/ });
    expect(reason.getAttribute('aria-invalid')).toBe('true');
    expect((reason as HTMLTextAreaElement).value).toBe('Operations stopped for good');
  });

  it('правка причини знімає застарілу помилку', async () => {
    endPermit.mockRejectedValue(outOfRange());
    render(wrap(<LifecycleDialogs permit={permit({ status: 'Active' })} dialog="end" onClose={vi.fn()} />));

    const dialog = await screen.findByRole('dialog');
    const reason = within(dialog).getByRole('textbox', { name: /permits\.page\.end\.reason/ });
    fireEvent.change(reason, { target: { value: 'Operations stopped for good' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'permits.page.end.verb' }));
    await within(dialog).findByText(/endDateOutOfRange/);

    fireEvent.change(reason, { target: { value: 'Operations stopped for good!' } });
    await waitFor(() => expect(within(dialog).queryByText(/endDateOutOfRange/)).toBeNull());
  });

  it('інша відмова (стан не той) діалог закриває, як і раніше', async () => {
    endPermit.mockRejectedValue(
      new EcrApiError({ title: 'Conflict', status: 409, errorCode: 'ECR-PRM-4096', correlationId: 'c' }),
    );
    const onClose = vi.fn();
    render(wrap(<LifecycleDialogs permit={permit({ status: 'Active' })} dialog="end" onClose={onClose} />));

    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByRole('textbox', { name: /permits\.page\.end\.reason/ }), {
      target: { value: 'Operations stopped for good' },
    });
    fireEvent.click(within(dialog).getByRole('button', { name: 'permits.page.end.verb' }));

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
  });
});

describe('P3-2: подвійний клік «Підтвердити» в End early (екран дозволу)', () => {
  it('другий клік до завершення запиту не шле мутацію вдруге', async () => {
    // Мутаційний доказ (вручну, відновлено): без `if (end.isPending) return` endPermit викликається двічі.
    let finish: (value: Permit) => void = () => undefined;
    endPermit.mockImplementation(() => new Promise<Permit>((resolve) => { finish = resolve; }));
    render(wrap(<LifecycleDialogs permit={permit({ status: 'Active' })} dialog="end" onClose={vi.fn()} />));

    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByRole('textbox', { name: /permits\.page\.end\.reason/ }), {
      target: { value: 'Operations stopped for good' },
    });
    const confirm = within(dialog).getByRole('button', { name: 'permits.page.end.verb' });
    fireEvent.click(confirm);
    await waitFor(() => expect(endPermit).toHaveBeenCalledTimes(1));
    fireEvent.click(confirm);
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(endPermit).toHaveBeenCalledTimes(1);
    finish(permit({ status: 'Ended' }));
  });
});

describe('PUI-10: ru, дата наприкінці речення в діалозі Activate', () => {
  it('екран дозволу: «по {to}.» без подвійної крапки - дата без «г.», початок як є', async () => {
    lang.value = 'ru';
    render(wrap(<LifecycleDialogs permit={permit({ validFrom: '2026-01-10', validTo: '2027-01-10' })} dialog="activate" onClose={vi.fn()} />));

    const dialog = await screen.findByRole('dialog');
    const reads = /permits\.page\.activate\.reads (\{.*\})/.exec(dialog.textContent ?? '');
    const params = JSON.parse(reads?.[1] ?? '{}') as { from: string; to: string };
    expect(params.from).toMatch(/2026 г\.$/);
    expect(params.to).toMatch(/2027 г$/);
  });

  it('перелік дозволів: те саме', async () => {
    lang.value = 'ru';
    render(
      wrap(
        <PermitLifecycleDialogs
          target={{ item: makePermit(7, { number: 'DRAFT-1', status: 'Draft', validFrom: '2026-01-10', validTo: '2027-01-10' }), action: 'activate' }}
          onClose={vi.fn()}
        />,
      ),
    );

    const dialog = await screen.findByRole('dialog');
    const reads = /permits\.list\.activate\.reads (\{.*\})/.exec(dialog.textContent ?? '');
    const params = JSON.parse(reads?.[1] ?? '{}') as { from: string; to: string };
    expect(params.from).toMatch(/2026 г\.$/);
    expect(params.to).toMatch(/2027 г$/);
  });
});

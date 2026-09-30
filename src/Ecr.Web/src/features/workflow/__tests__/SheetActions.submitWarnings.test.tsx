import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { EcrApiError } from '@/api/client';
import { SheetActions, warningsToConfirm } from '../SheetActions';

/**
 * ФВ-5.19: «Warning — з підтвердженням». Сервер відмовляє в поданні аркуша з
 * попередженнями валідації без `acknowledgeWarnings` — `422 ECR-SUB-4221`,
 * `messageKey = err.ECR-SUB-4221.warningsNeedConfirmation`, перелік у `messages`.
 * Клієнт показує діалог із переліком і, лише після підтвердження, повторює
 * подання з `acknowledgeWarnings = true`.
 */

const WarningKey = 'err.ECR-SUB-4221.warningsNeedConfirmation';
const WarningText = 'Volume is over the cap';

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

/** Тіла POST …/submit у порядку надходження. */
let submitBodies: Record<string, unknown>[] = [];

/**
 * Сервер просить підтвердження, доки в тілі немає `acknowledgeWarnings = true`.
 *
 * @param otherFailure Інша відмова `ECR-SUB-4221` (без ключа підтвердження).
 */
function mockFetch(otherFailure = false): void {
  submitBodies = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) return json(CurrentUser);

      if (url.includes('/api/v1/documents/1/submit') && init?.method === 'POST') {
        const body = JSON.parse(String(init.body)) as Record<string, unknown>;
        submitBodies.push(body);

        if (body['acknowledgeWarnings'] === true && !otherFailure) {
          return new Response(null, { status: 204 });
        }

        return json(
          {
            type: 'https://ecr.ncoc.kz/errors/ECR-SUB-4221',
            title: 'Submission is blocked',
            status: 422,
            detail: 'The sheet has 1 validation warning(s). Review them and confirm to submit anyway.',
            instance: '/api/v1/documents/1/submit',
            errorCode: 'ECR-SUB-4221',
            correlationId: 'corr-warn-1',
            messageKey: otherFailure ? 'err.ECR-SUB-4221.validationBlocked' : WarningKey,
            messageCount: '1',
            messages: [
              { ruleCode: 'CAP', message: WarningText, rowKey: '7001001', columnCode: 'Volume' },
            ],
          },
          422,
          'application/problem+json',
        );
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

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('SheetActions: подання з попередженнями (ФВ-5.19)', () => {
  it('перше подання йде без підтвердження і показує діалог із переліком попереджень', async () => {
    const spy = vi.spyOn(notifications, 'show');
    mockFetch();
    show();

    fireEvent.click(await screen.findByRole('button', { name: '⟦document.submit⟧' }));

    expect(await screen.findByText(WarningText)).toBeTruthy();
    expect(submitBodies).toHaveLength(1);
    expect(submitBodies[0]).toMatchObject({ sheetDefId: 42, periodKey: 202601, acknowledgeWarnings: false });

    // Це питання, а не помилка: тосту відмови немає.
    expect(spy).not.toHaveBeenCalled();
  });

  it('«Submit anyway» повторює подання з acknowledgeWarnings = true і закриває діалог', async () => {
    const spy = vi.spyOn(notifications, 'show');
    mockFetch();
    show();

    fireEvent.click(await screen.findByRole('button', { name: '⟦document.submit⟧' }));
    await screen.findByText(WarningText);

    fireEvent.click(await screen.findByRole('button', { name: '⟦workflow.submitAnyway⟧' }));

    await waitFor(() => {
      expect(submitBodies).toHaveLength(2);
    });
    expect(submitBodies[1]).toMatchObject({ acknowledgeWarnings: true });

    await waitFor(() => {
      expect(screen.queryByText(WarningText)).toBeNull();
    });
    expect(spy).toHaveBeenCalled();
  });

  it('«Cancel» закриває діалог і нічого не подає', async () => {
    mockFetch();
    show();

    fireEvent.click(await screen.findByRole('button', { name: '⟦document.submit⟧' }));
    await screen.findByText(WarningText);

    fireEvent.click(await screen.findByRole('button', { name: '⟦common.cancel⟧' }));

    await waitFor(() => {
      expect(screen.queryByText(WarningText)).toBeNull();
    });
    expect(submitBodies).toHaveLength(1);
  });

  it('інша відмова ECR-SUB-4221 діалогу не відкриває — це тост', async () => {
    const spy = vi.spyOn(notifications, 'show');
    mockFetch(true);
    show();

    fireEvent.click(await screen.findByRole('button', { name: '⟦document.submit⟧' }));

    await waitFor(() => {
      expect(spy).toHaveBeenCalled();
    });
    expect(screen.queryByRole('button', { name: '⟦workflow.submitAnyway⟧' })).toBeNull();
  });
});

describe('warningsToConfirm', () => {
  const problem = (over: Record<string, unknown>): EcrApiError =>
    new EcrApiError({
      title: 't',
      status: 422,
      errorCode: 'ECR-SUB-4221',
      correlationId: 'c',
      extensions2: { messageKey: WarningKey, messages: [{ message: 'A' }, { message: 'B' }] },
      ...over,
    });

  it('віддає тексти попереджень', () => {
    expect(warningsToConfirm(problem({}))).toEqual(['A', 'B']);
  });

  it('інший код або ключ — null', () => {
    expect(warningsToConfirm(problem({ errorCode: 'ECR-DOC-0422' }))).toBeNull();
    expect(
      warningsToConfirm(problem({ extensions2: { messageKey: 'err.ECR-SUB-4221.validationBlocked' } })),
    ).toBeNull();
    expect(warningsToConfirm(new Error('x'))).toBeNull();
  });
});

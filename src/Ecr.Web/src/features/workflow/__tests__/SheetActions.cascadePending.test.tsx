import { confirmSubmit } from '@/test/confirmSubmit';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SheetActions } from '../SheetActions';
import { readSeedEnCatalog } from '@/test/seedSql';

/**
 * F5 (рішення людини «Заборонити, поки йде перерахунок»): сервер відмовляє в поданні й затвердженні місяця, що
 * чекає каскаду від зміни джерела HSE400, — `422 ECR-SUB-4221` з `messageKey = err.ECR-SUB-4221.sourceCascadePending`.
 *
 * ⛔ Кнопки заздалегідь не вимикаються: стан «чекає каскаду» живе секунди-хвилину, і жоден наявний запит сторінки його
 * не несе (опитувати чергу заради кнопки — новий запит і зростання бандла `DocumentPage`). Доводимо, що відмова доходить
 * до тосту КОНКРЕТНИМ текстом ключа з каталогу (той самий шлях, що `SheetActions.submitStale.test.tsx`: текст за
 * ключем резолвить сервер, мок моделює його по справжньому сіду).
 */

const CascadeKey = 'err.ECR-SUB-4221.sourceCascadePending';

const GenericTitle = 'Submission is blocked';

/** Текст `en` за ключем у порціях `#UiEn`; `null` — ключа немає. */
function catalogText(key: string): string | null {
  const escaped = key.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const row = new RegExp(`\\(N'${escaped}',\\s*N'en',\\s*N'((?:[^']|'')*)'`).exec(readSeedEnCatalog());

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

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) return json(CurrentUser);

      if (url.includes('/api/v1/documents/1/submit') && init?.method === 'POST') {
        return json(
          {
            type: 'https://ecr.ncoc.kz/errors/ECR-SUB-4221',
            title: GenericTitle,
            status: 422,
            detail: catalogText(CascadeKey),
            instance: '/api/v1/documents/1/submit',
            errorCode: 'ECR-SUB-4221',
            correlationId: 'corr-f5-1',
            messageKey: CascadeKey,
            periodKey: '202601',
          },
          422,
          'application/problem+json',
        );
      }

      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('SheetActions: відмова Submit, поки місяць чекає каскаду джерела (F5)', () => {
  it('ключ є в каталозі й каже: йде перерахунок після зміни джерела, спробуйте за хвилину', () => {
    const text = catalogText(CascadeKey);

    expect(text, `seed/features: немає ключа ${CascadeKey} (en)`).not.toBeNull();
    expect(text).toContain('HSE400');
    expect(text?.toLowerCase()).toContain('recalculation');
    expect(text?.toLowerCase()).toContain('minute');
  });

  it('422 з messageKey sourceCascadePending — тост несе текст ключа, а не загальну назву', async () => {
    const spy = vi.spyOn(notifications, 'show');
    mockFetch();

    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    client.setQueryData(['document', 1, 202601], Summary);
    render(
      <MantineProvider>
        <QueryClientProvider client={client}>
          <SheetActions documentId={1} sheetDefId={42} periodKey={202601} state="Draft" />
        </QueryClientProvider>
      </MantineProvider>,
    );

    fireEvent.click(await screen.findByRole('button', { name: /submit/i }));
    await confirmSubmit();

    const expected = catalogText(CascadeKey);
    await waitFor(() => {
      expect(spy.mock.calls.map((call) => String((call[0] as { message: unknown }).message))).toContain(expected);
    });
    expect(spy.mock.calls.map((call) => String((call[0] as { message: unknown }).message)).join('\n')).not.toContain(
      GenericTitle,
    );
  });
});

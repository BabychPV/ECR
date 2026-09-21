import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { MethodologyDraftVersionDto } from '@/api/types';
import { useDeleteVersionAction } from '@/features/methodologies/DeleteVersionAction';
import { mayDeleteVersion } from '@/features/methodologies/versionDeletion';
import { showDone } from '@/shared/ui/notify';
import { testTheme } from '@/test/render';

/**
 * «Видалити чернетку» версії методології (`BE-25`).
 *
 * ⛔ Сервер — `fetch`-стаб із `application/problem+json`, а не підмінений
 * `deleteMethodologyVersion`: відмова проходить той самий розбір
 * (`apiFetch` → `EcrApiError` → `messageKey`), що й у продукті.
 */
vi.mock('@/shared/ui/notify', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/ui/notify')>()),
  showDone: vi.fn(),
}));

function version(id: number, status: 'Draft' | 'Published' | 'Deprecated', number: string): MethodologyDraftVersionDto {
  return {
    calendarMode: 'Actual',
    createdByUserId: 1,
    effectiveFrom: status === 'Draft' ? null : '2026-01-01',
    id,
    isEditable: status === 'Draft',
    level: 'Configuration',
    numericMode: 'Strict',
    status,
    traceLevel: 'ErrorsOnly',
    versionNumber: number,
  };
}

const Versions = [
  version(1, 'Deprecated', '2025.1'),
  version(2, 'Published', '2026.1'),
  version(3, 'Draft', '2026.2'),
];

const sent: { url: string; method: string }[] = [];

function mockServer(status: number, body?: unknown): void {
  sent.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init?: RequestInit) => {
      sent.push({ url: String(url), method: String(init?.method ?? 'GET') });

      return body === undefined
        ? new Response(null, { status })
        : new Response(JSON.stringify(body), {
            status,
            headers: { 'Content-Type': 'application/problem+json' },
          });
    }),
  );
}

function Harness({ allowed }: { allowed: boolean }): JSX.Element {
  const deletion = useDeleteVersionAction({ methodologyId: 5, allowed });

  return (
    <div>
      <h1>versions</h1>
      {deletion.refusal}
      <ul>
        {Versions.map((v) => (
          <li key={v.id} data-testid={`row-${v.versionNumber}`}>
            {v.versionNumber}
            {deletion.triggerFor(v)}
          </li>
        ))}
      </ul>
      {deletion.dialog}
    </div>
  );
}

function show(allowed = true): QueryClient {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <Harness allowed={allowed} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return client;
}

const DeleteButton = { name: '⟦methodologies.deleteVersion⟧' };

async function confirmDeletion(): Promise<void> {
  fireEvent.click(screen.getByRole('button', DeleteButton));
  fireEvent.click(await screen.findByTestId('confirm-verb'));
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.mocked(showDone).mockClear();
});

describe('mayDeleteVersion', () => {
  it('лише Draft і лише з правом', () => {
    expect(mayDeleteVersion(version(3, 'Draft', 'x'), true)).toBe(true);
    expect(mayDeleteVersion(version(3, 'Draft', 'x'), false)).toBe(false);
    expect(mayDeleteVersion(version(2, 'Published', 'x'), true)).toBe(false);
    expect(mayDeleteVersion(version(1, 'Deprecated', 'x'), true)).toBe(false);
  });
});

describe('useDeleteVersionAction: показ кнопки', () => {
  it('кнопка є рівно в рядку чернетки', () => {
    show();

    expect(screen.getAllByRole('button', DeleteButton)).toHaveLength(1);
    expect(screen.getByTestId('row-2026.2').textContent).toContain('⟦methodologies.deleteVersion⟧');
    expect(screen.getByTestId('row-2026.1').textContent).not.toContain('deleteVersion');
    expect(screen.getByTestId('row-2025.1').textContent).not.toContain('deleteVersion');
  });

  it('без права Calculation.EditFormula кнопки НЕМАЄ', () => {
    show(false);

    // Спершу — що рядки намальовані, інакше «кнопки немає» було б правдою з іншої причини.
    expect(screen.getByTestId('row-2026.2')).toBeDefined();
    expect(screen.queryByRole('button', DeleteButton)).toBeNull();
  });
});

describe('useDeleteVersionAction: підтвердження', () => {
  it('названо версію, фокус на Cancel, кнопка небезпечна, відкриття нічого не шле', async () => {
    mockServer(204);
    show();

    fireEvent.click(screen.getByRole('button', DeleteButton));

    const dialog = await screen.findByRole('dialog');
    expect(dialog.textContent).toContain('version=2026.2');

    await waitFor(() => expect(document.activeElement).toBe(screen.getByTestId('confirm-cancel')));
    expect(screen.getByTestId('confirm-verb').getAttribute('style') ?? '').toContain('statusError');
    expect(sent).toEqual([]);
  });
});

describe('useDeleteVersionAction: успіх', () => {
  it('DELETE на адресу версії, інвалідизація переліку, сповіщення', async () => {
    mockServer(204);
    const client = show();
    const invalidate = vi.spyOn(client, 'invalidateQueries');

    await confirmDeletion();

    await waitFor(() => expect(showDone).toHaveBeenCalledWith('⟦methodologies.versionDeleted (version=2026.2)⟧'));
    expect(sent).toEqual([{ url: '/api/v1/methodologies/5/versions/3', method: 'DELETE' }]);
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['methodologies', 'versionsOf', 5] });
    expect(screen.queryByRole('alert')).toBeNull();
  });
});

describe('useDeleteVersionAction: відмова 409 — причина за messageKey', () => {
  it('versionUsedInCalculations — «вже рахували», а не загальна відмова', async () => {
    mockServer(409, {
      title: 'err.ECR-CALC-0409',
      status: 409,
      errorCode: 'ECR-CALC-0409',
      correlationId: 'cid-used',
      detail: 'Methodology version 2026.2 has already been used in calculations and cannot be deleted.',
      messageKey: 'err.ECR-CALC-0409.versionUsedInCalculations',
      reason: 'UsedInCalculations',
      version: '2026.2',
    });
    show();

    await confirmDeletion();

    const alert = await screen.findByRole('alert');
    expect(alert.getAttribute('data-refusal')).toBe('UsedInCalculations');
    expect(alert.textContent).toContain('⟦err.ECR-CALC-0409.versionUsedInCalculations (version=2026.2)⟧');
    expect(alert.textContent).toContain('ECR-CALC-0409');
    expect(alert.textContent).toContain('cid-used');

    // ⛔ Не заголовок коду («потрібна друга пара очей») — це інша відмова.
    expect(alert.textContent).not.toContain('⟦err.ECR-CALC-0409⟧');
    expect(showDone).not.toHaveBeenCalled();
  });

  it('versionNotDraft — «не чернетка» зі станом версії', async () => {
    mockServer(409, {
      title: 'err.ECR-CALC-0409',
      status: 409,
      errorCode: 'ECR-CALC-0409',
      correlationId: 'cid-draft',
      detail: 'Only a draft methodology version can be deleted; version 2026.2 is Published.',
      messageKey: 'err.ECR-CALC-0409.versionNotDraft',
      reason: 'Published',
      version: '2026.2',
    });
    show();

    await confirmDeletion();

    const alert = await screen.findByRole('alert');
    expect(alert.getAttribute('data-refusal')).toBe('NotDraft');
    expect(alert.textContent).toContain(
      '⟦err.ECR-CALC-0409.versionNotDraft (version=2026.2, reason=Published)⟧',
    );
  });

  it('не 409 — загальний банер із текстом сервера, без вигаданої причини', async () => {
    mockServer(404, {
      title: 'err.ECR-CALC-0404',
      status: 404,
      errorCode: 'ECR-CALC-0404',
      correlationId: 'cid-404',
      detail: 'Methodology version 3 does not exist in this methodology.',
      messageKey: 'err.ECR-CALC-0404.version',
      methodologyVersionId: '3',
    });
    show();

    await confirmDeletion();

    const alert = await screen.findByRole('alert');
    expect(alert.getAttribute('data-refusal')).toBeNull();
    expect(alert.textContent).toContain('Methodology version 3 does not exist in this methodology.');
  });
});

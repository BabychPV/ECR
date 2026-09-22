import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { MappedFieldPreview } from '@/api/types';
import { UnitChangeAction } from '@/features/mapping/UnitChangeAction';

/**
 * Банер «джерело змінило одиницю» (`ФВ-16.9`).
 *
 * ⛔ Перевіряються АДРЕСА, МЕТОД і ТІЛО кліку «Так, прийняти» — той самий клас
 * доказу, що й `lifecycle.test.ts`/`PauseResumeAction.test.tsx`: клік не на
 * той маршрут жоден серверний тест не ловить.
 *
 * ⛔ Мутаційний доказ, назва задачі — три речі, які тест ловить, а зелений
 * рендер сам собою НІ:
 *   1. клік «Так» без виклику `accept-unit-change`;
 *   2. клік «Ні» з викликом сервера (не мав би дзвонити взагалі);
 *   3. відсутнє посилання на `/admin/units` при відмові 422.
 */

const pendingField: MappedFieldPreview = {
  fieldMapId: 9,
  sourceField: 'Flare_02_Flow',
  outcome: 'Materialized',
  targetRowKey: 'Flare_02',
  targetColumnDefId: 200,
  targetColumnCode: 'FLOW',
  aggregation: 'Sum',
  sourceUnitCode: 'kg',
  targetUnitCode: 't',
  pointCount: 3,
  foldedValue: null,
  isActive: false,
  pendingSourceUnitChange: {
    actualUnitCode: 'm3',
    actualUnitId: 5,
    detectedAt: '2026-09-20T10:15:30Z',
  },
};

/** Записані виклики `fetch`, у порядку надсилання. */
const sent: { url: string; method: string; body: unknown }[] = [];

function problemResponse(status: number, errorCode: string, messageKey: string): Response {
  return new Response(
    JSON.stringify({
      title: 'refused',
      status,
      errorCode,
      correlationId: 'cid-unit-1',
      messageKey,
      detail: 'сервер відмовив',
    }),
    { status, headers: { 'Content-Type': 'application/problem+json' } },
  );
}

function mockServer(response?: () => Response): void {
  sent.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init?: RequestInit) => {
      sent.push({
        url: String(url),
        method: String(init?.method ?? 'GET'),
        body: init?.body === undefined ? null : JSON.parse(String(init.body)),
      });

      if (response !== undefined) return response();

      return new Response(JSON.stringify({ id: 9, isActive: true, pendingSourceUnitChange: null }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

function show(
  field: MappedFieldPreview,
  allowed: boolean,
  onDismiss: () => void,
): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <MemoryRouter>
        <QueryClientProvider client={client}>
          <UnitChangeAction field={field} allowed={allowed} onDismiss={onDismiss} />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  cleanup();
});

describe('UnitChangeAction: банер зміни одиниці джерела', () => {
  it('показує нову одиницю, дотеперішню і час виявлення (з каталожних ключів)', () => {
    mockServer();
    show(pendingField, true, vi.fn());

    expect(
      screen.getByText('⟦mapping.unitChangeBanner (actualUnitCode=m3, expectedUnitCode=t)⟧'),
    ).toBeDefined();
    expect(screen.getByText('⟦mapping.unitChangeDetected⟧')).toBeDefined();
    expect(
      screen.getByText('⟦mapping.unitChangeAccept (actualUnitCode=m3)⟧'),
    ).toBeDefined();
    expect(screen.getByText('⟦mapping.unitChangeDecline⟧')).toBeDefined();
  });

  it('«pendingSourceUnitChange: null» — компонент нічого не малює', () => {
    mockServer();
    render(
      <MantineProvider>
        <MemoryRouter>
          <QueryClientProvider client={new QueryClient()}>
            <UnitChangeAction
              field={{ ...pendingField, pendingSourceUnitChange: null }}
              allowed
              onDismiss={vi.fn()}
            />
          </QueryClientProvider>
        </MemoryRouter>
      </MantineProvider>,
    );

    // ⚠ Не `container.textContent === ''`: `MantineProvider` вставляє власні
    // `<style>` responsive-класів усередину контейнера, тож текст контейнера
    // порожнім не буває навіть без жодного вузла від самого компонента.
    // `role="alert"` — єдиний елемент, який малює `UnitChangeAction`.
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('без права Integration.Manage кнопок немає взагалі, банер лишається', () => {
    mockServer();
    show(pendingField, false, vi.fn());

    expect(screen.queryByText('⟦mapping.unitChangeAccept (actualUnitCode=m3)⟧')).toBeNull();
    expect(screen.queryByText('⟦mapping.unitChangeDecline⟧')).toBeNull();
    expect(
      screen.getByText('⟦mapping.unitChangeBanner (actualUnitCode=m3, expectedUnitCode=t)⟧'),
    ).toBeDefined();
  });

  it('«Так, прийняти» шле POST на accept-unit-change з ПОРОЖНІМ тілом і кличе onDismiss', async () => {
    mockServer();
    const onDismiss = vi.fn();
    show(pendingField, true, onDismiss);

    fireEvent.click(screen.getByText('⟦mapping.unitChangeAccept (actualUnitCode=m3)⟧'));

    await waitFor(() =>
      expect(sent).toEqual([
        { url: '/api/v1/entity-field-maps/9/accept-unit-change', method: 'POST', body: {} },
      ]),
    );
    await waitFor(() => expect(onDismiss).toHaveBeenCalledTimes(1));
  });

  it('«Ні, це помилка джерела» НЕ ходить на сервер і кличе onDismiss одразу', () => {
    mockServer();
    const onDismiss = vi.fn();
    show(pendingField, true, onDismiss);

    fireEvent.click(screen.getByText('⟦mapping.unitChangeDecline⟧'));

    expect(sent).toEqual([]);
    expect(onDismiss).toHaveBeenCalledTimes(1);
  });

  it('відмова 422 (одиниці немає в довіднику) — лишає кнопки й додає посилання на /admin/units', async () => {
    mockServer(() => problemResponse(422, 'ECR-INT-0422', 'err.ECR-INT-0422.pendingUnitNotInCatalog'));
    const onDismiss = vi.fn();
    show(pendingField, true, onDismiss);

    fireEvent.click(screen.getByText('⟦mapping.unitChangeAccept (actualUnitCode=m3)⟧'));

    // ⚠ Не `getByRole('alert')`: банер САМ уже має цю роль, і `ErrorAlert`
    // усередині дав би ДРУГИЙ елемент з тим самим `role` — код відмови
    // однозначний і без цієї двозначності.
    await waitFor(() => expect(screen.getByText('ECR-INT-0422')).toBeDefined());

    const link = await screen.findByText('⟦mapping.unitChangeGoToUnits⟧');
    expect(link.closest('a')?.getAttribute('href')).toBe('/admin/units');

    // ⛔ Кнопки лишаються: людина могла завести одиницю в іншій вкладці.
    expect(screen.getByText('⟦mapping.unitChangeAccept (actualUnitCode=m3)⟧')).toBeDefined();
    expect(screen.getByText('⟦mapping.unitChangeDecline⟧')).toBeDefined();
    expect(onDismiss).not.toHaveBeenCalled();
  });

  it('відмова 409 (позначки вже немає) — банер закривається сам, без посилання на units', async () => {
    mockServer(() =>
      problemResponse(409, 'ECR-INT-0409', 'err.ECR-INT-0409.mappingUnitChangeNotPending'),
    );
    const onDismiss = vi.fn();
    show(pendingField, true, onDismiss);

    fireEvent.click(screen.getByText('⟦mapping.unitChangeAccept (actualUnitCode=m3)⟧'));

    await waitFor(() => expect(onDismiss).toHaveBeenCalledTimes(1));
    expect(screen.queryByText('⟦mapping.unitChangeGoToUnits⟧')).toBeNull();
  });

  it('інша відмова (не 422, не 409) лишає банер відкритим, без посилання на units', async () => {
    mockServer(() => problemResponse(500, 'ECR-SYS-0500', 'err.ECR-SYS-0500.unexpected'));
    const onDismiss = vi.fn();
    show(pendingField, true, onDismiss);

    fireEvent.click(screen.getByText('⟦mapping.unitChangeAccept (actualUnitCode=m3)⟧'));

    await waitFor(() => expect(screen.getByText('ECR-SYS-0500')).toBeDefined());
    expect(screen.queryByText('⟦mapping.unitChangeGoToUnits⟧')).toBeNull();
    expect(onDismiss).not.toHaveBeenCalled();
  });
});

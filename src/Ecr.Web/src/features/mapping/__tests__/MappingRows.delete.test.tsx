import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { MappedFieldPreview, MappingPreview } from '@/api/types';
import { MappingRows } from '@/features/mapping/MappingRows';
import { DeleteMappingAction } from '@/features/mapping/DeleteMappingAction';

/**
 * Видалення мапінгу з рядка перегляду (`BE-27`, макет `remove-mapping`).
 *
 * ⛔ Предмет перевірки — не «кнопка є», а три речі, кожна з яких раніше
 * коштувала б даних: право доступу ПЕРЕД кнопкою, підтвердження ПЕРЕД
 * запитом і фокус на безпечній дії в самому підтвердженні.
 */
const Field: MappedFieldPreview = {
  fieldMapId: 1,
  sourceField: 'Flare_01_CO',
  outcome: 'Materialized',
  targetRowKey: 'Flare_01',
  targetColumnDefId: 100,
  targetColumnCode: 'CO_MASS',
  aggregation: 'Sum',
  sourceUnitCode: 'kg',
  targetUnitCode: 't',
  pointCount: 2,
  foldedValue: '42.5',
  isActive: true,
  // ⚠ Поле обов'язкове з ФВ-16.9: `null` — мапінг рішення про одиницю не
  // чекає, тобто рядок поводиться так само, як до появи цієї ознаки.
  pendingSourceUnitChange: null,
};

const Preview: MappingPreview = {
  sourceEntityId: 1,
  code: 'FLARE_01',
  displayName: 'Факел 01',
  fromUtc: '2026-09-01T00:00:00Z',
  toUtc: '2026-09-08T00:00:00Z',
  pointsSeen: 3,
  isTruncated: false,
  fields: [Field, { ...Field, fieldMapId: 2, sourceField: 'Flare_01_NOx', isActive: false }],
  rows: [],
  unmappedSourceFields: [],
  uncoveredColumns: [],
};

function wrap(node: React.ReactNode): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>{node}</QueryClientProvider>
    </MantineProvider>,
  );
}

/** Відповідь `204` на `DELETE`, з протоколюванням адреси й методу. */
function stubDelete(): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn(async () => new Response(null, { status: 204 }));

  vi.stubGlobal('fetch', fetchMock);

  return fetchMock;
}

afterEach(() => {
  vi.unstubAllGlobals();
  cleanup();
});

describe('MappingRows: право доступу перед кнопкою видалення', () => {
  it('без права Integration.Manage кнопки видалення немає — ні на діючому рядку, ні на призупиненому', () => {
    wrap(<MappingRows preview={Preview} allowed={false} />);

    expect(screen.queryByText('⟦mapping.delete⟧')).toBeNull();
  });

  it('за замовчуванням (пропс не заданий) кнопки видалення теж немає', () => {
    wrap(<MappingRows preview={Preview} />);

    expect(screen.queryByText('⟦mapping.delete⟧')).toBeNull();
  });

  it('з правом — кнопка стоїть у КОЖНОМУ рядку, і діючому, і призупиненому', () => {
    wrap(<MappingRows preview={Preview} allowed />);

    const activeRow = screen.getByText('Flare_01_CO').closest('tr')!;
    const pausedRow = screen.getByText('Flare_01_NOx').closest('tr')!;

    expect(within(activeRow).getByText('⟦mapping.delete⟧')).toBeDefined();
    expect(within(pausedRow).getByText('⟦mapping.delete⟧')).toBeDefined();
  });
});

describe('DeleteMappingAction: підтвердження перед запитом', () => {
  it('клік по кнопці НЕ надсилає запиту — лише відкриває підтвердження', async () => {
    const fetchMock = stubDelete();

    wrap(<DeleteMappingAction field={Field} />);
    fireEvent.click(screen.getByText('⟦mapping.delete⟧'));

    // ⛔ Саме це й є мутаційний доказ: приберіть діалог і викличте
    // `remove.mutate()` прямо з кнопки — цей рядок почервоніє.
    expect(fetchMock).not.toHaveBeenCalled();

    await waitFor(() => expect(screen.getByTestId('confirm-verb')).toBeDefined());
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('фокус стоїть на «Скасувати», а не на кнопці видалення (правило L6)', async () => {
    stubDelete();

    wrap(<DeleteMappingAction field={Field} />);
    fireEvent.click(screen.getByText('⟦mapping.delete⟧'));

    await waitFor(() =>
      expect(document.activeElement).toBe(screen.getByTestId('confirm-cancel')),
    );
  });

  it('«Скасувати» закриває діалог і НЕ надсилає запиту', async () => {
    const fetchMock = stubDelete();

    wrap(<DeleteMappingAction field={Field} />);
    fireEvent.click(screen.getByText('⟦mapping.delete⟧'));
    await waitFor(() => expect(screen.getByTestId('confirm-cancel')).toBeDefined());

    fireEvent.click(screen.getByTestId('confirm-cancel'));

    await waitFor(() => expect(screen.queryByTestId('confirm-verb')).toBeNull());
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('підтвердження шле DELETE саме на адресу цього мапінгу', async () => {
    const fetchMock = stubDelete();

    wrap(<DeleteMappingAction field={{ ...Field, fieldMapId: 77 }} />);
    fireEvent.click(screen.getByText('⟦mapping.delete⟧'));
    await waitFor(() => expect(screen.getByTestId('confirm-verb')).toBeDefined());

    fireEvent.click(screen.getByTestId('confirm-verb'));

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];

    expect(String(url)).toBe('/api/v1/entity-field-maps/77');
    expect(String(init.method)).toBe('DELETE');
  });

  it('заголовок називає поле, а наслідок — адресу, куди мапінг клав значення', async () => {
    stubDelete();

    wrap(<DeleteMappingAction field={Field} />);
    fireEvent.click(screen.getByText('⟦mapping.delete⟧'));

    await waitFor(() => expect(screen.getByTestId('confirm-consequences')).toBeDefined());

    // ⚠ Каталог у тестах не піднятий, тож `t` віддає `⟦ключ (параметри)⟧` —
    // перевіряються САМЕ параметри: заголовок без назви поля й наслідок без
    // адреси є найчастіша форма «підтвердження ні про що».
    expect(screen.getByText(/⟦mapping\.deleteTitle \(field=Flare_01_CO\)⟧/)).toBeDefined();
    expect(
      within(screen.getByTestId('confirm-consequences')).getByText(
        /⟦mapping\.deleteConsequence \(target=Flare_01 · CO_MASS\)⟧/,
      ),
    ).toBeDefined();
  });

  it('відмова «за мапінгом уже зібрано дані» показує ЧИСЛО точок, а не нейтральний код', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(
            // ⚠ Розширення лежать ПЛОСКО у верхньому рівні тіла (RFC 9457
            // §3.2) — саме так їх пише сервер і саме так `problemBodyOf` їх
            // збирає в `extensions2`. Фікстура, що загортає їх у `extensions2`
            // сама, перевіряла б форму, якої на дроті не буває.
            JSON.stringify({
              status: 409,
              title: 'Conflict',
              errorCode: 'ECR-INT-0409',
              messageKey: 'err.ECR-INT-0409.mappingHasCollectedData',
              collectedPoints: 4812,
            }),
            { status: 409, headers: { 'Content-Type': 'application/problem+json' } },
          ),
      ),
    );

    wrap(<DeleteMappingAction field={Field} />);
    fireEvent.click(screen.getByText('⟦mapping.delete⟧'));
    await waitFor(() => expect(screen.getByTestId('confirm-verb')).toBeDefined());

    fireEvent.click(screen.getByTestId('confirm-verb'));

    await waitFor(() =>
      expect(screen.getByText(/⟦mapping\.deleteBlocked \(points=4812\)⟧/)).toBeDefined(),
    );

    // Діалог закрито: пропонувати «видалити ще раз» у відповідь на 409 —
    // порада повторити те, що сервер щойно відмовився робити.
    // ⚠ Через `waitFor`: Mantine знімає `<Modal>` з DOM не в тому ж такті, що
    // й `opened={false}`, а після переходу.
    await waitFor(() => expect(screen.queryByTestId('confirm-verb')).toBeNull());
  });
});

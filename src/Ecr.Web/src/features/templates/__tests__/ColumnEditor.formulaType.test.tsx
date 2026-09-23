import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { emptyColumnDraft, type ColumnDraft } from '../column';
import { testTheme } from '@/test/render';

/**
 * Дефект, знайдений живим переглядом (сьогодні): форма "Add column"
 * (`ColumnEditor.tsx` через `TemplateVersionPage.tsx`) пропонувала лише
 * `EditableDataTypes` — масив без `Formula`/`Calculated`. Тип колонки
 * незмінний ПІСЛЯ створення (`disabled={!draft.isNew}` на `Select` нижче в
 * компоненті), а прикріпити формулу до колонки, заведеної іншим типом
 * (`Decimal`), сервер відхиляє: `err.ECR-TMPL-4227.formulaOnManualColumn`
 * (`FormulaDefHandlers.cs:269-281`) — «Create a Formula column (the type
 * cannot be changed) or remove the formula». Тобто сервер сам казав
 * конфігуратору завести колонку типу, якого форма не давала обрати:
 * колонку Formula/Calculated через UI завести було НЕМОЖЛИВО, хоча сервер
 * приймає обидва типи при створенні
 * (`tests/Ecr.Scenarios.Tests/DataEntryScenarios.cs:1112-1119` — Calculated,
 * `:1145-1155` — Formula).
 *
 * ⚠ Саму формулу форма тут не вводить — це окремий крок, наявна кнопка
 * "Formula" в переліку колонок (`TemplateVersionPage.tsx` → `FormulaEditor`,
 * уже працює). Ця форма лише заводить колонку ПОТРІБНОГО типу, порожню.
 *
 * ⚠ Каталог тут не вантажиться (як і в `ColumnEditor.blockerLabels.test.tsx`):
 * `t()` повертає позначений ключ `⟦…⟧`, підписи опцій `Select` — сирі
 * значення enum (`data={EditableColumnDataTypes}`, масив рядків без i18n).
 */

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function mockServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';

      if (path.endsWith('/api/v1/languages')) {
        return json([{ code: 'en', nameNative: 'English', isDefault: true }]);
      }

      return json(null);
    }),
  );
}

async function show(draft: ColumnDraft, onChange: (next: ColumnDraft) => void): Promise<void> {
  mockServer();

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const { ColumnEditor } = await import('../ColumnEditor');

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <ColumnEditor
          draft={draft}
          disabled={false}
          saving={false}
          templateVersionId={1}
          onChange={onChange}
          onSubmit={() => {}}
          onCancel={() => {}}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ColumnEditor: колонку типу Formula/Calculated можна завести через форму "Add column"', () => {
  it('список типів пропонує Formula і Calculated (мутаційний доказ у звіті задачі)', async () => {
    const onChange = vi.fn();
    const draft: ColumnDraft = { ...emptyColumnDraft(1) };
    await show(draft, onChange);

    fireEvent.click(await screen.findByLabelText(/columns\.dataType⟧/));

    fireEvent.click(await screen.findByRole('option', { name: 'Formula' }));
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ dataType: 'Formula' }));

    onChange.mockClear();
    fireEvent.click(await screen.findByLabelText(/columns\.dataType⟧/));
    fireEvent.click(await screen.findByRole('option', { name: 'Calculated' }));
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ dataType: 'Calculated' }));
  }, 30_000);

  it('нова колонка типу Formula, код і заголовок заповнені, формули ще немає — нічого не блокує', async () => {
    const draft: ColumnDraft = {
      ...emptyColumnDraft(1),
      code: 'Total',
      headerL10n: { en: 'Total' },
      dataType: 'Formula',
    };
    await show(draft, () => {});

    await screen.findByLabelText(/columns\.code⟧/);

    // Жодного попередження-блокувальника на екрані.
    expect(screen.queryByText(/⟦columns\.err/)).toBeNull();

    const saveButton = screen.getByRole('button', { name: '⟦columns.save⟧' }) as HTMLButtonElement;
    expect(saveButton.disabled).toBe(false);
  }, 30_000);

  it('те саме для Calculated', async () => {
    const draft: ColumnDraft = {
      ...emptyColumnDraft(1),
      code: 'Result',
      headerL10n: { en: 'Result' },
      dataType: 'Calculated',
    };
    await show(draft, () => {});

    await screen.findByLabelText(/columns\.code⟧/);

    expect(screen.queryByText(/⟦columns\.err/)).toBeNull();

    const saveButton = screen.getByRole('button', { name: '⟦columns.save⟧' }) as HTMLButtonElement;
    expect(saveButton.disabled).toBe(false);
  }, 30_000);
});

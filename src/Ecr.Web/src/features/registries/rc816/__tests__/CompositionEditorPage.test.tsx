import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CompositionEditorPage } from '../CompositionEditorPage';
import { mockServer } from './compositionServer';
import { testTheme } from '@/test/render';

/**
 * Редактор master-detail (`ФВ-8.16`): кейси потоку й склад газу кейсу редагуються як одна таблиця.
 *
 * ⚠ Каталог рядків не завантажений — підписи приходять ключами в `⟦…⟧` разом із параметрами, тож
 * Σ перевіряється саме тим числом, яке отримав `t()`.
 */

function show(code = 'STREAM_CASE'): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[`/admin/registries/${code}/composition`]}>
          <Routes>
            <Route path="/admin/registries/:code/composition" element={<CompositionEditorPage />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

async function openCase(label: string): Promise<HTMLElement> {
  fireEvent.click(await screen.findByRole('button', { name: label }));
  return waitFor(() => {
    const table = document.querySelector<HTMLElement>('[data-rc816-table="GAS_COMPOSITION"]');
    if (table === null) throw new Error('панель складу ще не з\'явилась');
    return table;
  });
}

function sumText(): string {
  return document.querySelector('[data-rc816-sum="SUM_100"]')?.textContent ?? '';
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('CompositionEditorPage: master-detail без введення ідентифікаторів', () => {
  it('обраний кейс відкриває лише ЙОГО склад, а поле композиції в складі не показується', async () => {
    const server = mockServer(['Registry.View', 'Registry.EditData']);
    show();

    const table = await openCase('E77');

    expect(server.rowQueries.some((query) => query.startsWith('GAS_COMPOSITION?') && query.includes('parentEntryId=77'))).toBe(true);
    const headers = within(table).getAllByRole('columnheader').map((header) => header.textContent);
    expect(headers).toContain('COMPONENT');
    expect(headers).not.toContain('CASE');
    expect(within(table).getAllByRole('row')).toHaveLength(3);
  });

  it('Σ складу рахується з поточних значень, разом із незбереженими', async () => {
    mockServer(['Registry.View', 'Registry.EditData']);
    show();
    const table = await openCase('E77');

    await waitFor(() => expect(sumText()).toContain('sum=99.8'));
    expect(sumText()).toContain('registries.rc816.sumOk');

    const inputs = within(table).getAllByRole('textbox', { name: 'MOL_PCT' });
    fireEvent.change(inputs[0] as HTMLElement, { target: { value: '50' } });

    expect(sumText()).toContain('sum=89.8');
    expect(sumText()).toContain('registries.rc816.sumOff');
  });

  it('нова частина зберігається пакетом із батьком у полі композиції', async () => {
    const server = mockServer(['Registry.View', 'Registry.EditData']);
    show();
    const table = await openCase('E77');

    const panel = table.closest<HTMLElement>('[data-rc816-panel]') as HTMLElement;
    fireEvent.click(within(panel).getByRole('button', { name: /registries\.rc816\.addPart/ }));
    const selects = await waitFor(() => {
      const found = within(table).getAllByRole('combobox', { name: 'COMPONENT' });
      if (found.length < 3) throw new Error('рядок ще не додано');
      return found;
    });
    await waitFor(() => expect(within(selects[2] as HTMLElement).getAllByRole('option').length).toBeGreaterThan(1));
    fireEvent.change(selects[2] as HTMLElement, { target: { value: '6' } });
    const values = within(table).getAllByRole('textbox', { name: 'MOL_PCT' });
    fireEvent.change(values[2] as HTMLElement, { target: { value: '0.2' } });

    expect(sumText()).toContain('sum=100');

    fireEvent.click(within(panel).getByRole('button', { name: /registries\.rc816\.save/ }));

    await waitFor(() => expect(server.batches).toHaveLength(1));
    const [batch] = server.batches;
    expect(batch?.code).toBe('GAS_COMPOSITION');
    expect(batch?.dryRun).toBe('false');
    expect(batch?.body.items).toEqual([
      { clientRowId: 'n1', op: 'upsert', id: null, code: null, baseVersion: null, values: { CASE: '77', COMPONENT: '6', MOL_PCT: '0.2' } },
    ]);
  });

  it('поки склад не збережено, інший кейс обрати не можна — правки не губляться мовчки', async () => {
    mockServer(['Registry.View', 'Registry.EditData']);
    show();
    const table = await openCase('E77');

    const other = (): HTMLElement => screen.getByRole('button', { name: 'E78' });
    expect(other().getAttribute('aria-disabled')).toBeNull();
    fireEvent.change(within(table).getAllByRole('textbox', { name: 'MOL_PCT' })[0] as HTMLElement, { target: { value: '1' } });

    await waitFor(() => expect(other().getAttribute('aria-disabled')).toBe('true'));
    // ⚠ Кнопка лишається у фокусі й пояснює, чому недоступна, — а клік нічого не перемикає.
    const describedBy = other().getAttribute('aria-describedby') ?? '';
    expect(document.getElementById(describedBy)?.textContent).toBe('⟦registries.rc816.selectionLocked⟧');
    fireEvent.click(other());
    expect(screen.getByRole('button', { name: 'E77' }).getAttribute('aria-pressed')).toBe('true');
    expect(other().getAttribute('aria-pressed')).toBe('false');
  });

  it('зміна верхнього батька скидає вибір середнього рівня — склад кейсу іншого потоку не лишається на екрані', async () => {
    mockServer(['Registry.View', 'Registry.EditData'], { threeLevels: true });
    show('STREAM');

    fireEvent.click(await screen.findByRole('button', { name: 'S1' }));
    fireEvent.click(await screen.findByRole('button', { name: 'E78' }));
    await waitFor(() => expect(document.querySelector('[data-rc816-panel="GAS_COMPOSITION"]')).not.toBeNull());

    fireEvent.click(screen.getByRole('button', { name: 'S2' }));

    // Кейси S2 на місці, а склад «370 Winter» потоку S1 — ні: інакше правка лягла б не в той потік.
    expect(await screen.findByRole('button', { name: 'E79' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'E78' })).toBeNull();
    expect(document.querySelector('[data-rc816-panel="GAS_COMPOSITION"]')).toBeNull();
    expect(screen.getByRole('button', { name: 'E79' }).getAttribute('aria-pressed')).toBe('false');
  });

  it('поки пакет зберігається, комірки й додавання заблоковані — правка під час запиту не губиться мовчки', async () => {
    let release: () => void = () => undefined;
    const server = mockServer(['Registry.View', 'Registry.EditData'], {
      holdCommit: new Promise<void>((resolve) => (release = resolve)),
    });
    show();
    const table = await openCase('E77');
    const panel = table.closest<HTMLElement>('[data-rc816-panel]') as HTMLElement;

    const values = (): HTMLElement[] => within(table).getAllByRole('textbox', { name: 'MOL_PCT' });
    fireEvent.change(values()[0] as HTMLElement, { target: { value: '60.2' } });
    fireEvent.click(within(panel).getByRole('button', { name: /registries\.rc816\.save/ }));
    await waitFor(() => expect(server.batches).toHaveLength(1));

    expect(values().every((input) => input.hasAttribute('disabled'))).toBe(true);
    expect(within(panel).getByRole('button', { name: /registries\.rc816\.addPart/ }).hasAttribute('disabled')).toBe(true);

    release();
    await waitFor(() => expect(values()[0]?.hasAttribute('disabled')).toBe(false));
  });

  it('після правки «Показати ще» додає довантажені рядки — вони не ховаються за знімком правок (L9-09)', async () => {
    mockServer(['Registry.View', 'Registry.EditData'], { pagedCases: true });
    show();
    await screen.findByRole('button', { name: 'E77' });

    const top = document.querySelector<HTMLElement>('[data-rc816-panel="STREAM_CASE"]') as HTMLElement;
    fireEvent.change(within(top).getAllByRole('textbox', { name: 'CASE_NAME' })[0] as HTMLElement, { target: { value: 'Renamed' } });
    fireEvent.click(within(top).getByRole('button', { name: /registries\.rc816\.more/ }));

    expect(await within(top).findByRole('button', { name: 'E999' })).toBeTruthy();
    // Правка лишилась на місці.
    expect((within(top).getAllByRole('textbox', { name: 'CASE_NAME' })[0] as HTMLInputElement).value).toBe('Renamed');
  });

  it('без Registry.EditData — лише перегляд: ні додавання, ні збереження', async () => {
    mockServer(['Registry.View']);
    show();
    await openCase('E77');

    expect(screen.getByText(/registries\.rc816\.readOnly/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: /registries\.rc816\.save/ })).toBeNull();
    expect(screen.queryByRole('button', { name: /registries\.rc816\.addPart/ })).toBeNull();
  });

  it('довідник-частина показує, чиєю частиною він є, і веде до редактора батька', async () => {
    mockServer(['Registry.View', 'Registry.EditData']);
    show('GAS_COMPOSITION');

    expect(await screen.findByText(/registries\.rc816\.isPartOf \(parent=STREAM_CASE\)/)).toBeTruthy();
    expect(screen.getByRole('link', { name: /registries\.rc816\.openParent/ }).getAttribute('href')).toBe(
      '/admin/registries/STREAM_CASE/composition',
    );
    expect(await screen.findByText(/registries\.rc816\.noChain/)).toBeTruthy();
  });
});

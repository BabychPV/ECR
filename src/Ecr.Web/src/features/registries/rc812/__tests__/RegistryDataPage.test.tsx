import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest';
import { act, fireEvent, screen, within } from '@testing-library/react';
import { QueryClient } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { mockServer, passed, showDataPage, storedRows, type SentBatch } from './fixtures';

/**
 * Табличний редактор даних довідника (`ФВ-8.12`, FEATURE-REGISTRY-TABLES §8.4, §8.8).
 *
 * ⛔ До цього екрана запис правився формою, де `Lookup` вводився СИРИМ id, а перелік показував
 * лише код і назву (§1.1 р.11). Тести нижче стережуть саме те, чого там бракувало: назва цілі
 * замість id, типізована правка з клавіатури, один пакет на збереження, помилки й дублі ключа
 * до збереження — у комірці, до якої вони належать.
 *
 * ⚠ Мутаційні докази (перевірено руками, 2026-09-30; кожна мутація — червоний тест):
 *   - `cellDisplay` повертає `value` замість `display` → «Lookup показує назву цілі…»;
 *   - без виклику збереження в обробнику `Ctrl+S` → «правка числа… Ctrl+S…» і «Ctrl+Shift+Delete…»;
 *   - `toBatch` без `baseVersion` наявного рядка → «правка числа… baseVersion…»;
 *   - `validateCell` без гілки неоднозначного → «неоднозначна кома (1,234)…»;
 *   - (2026-10-04, L9-04) `onEdit` без `normalizeCellInput` → «вставка 12,5 з Excel…»;
 *   - `problemsByRow` губить `field` → «помилка dryRun лягає в свою комірку…»;
 *   - `canSave` без `duplicates.size === 0` → «дубль ключа… блокує збереження».
 */

const cell = (r: number, c: number): HTMLElement => {
  const found = document.querySelector<HTMLElement>(`[data-cell="${String(r)}:${String(c)}"]`);
  if (found === null) throw new Error(`cell ${String(r)}:${String(c)} not found`);
  return found;
};

async function editText(r: number, c: number, text: string): Promise<void> {
  const target = cell(r, c);
  act(() => target.focus());
  fireEvent.keyDown(target, { key: 'Enter' });
  const input = await within(target).findByRole('textbox');
  fireEvent.change(input, { target: { value: text } });
  fireEvent.keyDown(input, { key: 'Enter' });
}

const committed = (sent: readonly SentBatch[]): SentBatch[] => sent.filter((b) => !b.dryRun);

beforeEach(async () => {
  mockServer();
  await loadCatalog('en', 'private');
  await loadCatalog('en', 'public');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Дані довідника: табличний редактор', () => {
  it('Lookup показує назву цілі, а не її id (ФВ-8.8)', async () => {
    showDataPage();
    const grid = await screen.findByRole('grid', { name: 'Stream cases' });

    expect(within(cell(0, 0)).getByText('1D-2 · HP Separator Gas')).toBeDefined();
    expect(within(grid).queryByText('162')).toBeNull();
    expect(grid.getAttribute('aria-rowcount')).toBe('3');
  });

  it('правка числа з клавіатури і Ctrl+S шле один пакет із baseVersion і лише зміненим полем', async () => {
    const sent = mockServer();
    showDataPage();
    await screen.findByRole('grid');

    await editText(0, 2, '50.5');
    expect(await screen.findByText(/1 unsaved changes/)).toBeDefined();

    fireEvent.keyDown(window, { key: 's', ctrlKey: true });

    await vi.waitFor(() => {
      expect(committed(sent)).toHaveLength(1);
    });
    expect(committed(sent)[0]?.items).toEqual([
      { clientRowId: 'e:4411', op: 'upsert', id: 4411, code: null, baseVersion: 'AAABkWmN3kM=', values: { T_C: '50.5' } },
    ]);
    expect(await screen.findByText(/^Saved /)).toBeDefined();
  });

  it('вставка 12,5 з Excel (ru/kz) дає 12.5 у пакеті й не блокує збереження (L9-04)', async () => {
    const sent = mockServer();
    showDataPage();
    const grid = await screen.findByRole('grid');

    act(() => cell(0, 2).focus());
    fireEvent.paste(grid, { clipboardData: { getData: () => '12,5\n7,25\n' } });
    await vi.waitFor(() => {
      expect(cell(1, 2).getAttribute('data-edited')).toBe('true');
    });
    expect(cell(0, 2).getAttribute('aria-invalid')).toBeNull();

    fireEvent.keyDown(window, { key: 's', ctrlKey: true });
    await vi.waitFor(() => {
      expect(committed(sent)).toHaveLength(1);
    });
    expect(committed(sent)[0]?.items.map((item) => item.values)).toEqual([{ T_C: '12.5' }, { T_C: '7.25' }]);
  });

  it('збереження скидає сторінку впливу довідника (L9-21)', async () => {
    mockServer();
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    client.setQueryData(['registry-impact', 'STREAM_CASE'], { items: [] });
    showDataPage(client);
    await screen.findByRole('grid');

    await editText(0, 2, '50.5');
    fireEvent.keyDown(window, { key: 's', ctrlKey: true });

    await vi.waitFor(() => {
      expect(client.getQueryState(['registry-impact', 'STREAM_CASE'])?.isInvalidated).toBe(true);
    });
  });

  it('неоднозначна кома (1,234) підсвічена до сервера і не дає зберегти', async () => {
    showDataPage();
    await screen.findByRole('grid');

    await editText(0, 2, '1,234');

    await vi.waitFor(() => {
      expect(cell(0, 2).getAttribute('aria-invalid')).toBe('true');
    });
    expect(cell(0, 2).getAttribute('title')).toBe('Use a dot, not a comma, as the decimal separator.');
    expect(screen.getByRole('button', { name: /Save 1 changes/ }).hasAttribute('disabled')).toBe(true);
  });

  it('помилка dryRun лягає в свою комірку, а не на весь рядок', async () => {
    mockServer({
      batch: (sent) => ({
        ...passed(sent),
        applied: false,
        rows: [
          {
            clientRowId: 'e:4411',
            entryId: 4411,
            status: 'error',
            version: null,
            errors: [{ field: 'T_C', errorCode: 'ECR-REG-4093', messageKey: 'err.ECR-REG-4093.entryChanged', params: { entryCode: 'E000004411' } }],
          },
        ],
      }),
    });
    showDataPage();
    await screen.findByRole('grid');

    await editText(0, 2, '51');

    await vi.waitFor(
      () => {
        expect(cell(0, 2).getAttribute('aria-invalid')).toBe('true');
      },
      { timeout: 3000 },
    );
    expect(cell(0, 1).getAttribute('aria-invalid')).toBeNull();
    expect(await screen.findByText(/1 errors, 0 warnings/, {}, { timeout: 3000 })).toBeDefined();
  });

  it('дубль ключа в сітці видно одразу, і він блокує збереження', async () => {
    showDataPage();
    await screen.findByRole('grid');

    await editText(1, 1, ' 370 WINTER ');

    const notes = await screen.findAllByText(/Same key PK as row/);
    expect(notes).toHaveLength(2);
    expect(screen.getByRole('button', { name: /Save 1 changes/ }).hasAttribute('disabled')).toBe(true);
  });

  it('вставка блоку з Excel за край таблиці додає нові рядки', async () => {
    const sent = mockServer();
    showDataPage();
    const grid = await screen.findByRole('grid');

    act(() => cell(1, 1).focus());
    fireEvent.paste(grid, { clipboardData: { getData: () => 'Autumn\t12.5\nSpring\t7\n' } });

    await vi.waitFor(() => {
      expect(document.querySelectorAll('[data-row-key^="n:"]')).toHaveLength(1);
    });
    expect(within(cell(2, 1)).getByText('Spring')).toBeDefined();

    fireEvent.keyDown(window, { key: 's', ctrlKey: true });
    // Новий рядок без обовʼязкового STREAM — збереження заблоковане ще на клієнті.
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(committed(sent)).toHaveLength(0);
  });

  it('поки пакет зберігається, комірки не редагуються і вставка не приймається — правка не зникне мовчки', async () => {
    let release: () => void = () => undefined;
    const sent = mockServer({ holdCommit: new Promise<void>((resolve) => (release = resolve)) });
    showDataPage();
    const grid = await screen.findByRole('grid');

    await editText(0, 2, '50.5');
    fireEvent.keyDown(window, { key: 's', ctrlKey: true });
    await vi.waitFor(() => {
      expect(committed(sent)).toHaveLength(1);
    });

    // Запит у дорозі: Enter не відкриває редактор, вставка не додає рядків.
    const other = cell(1, 2);
    act(() => other.focus());
    fireEvent.keyDown(other, { key: 'Enter' });
    expect(within(other).queryByRole('textbox')).toBeNull();
    fireEvent.paste(grid, { clipboardData: { getData: () => 'x\ty\nz\tw\n' } });
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(document.querySelectorAll('[data-row-key^="n:"]')).toHaveLength(0);

    await act(async () => {
      release();
      await Promise.resolve();
    });
    expect(await screen.findByText(/^Saved /)).toBeDefined();

    // Після відповіді редагування знову доступне.
    act(() => other.focus());
    fireEvent.keyDown(other, { key: 'Enter' });
    expect(await within(other).findByRole('textbox')).toBeDefined();
  });

  it('вставка трьох рядків з однаковим Lookup — один запит до довідника-цілі (L9-07)', async () => {
    mockServer();
    showDataPage();
    const grid = await screen.findByRole('grid');
    const lookupCalls = (): number =>
      vi.mocked(fetch).mock.calls.filter(([input]) => String(input).includes('/registries/STREAM/rows')).length;

    act(() => cell(0, 0).focus());
    fireEvent.paste(grid, { clipboardData: { getData: () => 'S162\ns162\nS162\n' } });

    await vi.waitFor(() => {
      expect(document.querySelectorAll('[data-row-key^="n:"]')).toHaveLength(1);
    });
    await vi.waitFor(() => {
      expect(within(cell(2, 0)).getByText('1D-2 · HP Separator Gas (S162)')).toBeDefined();
    });
    expect(lookupCalls()).toBe(1);
  });

  it('відмова зіставлення Lookup — банер незіставлених, а не мовчки відкинутий хвіст (L9-07)', async () => {
    mockServer({ lookupStatus: 403 });
    const unhandled = vi.fn();
    window.addEventListener('unhandledrejection', unhandled);
    showDataPage();
    const grid = await screen.findByRole('grid');

    act(() => cell(0, 0).focus());
    fireEvent.paste(grid, { clipboardData: { getData: () => 'S162\tAutumn\n' } });

    expect(await screen.findByText('1 pasted cells could not be matched and were left unchanged.')).toBeDefined();
    expect(within(cell(0, 1)).getByText('Autumn')).toBeDefined();
    window.removeEventListener('unhandledrejection', unhandled);
    expect(unhandled).not.toHaveBeenCalled();
  });

  it('без Registry.EditData — лише читання: пояснення словами, без збереження, Enter не редагує', async () => {
    mockServer({ permissions: ['Registry.View'] });
    showDataPage();
    await screen.findByRole('grid');

    expect(await screen.findByText('Read only: editing needs the Registry.EditData permission.')).toBeDefined();
    expect(screen.queryByRole('button', { name: /^Save/ })).toBeNull();

    const target = cell(0, 2);
    act(() => target.focus());
    fireEvent.keyDown(target, { key: 'Enter' });
    expect(within(target).queryByRole('textbox')).toBeNull();
  });

  it('Ctrl+Enter додає рядок і переводить фокус на НЬОГО, а не лишає на попередньому (L9-16)', async () => {
    showDataPage();
    await screen.findByRole('grid');

    const target = cell(1, 1);
    act(() => target.focus());
    fireEvent.keyDown(target, { key: 'Enter', ctrlKey: true });

    await vi.waitFor(() => {
      expect(document.activeElement?.getAttribute('data-cell')).toBe('2:0');
    });
    expect(document.querySelectorAll('[data-row-key^="n:"]')).toHaveLength(1);
  });

  it('симуляція «очима користувача» з Registry.EditData — лише читання: банер, Enter не редагує, пакетів немає (L9-18)', async () => {
    const sent = mockServer({ simulation: true });
    showDataPage();
    await screen.findByRole('grid');

    expect(await screen.findByText('Permission simulation: writing is disabled regardless of permissions.')).toBeDefined();
    expect(screen.queryByRole('button', { name: /^Save/ })).toBeNull();

    const target = cell(0, 2);
    act(() => target.focus());
    fireEvent.keyDown(target, { key: 'Enter' });
    expect(within(target).queryByRole('textbox')).toBeNull();
    fireEvent.keyDown(window, { key: 's', ctrlKey: true });
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(sent).toHaveLength(0);
  });

  it('Ctrl+Shift+Delete позначає рядок до видалення, пакет несе op delete', async () => {
    const sent = mockServer();
    showDataPage();
    await screen.findByRole('grid');

    const target = cell(1, 0);
    act(() => target.focus());
    fireEvent.keyDown(target, { key: 'Delete', ctrlKey: true, shiftKey: true });
    fireEvent.keyDown(window, { key: 's', ctrlKey: true });

    await vi.waitFor(() => {
      expect(committed(sent)).toHaveLength(1);
    });
    expect(committed(sent)[0]?.items).toEqual([
      { clientRowId: 'e:4412', op: 'delete', id: 4412, code: null, baseVersion: storedRows[1]?.version, values: null },
    ]);
  });
});

/**
 * Контраст рядка, позначеного до видалення (WCAG 1.4.3).
 *
 * ⛔ Було `opacity: 0.6`: напівпрозорість множить контраст і приглушеного тексту (`--ecr-muted`, ≥ 4.5 лише
 * без неї) — рядок падав нижче AA в обох темах. Мутаційний доказ (перевірено руками 2026-09-30): повернути
 * `opacity: 0.6` у стиль рядка → червоний.
 */
describe('Дані довідника: рядок до видалення', () => {
  it('закреслений і приглушений токеном, без напівпрозорості', async () => {
    showDataPage();
    await screen.findByRole('grid');

    const target = cell(1, 0);
    act(() => target.focus());
    fireEvent.keyDown(target, { key: 'Delete', ctrlKey: true, shiftKey: true });

    const row = target.closest('tr') as HTMLElement;
    await vi.waitFor(() => expect(row.getAttribute('data-deleted')).toBe('true'));
    expect(row.style.textDecoration).toBe('line-through');
    expect(row.style.opacity).toBe('');
    expect(row.style.getPropertyValue('--text-color') || row.style.color).toContain('dimmed');
  });
});


import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { emptyColumnDraft, type ColumnDraft } from '../column';
import { emptyStyleDraft } from '../style';
import { testTheme } from '@/test/render';

/**
 * Причина, з якої колонку ще не можна зберегти, називається РЕЧЕННЯМ.
 *
 * ⛔ Дві причини стилю (`StyleCode`, `StyleFontSize`) падали в `default` і
 * показувалися голим кодом переліку розробника — тобто людина бачила
 * `StyleFontSize` замість «розмір шрифту має бути числом». Рядків у каталозі
 * не було взагалі, тож це не можна було полагодити самим підписом.
 *
 * ⚠ Каталог тут не вантажиться: `t()` повертає позначений ключ `⟦…⟧`, і саме
 * його наявність доводить, що підпис береться з каталогу, а не з коду.
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

async function show(draft: ColumnDraft): Promise<void> {
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
          onChange={() => {}}
          onSubmit={() => {}}
          onCancel={() => {}}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Заповнена чернетка: решта причин мовчить, і видно рівно ту, що перевіряємо. */
const filled: ColumnDraft = {
  ...emptyColumnDraft(1),
  code: 'Amount',
  headerL10n: { en: 'Amount' },
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ColumnEditor: причини стилю названі реченням, а не кодом', () => {
  it('недійсний код стилю — підпис із каталогу, а не слово StyleCode', async () => {
    await show({ ...filled, style: { ...emptyStyleDraft(''), code: '' } });

    expect(await screen.findByText('⟦columns.errStyleCode⟧')).toBeDefined();

    // ⛔ Саме цього й не було: код переліку розробника на екрані користувача.
    expect(screen.queryByText('StyleCode')).toBeNull();
  }, 30_000);

  it('нечисловий розмір шрифту — теж речення, а не слово StyleFontSize', async () => {
    await show({
      ...filled,
      style: { ...emptyStyleDraft('AmountStyle'), fontSize: 'abc' },
    });

    expect(await screen.findByText('⟦columns.errStyleFontSize⟧')).toBeDefined();
    expect(screen.queryByText('StyleFontSize')).toBeNull();
  }, 30_000);

  it('дзеркало: чернетка без вад — жодної причини на екрані', async () => {
    await show({ ...filled, style: { ...emptyStyleDraft('AmountStyle'), fontSize: '11' } });

    // ⚠ Дочекатися самої форми, інакше «причини немає» зелене просто тому, що
    // компонент ще не змонтувався.
    await screen.findByLabelText(/columns\.code⟧/);

    expect(screen.queryByText('⟦columns.errStyleCode⟧')).toBeNull();
    expect(screen.queryByText('⟦columns.errStyleFontSize⟧')).toBeNull();
  }, 30_000);
});

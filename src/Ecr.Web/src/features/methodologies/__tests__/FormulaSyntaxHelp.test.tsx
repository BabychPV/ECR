import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { testTheme } from '@/test/render';
import { SyntaxExamples } from '../formulaSyntax';

/**
 * Довідка синтаксису в діалозі формули: закрита → кнопка `aria-expanded` відкриває панель
 * під редактором; у панелі — позначення `@`, `CST.`, `!`, `HDR.`, `[Period]`, приклади
 * (`formulaSyntax.ts`) і функції З СЕРВЕРА (`/expressions/metadata`), а не зашитий перелік.
 */

const Strings: Record<string, string> = {
  'methodologies.syntaxHelp.toggle': 'Syntax help',
  'methodologies.syntaxHelp.argsExact': 'arguments: {count}',
  'methodologies.syntaxHelp.argsRange': 'arguments: {min}–{max}',
  'methodologies.syntaxHelp.argsAtLeast': 'arguments: {min} or more',
  'methodologies.syntaxHelp.extension': 'extension',
  'methodologies.syntaxHelp.refPeriod': 'The calendar of the current period: {properties}.',
};

function mockApi(): { metadataCalls: string[] } {
  const metadataCalls: string[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (payload: unknown): Response =>
        new Response(JSON.stringify(payload), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Strings });

      if (url.includes('/api/v1/expressions/metadata')) {
        metadataCalls.push(url);

        return json({
          arguments: [],
          constants: [],
          formulas: [],
          headers: [],
          functions: [
            { name: 'Pow', minArgs: 2, maxArgs: 2, acceptsRange: false, resultType: 'Number', tier: 'Core' },
            { name: 'in', minArgs: 2, maxArgs: null, acceptsRange: false, resultType: 'Boolean', tier: 'Core' },
            { name: 'REGFIND', minArgs: 2, maxArgs: null, acceptsRange: false, resultType: 'Number', tier: 'Extension' },
          ],
        });
      }

      throw new Error(`Немає мока для ${url}`);
    }),
  );

  return { metadataCalls };
}

async function show(): Promise<void> {
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');
  const { FormulaSyntaxHelp } = await import('../FormulaSyntaxHelp');
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <FormulaSyntaxHelp versionId={42} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Довідка синтаксису формули', () => {
  it('закрита за замовчуванням і не ходить мережею; кнопка керує панеллю через aria-controls', async () => {
    const { metadataCalls } = mockApi();
    await show();

    const toggle = await screen.findByRole('button', { name: 'Syntax help' });
    const panel = document.getElementById(toggle.getAttribute('aria-controls') ?? '');

    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(panel?.hidden).toBe(true);
    expect(metadataCalls).toHaveLength(0);

    fireEvent.click(toggle);

    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(panel?.hidden).toBe(false);

    fireEvent.click(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(panel?.hidden).toBe(true);
  }, 60000);

  it('пояснює @, CST., !, HDR. і [Period]; показує кожен приклад', async () => {
    mockApi();
    await show();
    fireEvent.click(await screen.findByRole('button', { name: 'Syntax help' }));

    for (const token of ['@Name', 'CST.Name', '!Name', 'HDR.Name', '[Period].Days', '^', '&']) {
      expect(document.querySelector(`[data-syntax-token="${token}"]`), token).not.toBeNull();
    }

    expect(screen.getByText(/Days, Hours, Seconds, Start, End, Year, Sequence/)).toBeTruthy();

    for (const example of SyntaxExamples) {
      const row = document.querySelector(`[data-example="${example.id}"]`);
      expect(row?.textContent, example.id).toContain(example.code);
    }
  }, 60000);

  it('функції — з відповіді сервера, з арністю і позначкою розширення', async () => {
    const { metadataCalls } = mockApi();
    await show();
    fireEvent.click(await screen.findByRole('button', { name: 'Syntax help' }));

    await screen.findByTestId('syntax-help-functions');
    expect(metadataCalls).toHaveLength(1);
    expect(metadataCalls[0]).toContain('dialect=Methodology');
    expect(metadataCalls[0]).toContain('methodologyVersionId=42');

    expect(document.querySelector('[data-function="Pow"]')?.textContent).toContain('arguments: 2');
    expect(document.querySelector('[data-function="in"]')?.textContent).toContain('arguments: 2 or more');
    // ⛔ Мутація «позначка розширення на всіх» або «на жодній» валить одну з двох перевірок.
    expect(document.querySelector('[data-function="REGFIND"]')?.textContent).toContain('extension');
    expect(document.querySelector('[data-function="Pow"]')?.textContent).not.toContain('extension');

    await waitFor(() => {
      expect(document.querySelectorAll('[data-function]')).toHaveLength(3);
    });
  }, 60000);
});

import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MethodologyCategoryRulePanel } from '@/features/methodologies/CategoryRulePanel';
import { loadCatalog } from '@/shared/i18n';
import { testTheme } from '@/test/render';

/**
 * Панель правила категорії (RC14-F): без права/не чернетка — жодних дій; з правом — «Set rule» → PUT
 * із тілом `{expression}`; наявне правило показується й видаляється через підтвердження (DELETE).
 */

const Strings: Record<string, string> = {
  'methodologies.categoryRule': 'Category rule',
  'methodologies.categoryRuleSet': 'Set rule',
  'methodologies.categoryRuleEdit': 'Edit rule',
  'methodologies.categoryRuleDelete': 'Delete rule',
  'methodologies.categoryRuleDeleteHint': 'Delete the rule?',
  'methodologies.categoryRuleNone': 'This version has no category rule',
  'methodologies.categoryRuleNoneHint': 'Hint',
  'methodologies.categoryRuleExpression': 'Expression',
  'methodologies.categoryRuleExpressionHint': 'Must return text',
  'methodologies.categoryRuleUpdatedAt': 'Last changed',
  'methodologies.categoryRuleSaved': 'Saved.',
  'methodologies.categoryRuleDeleted': 'Deleted.',
  'common.save': 'Save',
  'common.cancel': 'Cancel',
  'common.delete': 'Remove',
  'common.retry': 'Retry',
  'state.errorTitle': 'The request failed',
  'state.errorUnknown': 'An unexpected error occurred.',
  'state.emptyTitle': 'Nothing here yet',
};

const Url = '/api/v1/methodologies/1/versions/10/category-rule';

function mockApi(initial: string | null): { calls: { method: string; body: unknown }[] } {
  let expression = initial;
  const calls: { method: string; body: unknown }[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      const json = (body: unknown, status = 200): Response =>
        new Response(status === 204 ? null : JSON.stringify(body), {
          status,
          headers: { 'Content-Type': 'application/json' },
        });

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Strings });

      if (url.endsWith(Url)) {
        if (method === 'PUT') {
          const body = JSON.parse(String(init?.body)) as { expression: string };
          calls.push({ method, body });
          expression = body.expression;
        } else if (method === 'DELETE') {
          calls.push({ method, body: null });
          expression = null;
          return json(null, 204);
        }

        return json({ expression, updatedAt: expression === null ? null : '2026-10-08T01:00:00Z' });
      }

      throw new Error(`Немає мока для ${method} ${url}`);
    }),
  );

  return { calls };
}

function show(editable: boolean): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MethodologyCategoryRulePanel methodologyId={1} versionId={10} editable={editable} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('MethodologyCategoryRulePanel', () => {
  it('без права дій немає, правило видно', async () => {
    mockApi("if(@Fuel = 'D', 'Diesel', 'Gas')");
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    show(false);

    await screen.findByText("if(@Fuel = 'D', 'Diesel', 'Gas')");
    expect(screen.queryByRole('button', { name: 'Edit rule' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Delete rule' })).toBeNull();
  });

  it('з правом: порожній стан → Set rule → PUT з виразом', async () => {
    const { calls } = mockApi(null);
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    show(true);

    await screen.findByText('This version has no category rule');
    fireEvent.click(screen.getByRole('button', { name: 'Set rule' }));

    const input = await screen.findByLabelText('Expression');
    const save = screen.getByRole('button', { name: 'Save' });
    expect((save as HTMLButtonElement).disabled).toBe(true);

    fireEvent.change(input, { target: { value: '  !ECW_Category  ' } });
    fireEvent.click(save);

    await waitFor(() => expect(calls).toEqual([{ method: 'PUT', body: { expression: '!ECW_Category' } }]));
    await screen.findByText('!ECW_Category');
  });

  it('видалення йде через підтвердження і викликає DELETE', async () => {
    const { calls } = mockApi('!ECW_Category');
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    show(true);

    await screen.findByText('!ECW_Category');
    fireEvent.click(screen.getByRole('button', { name: 'Delete rule' }));
    expect(calls).toEqual([]);

    fireEvent.click(await screen.findByRole('button', { name: 'Remove' }));

    await waitFor(() => expect(calls).toEqual([{ method: 'DELETE', body: null }]));
    await screen.findByText('This version has no category rule');
  });
});

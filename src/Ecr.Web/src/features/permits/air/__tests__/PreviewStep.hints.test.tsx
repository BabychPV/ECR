import type { JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { focusManager, QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { TestProviders } from '@/test/render';
import * as airApi from '@/features/permits/air/airApi';
import { EmptyDraft } from '../import/importModel';
import { nothingToChange, PreviewStep } from '../import/PreviewStep';
import type { AirPreview } from '../airTypes';
import { preview, tree } from './airFixtures';

/**
 * Preview step, walkthrough fixes: a hint (link to the NAE structure page in a new tab) when there is no node to create codes
 * under; the «nothing would change» note on an unchanged re-import; the tree is re-read when the user comes back to the tab.
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));
vi.mock('@/features/permits/air/airApi', () => ({ getNaeTree: vi.fn() }));

const getNaeTree = vi.mocked(airApi.getNaeTree);
const text = (key: string): string => Catalog[key] ?? key;

beforeEach(() => getNaeTree.mockResolvedValue(tree()));
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

function mount(body: AirPreview): JSX.Element {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return (
    <TestProviders>
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <PreviewStep
            preview={body}
            draft={EmptyDraft}
            onDraft={vi.fn()}
            stale={false}
            onRerun={vi.fn()}
            rerunning={false}
            onApply={vi.fn()}
            applying={false}
            mayApply
          />
        </MemoryRouter>
      </QueryClientProvider>
    </TestProviders>
  );
}

const unchanged = (): AirPreview =>
  preview({
    missing: [],
    issues: [],
    replace: { confirmationRequired: false, thisOwner: { ownerKey: '5', label: 'P', parents: 3, children: 6 }, otherOwners: [] },
    diff: { newParents: 0, changedParents: 0, unchangedParents: 3, removedParents: 0, skippedParents: 0, children: 6, newChildren: 0, changedChildren: 0, removedChildren: 0 },
  });

describe('PreviewStep — missing codes and an empty NAE tree', () => {
  it('no usable node: a hint with a link to the NAE structure page in a new tab', async () => {
    getNaeTree.mockResolvedValue({ ...tree(), nodes: [] });
    render(mount(preview()));

    const hint = await screen.findByTestId('air-no-nodes-hint');
    const link = hint.querySelector('a');
    expect(hint.textContent).toContain(text('permits.air.import.missing.noNodesHint'));
    expect(link?.getAttribute('href')).toBe('/admin/permits/nae');
    expect(link?.getAttribute('target')).toBe('_blank');
    expect(link?.getAttribute('rel')).toContain('noopener');
  });

  it('nodes exist: no hint', async () => {
    render(mount(preview()));
    await waitFor(() => expect(getNaeTree).toHaveBeenCalled());
    await screen.findByTestId('air-missing-group');
    expect(screen.queryByTestId('air-no-nodes-hint')).toBeNull();
  });
});

describe('PreviewStep — coming back from the NAE structure page', () => {
  it('the tree is read again on window focus even within the default stale time', async () => {
    getNaeTree.mockResolvedValue({ ...tree(), nodes: [] });
    render(mount(preview()));
    await screen.findByTestId('air-no-nodes-hint');
    expect(getNaeTree).toHaveBeenCalledTimes(1);

    getNaeTree.mockResolvedValue(tree());
    focusManager.setFocused(false);
    focusManager.setFocused(true);

    await waitFor(() => expect(getNaeTree).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(screen.queryByTestId('air-no-nodes-hint')).toBeNull());
  });
});

describe('PreviewStep — nothing would change', () => {
  it('New = Changed = Removed = 0 and no missing codes: the info note, the row says «from the file», Apply stays enabled', () => {
    const body = unchanged();
    expect(nothingToChange(body)).toBe(true);
    render(mount(body));

    expect(screen.getByTestId('air-nothing-changes').textContent).toContain(text('permits.air.import.replace.nothing'));
    expect(screen.getByText(text('permits.air.import.replace.new'))).toBeTruthy();
    expect((screen.getByRole('button', { name: text('permits.air.import.apply') }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('any new, changed or removed source, or a missing code: no note', () => {
    for (const diff of [{ newParents: 1 }, { changedParents: 1 }, { removedParents: 1 }]) {
      const body = unchanged();
      expect(nothingToChange({ ...body, preview: { ...body.preview, diff: { ...body.preview.diff, ...diff } } })).toBe(false);
    }
    expect(nothingToChange(preview())).toBe(false);
    expect(nothingToChange({ ...unchanged(), preview: { ...unchanged().preview, missing: preview().preview.missing } })).toBe(false);
  });
});

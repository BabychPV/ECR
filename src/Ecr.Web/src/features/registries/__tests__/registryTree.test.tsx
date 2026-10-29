import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { apiFetch } from '@/api/client';
import { RegistryTreeEditor } from '@/features/registries/tree/RegistryTreeEditor';
import { toTreeLevels, toTreeNodes } from '@/features/registries/tree/treeAdapter';
import type { RegistryHierarchy, RegistryTree, RegistryTreeNode } from '@/features/registries/tree/treeApi';
import { renderWithQuery } from '@/test/render';

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  apiFetch: vi.fn(),
}));

/**
 * Адаптер дерева довідника до контракту N1: відображення DTO -> вузли, адреси й тіла запитів, оновлення після змін.
 * Мережа - підмінений `apiFetch` (обробники сервера ще скелет, e2e заблоковано до їх появи).
 */

const node = (over: Partial<RegistryTreeNode> & Pick<RegistryTreeNode, 'id' | 'code' | 'display'>): RegistryTreeNode => ({
  parentEntryId: null,
  levelCode: 'region',
  ordinal: 1,
  validFrom: null,
  validTo: null,
  isActive: true,
  childCount: 0,
  isMatch: false,
  ...over,
});

const hierarchy: RegistryHierarchy = {
  registryCode: 'NAE_TREE',
  isHierarchical: true,
  maxDepth: 6,
  levels: [
    { id: 2, ordinal: 2, code: 'site', name: { values: { en: 'Site' } }, isOptional: false, entryCount: 1 },
    { id: 1, ordinal: 1, code: 'region', name: { values: { en: 'Region' } }, isOptional: false, entryCount: 1 },
    { id: 3, ordinal: 3, code: 'nae', name: { values: { en: 'NAE' } }, isOptional: false, entryCount: 2 },
  ],
};

const tree: RegistryTree = {
  registryCode: 'NAE_TREE',
  isHierarchical: true,
  dataRevision: 7,
  levels: hierarchy.levels,
  nodes: [
    node({ id: 1, code: 'ATY', display: 'Atyrau', childCount: 1 }),
    node({ id: 3, code: 'BRG', display: 'Barges', parentEntryId: 1, levelCode: 'site', childCount: 2 }),
    node({ id: 4, code: '1085', display: 'Exhaust pipe', parentEntryId: 3, levelCode: 'nae', ordinal: 1 }),
    node({ id: 6, code: '1086', display: 'Second pipe', parentEntryId: 3, levelCode: 'nae', ordinal: 2 }),
  ],
};

const calls = (): { path: string; init?: RequestInit }[] =>
  vi.mocked(apiFetch).mock.calls.map(([path, init]) => ({ path, ...(init === undefined ? {} : { init }) }));

beforeEach(() => {
  vi.mocked(apiFetch).mockReset();
  vi.mocked(apiFetch).mockImplementation((path: string) => {
    if (path.includes('/hierarchy')) return Promise.resolve(hierarchy);
    if (path.includes('/tree')) return Promise.resolve(tree);
    if (path.endsWith('/move')) return Promise.resolve({ id: 4, parentEntryId: 3, levelCode: 'nae', ordinal: 2 });
    if (path.endsWith('/retire')) return Promise.resolve({ retiredIds: [1, 3, 4, 6], affectedRows: 0 });

    return Promise.reject(new Error(`unexpected ${path}`));
  });
});

afterEach(cleanup);

describe('treeAdapter', () => {
  it('вузол виведений, коли вимкнений або вікно закрите (validTo - перший недійсний день)', () => {
    const mapped = toTreeNodes(
      [
        node({ id: 1, code: 'a', display: 'A' }),
        node({ id: 2, code: 'b', display: 'B', isActive: false }),
        node({ id: 3, code: 'c', display: 'C', validTo: '2026-10-29' }),
        node({ id: 4, code: 'd', display: 'D', validTo: '2026-10-30' }),
        node({ id: 5, code: 'e', display: 'E', validFrom: '2027-01-01' }),
      ],
      '2026-10-29',
    );

    expect(mapped.map((item) => item.retired)).toEqual([false, true, true, false, false]);
  });

  it('поля вузла: батько, назва, код, рівень, діти, збіг', () => {
    const [mapped] = toTreeNodes([node({ id: 9, code: 'x', display: 'X', parentEntryId: 3, levelCode: 'nae', ordinal: 4, childCount: 2, isMatch: true })], '2026-10-29');

    expect(mapped).toMatchObject({ id: 9, parentId: 3, label: 'X', code: 'x', levelCode: 'nae', ordinal: 4, childCount: 2, isMatch: true });
  });

  it('рівні сортуються за ordinal, назва - мовою читача, запасний варіант - код', () => {
    const levels = toTreeLevels([...hierarchy.levels, { id: 9, ordinal: 4, code: 'raw', name: { values: {} }, isOptional: true, entryCount: 0 }]);

    expect(levels.map((level) => level.code)).toEqual(['region', 'site', 'nae', 'raw']);
    expect(levels.map((level) => level.name)).toEqual(['Region', 'Site', 'NAE', 'raw']);
    expect(levels[3]?.isOptional).toBe(true);
  });
});

describe('RegistryTreeEditor', () => {
  const mount = (canEdit = true) => renderWithQuery(<RegistryTreeEditor registryCode="NAE_TREE" label="NAE structure" canEdit={canEdit} />);

  it('читає рівні й дерево за адресами контракту та малює дерево з рівнів сервера', async () => {
    mount();

    expect(await screen.findByRole('treeitem', { name: /Atyrau/ })).toBeTruthy();
    const paths = calls().map((call) => call.path);
    expect(paths).toContain('/api/v1/registries/NAE_TREE/hierarchy');
    expect(paths).toContain('/api/v1/registries/NAE_TREE/tree');
  });

  it('«Виведені» додає includeRetired=true, пошук - q (після паузи, обрізаний)', async () => {
    const user = userEvent.setup();
    mount();
    await screen.findByRole('treeitem', { name: /Atyrau/ });

    await user.click(screen.getByTestId('tree-show-retired'));
    await waitFor(() => expect(calls().map((call) => call.path)).toContain('/api/v1/registries/NAE_TREE/tree?includeRetired=true'));

    await user.type(screen.getByTestId('tree-search'), ' 108 ');
    await waitFor(
      () => expect(calls().map((call) => call.path)).toContain('/api/v1/registries/NAE_TREE/tree?includeRetired=true&q=108'),
      { timeout: 2000 },
    );
  });

  it('Alt+↓ - POST move з новим порядком і без зміни рівня; після успіху дерево перечитується', async () => {
    const user = userEvent.setup();
    mount();
    const barges = await screen.findByRole('treeitem', { name: /Barges/ });
    barges.focus();
    await user.keyboard('{ArrowRight}');
    const row = await screen.findByRole('treeitem', { name: /Exhaust pipe/ });
    const before = calls().filter((call) => call.path.endsWith('/tree')).length;

    row.focus();
    await user.keyboard('{Alt>}{ArrowDown}{/Alt}');

    await waitFor(() => expect(calls().some((call) => call.path.endsWith('/entries/4/move'))).toBe(true));
    const move = calls().find((call) => call.path.endsWith('/entries/4/move'));
    expect(move?.init?.method).toBe('POST');
    expect(JSON.parse(String(move?.init?.body))).toEqual({ parentEntryId: 3, levelCode: null, beforeEntryId: null });
    await waitFor(() => expect(calls().filter((call) => call.path.endsWith('/tree')).length).toBeGreaterThan(before));
  });

  it('Delete + підтвердження - POST retire з каскадом для вузла з дітьми', async () => {
    const user = userEvent.setup();
    mount();
    const row = await screen.findByRole('treeitem', { name: /Atyrau/ });

    row.focus();
    await user.keyboard('{Delete}');
    await user.click(await screen.findByTestId('confirm-verb'));

    await waitFor(() => expect(calls().some((call) => call.path.endsWith('/entries/1/retire'))).toBe(true));
    const retire = calls().find((call) => call.path.endsWith('/entries/1/retire'));
    expect(JSON.parse(String(retire?.init?.body))).toEqual({ on: null, cascade: true });
  });

  it('без права змін запитів move/retire немає, а дерево читається', async () => {
    const user = userEvent.setup();
    mount(false);
    const row = await screen.findByRole('treeitem', { name: /Atyrau/ });

    row.focus();
    await user.keyboard('{Delete}{Alt>}{ArrowDown}{/Alt}');

    expect(screen.queryByTestId('confirm-verb')).toBeNull();
    expect(calls().some((call) => call.path.endsWith('/move') || call.path.endsWith('/retire'))).toBe(false);
  });

  it('помилка читання без даних - сповіщення з повтором, а не порожнє дерево', async () => {
    vi.mocked(apiFetch).mockReset();
    vi.mocked(apiFetch).mockRejectedValue(new Error('boom'));
    mount();

    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeNull());
    expect(screen.queryByRole('tree')).toBeNull();
  });
});

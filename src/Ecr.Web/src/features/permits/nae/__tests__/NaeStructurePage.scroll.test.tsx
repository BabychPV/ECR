import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import { renderWithQuery } from '@/test/render';
import { NaeStructurePage } from '../NaeStructurePage';
import { SplitTopVar } from '../useSplitTop';

/**
 * Сторінка структури NAE не стрибає (людина 05.11). Причина стрибка: вибір вузла — `navigate(?node=, replace)` → нова
 * `location.key` → глобальний `<ScrollRestoration/>` скидає вікно на (0, 0); довга панель змушувала гортати саме вікно.
 * Тут — те, що перевіряється без розкладки: блок із власною прокруткою, виміряний відступ, панель не перемонтовується
 * при зміні вибору. Справжня прокрутка — e2e `naeUx.spec.ts`.
 */
vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  apiFetch: vi.fn(),
}));
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string) => key,
}));
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { permissions: ['Registry.View', 'Registry.EditData'] } }),
}));

const levels = [
  { id: 1, ordinal: 1, code: 'REGION', name: { values: { en: 'Region' } }, isOptional: false, entryCount: 1 },
  { id: 3, ordinal: 3, code: 'SRC', name: { values: { en: 'NAE' } }, isOptional: false, entryCount: 1 },
];
const node = (id: number, display: string, levelCode: string, parentEntryId: number | null) => ({
  id,
  code: `T-${String(id)}`,
  display,
  levelCode,
  parentEntryId,
  ordinal: 1,
  validFrom: null,
  validTo: null,
  isActive: true,
  childCount: 0,
  isMatch: false,
});
const tree = {
  registryCode: 'NAE_TREE',
  isHierarchical: true,
  dataRevision: 1,
  levels,
  nodes: [node(1, 'Atyrau', 'REGION', null), node(3, 'Pipe 1085', 'SRC', 1)],
};

beforeEach(() => {
  vi.mocked(apiFetch).mockReset();
  vi.mocked(apiFetch).mockImplementation((path: string) => {
    if (path.endsWith('/definition')) return Promise.resolve({ id: 9, code: 'NAE_TREE', codeMode: 'Auto', fields: [] }) as never;
    if (path.endsWith('/hierarchy')) return Promise.resolve({ registryCode: 'NAE_TREE', isHierarchical: true, maxDepth: 6, levels }) as never;
    if (path.includes('/tree')) return Promise.resolve(tree) as never;
    if (path.endsWith('/references')) return Promise.resolve({ registryCode: 'NAE_TREE', entryId: 3, items: [], total: 0 }) as never;
    const detail = /\/entries\/(\d+)$/.exec(path);
    if (detail !== null) return Promise.resolve({ id: Number(detail[1]), code: `T-${String(detail[1])}`, displayL10n: { values: { en: 'N' } }, parentEntryId: null, values: {} }) as never;
    return Promise.resolve([]) as never;
  });
});
afterEach(cleanup);

const mount = () =>
  renderWithQuery(
    <MemoryRouter initialEntries={['/admin/permits/nae?node=3']}>
      <NaeStructurePage />
    </MemoryRouter>,
  );

describe('NaeStructurePage: прокрутка', () => {
  it('дерево й панель вузла лежать у блоці .nae-split з виміряним відступом від верху документа', async () => {
    const view = mount();
    await screen.findByTestId('tree-panel');

    const split = view.container.querySelector<HTMLElement>('.nae-split[data-nae-split]');
    expect(split).not.toBeNull();
    expect(split?.querySelector('.ecr-tree-pane')).not.toBeNull();
    expect(split?.querySelector('.ecr-tree-side')).not.toBeNull();
    // `useSplitTop` ставить змінну, з якої CSS рахує висоту блоку (вікно мінус шапка), без констант.
    expect(split?.style.getPropertyValue(SplitTopVar)).toMatch(/^\d+px$/);
  });

  it('зміна вибраного вузла не перемонтовує панель і дерево: той самий DOM-елемент', async () => {
    const user = userEvent.setup();
    const view = mount();
    await screen.findByTestId('tree-panel');
    const side = view.container.querySelector('.ecr-tree-side');
    const pane = view.container.querySelector('.ecr-tree-pane');
    const viewport = screen.getByTestId('tree-viewport');

    await user.click(await screen.findByRole('treeitem', { name: /Atyrau/ }));
    await screen.findByRole('heading', { name: 'Atyrau' });

    expect(view.container.querySelector('.ecr-tree-side')).toBe(side);
    expect(view.container.querySelector('.ecr-tree-pane')).toBe(pane);
    expect(screen.getByTestId('tree-viewport')).toBe(viewport);
    expect(within(side as HTMLElement).getByTestId('tree-panel')).toBeTruthy();
  });

  it('панель має стабільне місце під атрибути: .nae-panel-extra', async () => {
    const view = mount();
    await screen.findByTestId('tree-panel');

    expect(view.container.querySelector('.ecr-tree-side .nae-panel-extra')).not.toBeNull();
  });
});

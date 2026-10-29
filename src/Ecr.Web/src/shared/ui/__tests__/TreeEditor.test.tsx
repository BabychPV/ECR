import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { TreeEditor, TreeNodePanel, type TreeEditorProps, type TreeLevel, type TreeNode } from '@/shared/ui/TreeEditor';
import { renderWithMantine } from '@/test/render';

/**
 * Компонент редактора дерева: ARIA-розмітка, клавіатура, пошук, виведення, перенос, віртуалізація.
 *
 * ⚠ Каталог рядків у компонентному тесті не завантажений, тож `t()` чесно віддає позначений ключ (`⟦ключ⟧`):
 * підписи кнопок і назви діалогів перевіряються за ключами, а не за англійським текстом.
 */

const levels: TreeLevel[] = [
  { code: 'region', ordinal: 1, name: 'Region', isOptional: false },
  { code: 'area', ordinal: 2, name: 'Area', isOptional: true },
  { code: 'site', ordinal: 3, name: 'Site', isOptional: false },
  { code: 'nae', ordinal: 4, name: 'NAE', isOptional: false },
];

const n = (id: number, parentId: number | null, label: string, levelCode: string, ordinal = 1, extra: Partial<TreeNode> = {}): TreeNode => ({
  id,
  parentId,
  label,
  levelCode,
  ordinal,
  code: `C${String(id)}`,
  ...extra,
});

const sample: TreeNode[] = [
  n(1, null, 'Atyrau', 'region', 1, { childCount: 2 }),
  n(2, 1, 'Offshore', 'area', 1, { childCount: 2 }),
  n(3, 2, 'Barges', 'site', 1, { childCount: 3 }),
  n(4, 3, '1085', 'nae', 1),
  n(5, 3, '1086', 'nae', 2),
  n(6, 3, '1087', 'nae', 3, { retired: true }),
  n(7, 2, 'Island', 'site', 2, { childCount: 0 }),
  n(8, 1, 'Onshore', 'area', 2),
  n(9, null, 'Mangistau', 'region', 2),
];

function setup(props: Partial<TreeEditorProps> = {}): { onSelect: ReturnType<typeof vi.fn> } {
  const onSelect = vi.fn();
  renderWithMantine(
    <TreeEditor label="NAE structure" nodes={sample} levels={levels} selectedId={null} onSelect={onSelect} {...props} />,
  );

  return { onSelect };
}

const items = (): HTMLElement[] => screen.queryAllByRole('treeitem');
const itemByName = (name: string): HTMLElement => {
  const found = items().find((element) => element.textContent?.includes(name) === true);
  if (found === undefined) throw new Error(`Немає рядка «${name}»`);

  return found;
};

afterEach(cleanup);

describe('TreeEditor: розмітка', () => {
  it('role=tree з підписом; рядки мають level/posinset/setsize, expanded - лише у батьків', () => {
    setup();

    expect(screen.getByRole('tree', { name: 'NAE structure' })).toBeTruthy();
    // Корені розгорнуті на старті: Atyrau, Offshore, Onshore, Mangistau (Offshore не розгорнуто)
    const atyrau = itemByName('Atyrau');
    expect(atyrau.getAttribute('aria-level')).toBe('1');
    expect(atyrau.getAttribute('aria-posinset')).toBe('1');
    expect(atyrau.getAttribute('aria-setsize')).toBe('2');
    expect(atyrau.getAttribute('aria-expanded')).toBe('true');

    const offshore = itemByName('Offshore');
    expect(offshore.getAttribute('aria-level')).toBe('2');
    expect(offshore.getAttribute('aria-expanded')).toBe('false');
    expect(itemByName('Onshore').hasAttribute('aria-expanded')).toBe(false);
  });

  it('roving tabindex: рівно один рядок у таб-послідовності', () => {
    setup();

    expect(items().filter((element) => element.tabIndex === 0)).toHaveLength(1);
  });

  it('вибраний вузол: aria-selected=true лише в нього, предки розгорнуті, щоб його було видно', () => {
    setup({ selectedId: 4 });

    expect(itemByName('1085').getAttribute('aria-selected')).toBe('true');
    expect(itemByName('1086').getAttribute('aria-selected')).toBe('false');
  });

  it('виведений вузол: текстова позначка й data-retired, не лише колір', () => {
    setup({ selectedId: 6 });

    const retired = itemByName('1087');
    expect(retired.getAttribute('data-retired')).toBe('true');
    expect(within(retired).getByText('⟦treeEditor.retiredTag⟧')).toBeTruthy();
  });

  it('порожнє дерево й завантаження мають свій текст', () => {
    setup({ nodes: [], loading: true });
    expect(screen.getByTestId('tree-empty').textContent).toBe('⟦treeEditor.loading⟧');
    cleanup();

    setup({ nodes: [] });
    expect(screen.getByTestId('tree-empty').textContent).toBe('⟦treeEditor.empty⟧');
  });
});

describe('TreeEditor: клавіатура', () => {
  it('стрілки рухають фокус, → розгортає, ← згортає, End/Home - краї', async () => {
    const user = userEvent.setup();
    setup();

    itemByName('Atyrau').focus();
    await user.keyboard('{ArrowDown}');
    expect(document.activeElement).toBe(itemByName('Offshore'));

    await user.keyboard('{ArrowRight}');
    expect(itemByName('Offshore').getAttribute('aria-expanded')).toBe('true');
    expect(items().some((element) => element.textContent?.includes('Island') === true)).toBe(true);

    await user.keyboard('{ArrowRight}');
    expect(document.activeElement).toBe(itemByName('Barges'));

    await user.keyboard('{ArrowLeft}');
    expect(document.activeElement).toBe(itemByName('Offshore'));
    await user.keyboard('{ArrowLeft}');
    expect(itemByName('Offshore').getAttribute('aria-expanded')).toBe('false');

    await user.keyboard('{End}');
    expect(document.activeElement).toBe(itemByName('Mangistau'));
    await user.keyboard('{Home}');
    expect(document.activeElement).toBe(itemByName('Atyrau'));
  });

  it('Enter вибирає вузол, клац теж', async () => {
    const user = userEvent.setup();
    const { onSelect } = setup();

    itemByName('Atyrau').focus();
    await user.keyboard('{Enter}');
    expect(onSelect).toHaveBeenLastCalledWith(1);

    await user.click(itemByName('Mangistau'));
    expect(onSelect).toHaveBeenLastCalledWith(9);
  });

  it('набір літер переносить фокус на рядок із цією літерою', async () => {
    const user = userEvent.setup();
    setup();

    itemByName('Atyrau').focus();
    await user.keyboard('m');
    expect(document.activeElement).toBe(itemByName('Mangistau'));
  });

  it('Delete відкриває підтвердження з назвою; підтвердження викликає onRetire з каскадом для вузла з дітьми', async () => {
    const user = userEvent.setup();
    const onRetire = vi.fn().mockResolvedValue(undefined);
    setup({ canEdit: true, onRetire });

    itemByName('Atyrau').focus();
    await user.keyboard('{Delete}');

    // Фокус у діалозі - на «Скасувати» (правило L6), незворотна дія не виконується Enter'ом.
    expect(await screen.findByTestId('confirm-cancel')).toBeTruthy();
    expect(onRetire).not.toHaveBeenCalled();
    await user.click(screen.getByTestId('confirm-verb'));

    await waitFor(() => expect(onRetire).toHaveBeenCalledTimes(1));
    expect(onRetire.mock.calls[0]?.[0]).toMatchObject({ id: 1 });
    expect(onRetire.mock.calls[0]?.[1]).toBe(true);
  });

  it('листок без дітей виводиться без каскаду', async () => {
    const user = userEvent.setup();
    const onRetire = vi.fn();
    setup({ canEdit: true, onRetire, selectedId: 4 });

    itemByName('1085').focus();
    await user.keyboard('{Delete}');
    await user.click(await screen.findByTestId('confirm-verb'));

    await waitFor(() => expect(onRetire).toHaveBeenCalled());
    expect(onRetire.mock.calls[0]?.[1]).toBe(false);
  });

  it('без права змін Delete, Insert і Alt+M нічого не відкривають', async () => {
    const user = userEvent.setup();
    const onAddChild = vi.fn();
    setup({ canEdit: false, onRetire: vi.fn(), onMove: vi.fn(), onAddChild });

    itemByName('Atyrau').focus();
    await user.keyboard('{Delete}{Insert}{Alt>}m{/Alt}');

    expect(screen.queryByTestId('confirm-verb')).toBeNull();
    expect(screen.queryByTestId('tree-move-dialog')).toBeNull();
    expect(onAddChild).not.toHaveBeenCalled();
  });

  it('Insert додає дитину на найближчому дозволеному рівні', async () => {
    const user = userEvent.setup();
    const onAddChild = vi.fn();
    setup({ canEdit: true, onAddChild });

    itemByName('Atyrau').focus();
    await user.keyboard('{Insert}');

    expect(onAddChild).toHaveBeenCalledTimes(1);
    expect(onAddChild.mock.calls[0]?.[0]).toMatchObject({ id: 1 });
    expect(onAddChild.mock.calls[0]?.[1]).toMatchObject({ code: 'area' });
  });

  it('Alt+↓ міняє порядок: onMove з тим самим батьком і сусідом-«перед»', async () => {
    const user = userEvent.setup();
    const onMove = vi.fn().mockResolvedValue(undefined);
    setup({ canEdit: true, onMove, selectedId: 4 });

    itemByName('1085').focus();
    await user.keyboard('{Alt>}{ArrowDown}{/Alt}');

    await waitFor(() => expect(onMove).toHaveBeenCalledTimes(1));
    expect(onMove).toHaveBeenCalledWith({ id: 4, parentId: 3, beforeId: 6 });
    await waitFor(() => expect(screen.getByTestId('tree-status').textContent).toContain('treeEditor.reordered'));
  });
});

describe('TreeEditor: перенос', () => {
  it('Alt+M - діалог із дозволеними місцями; вибір і підтвердження викликають onMove', async () => {
    const user = userEvent.setup();
    const onMove = vi.fn().mockResolvedValue(undefined);
    setup({ canEdit: true, onMove, selectedId: 3 });

    itemByName('Barges').focus();
    await user.keyboard('{Alt>}m{/Alt}');

    const dialog = await screen.findByTestId('tree-move-dialog');
    // Barges (site) може стояти під регіоном і зоною, але не під собою, своїми нащадками й сусідньою «Island» (site).
    const radios = within(dialog).getAllByRole('radio').map((radio) => (radio as HTMLInputElement).labels?.[0]?.textContent ?? '');
    expect(radios.some((text) => text.includes('Atyrau › Onshore'))).toBe(true);
    expect(radios.some((text) => text.includes('Island'))).toBe(false);
    expect(radios.some((text) => text.includes('1085'))).toBe(false);

    const confirm = within(dialog).getByTestId('tree-move-confirm');
    expect((confirm as HTMLButtonElement).disabled).toBe(true);

    await user.click(within(dialog).getByRole('radio', { name: /Atyrau › Onshore/ }));
    await user.click(confirm);

    await waitFor(() => expect(onMove).toHaveBeenCalledWith({ id: 3, parentId: 8, beforeId: null }));
    await waitFor(() => expect(screen.queryByTestId('tree-move-dialog')).toBeNull());
  });

  it('відмова сервера лишає діалог відкритим для повтору', async () => {
    const user = userEvent.setup();
    const onMove = vi.fn().mockRejectedValue(new Error('422'));
    setup({ canEdit: true, onMove, selectedId: 3 });

    itemByName('Barges').focus();
    await user.keyboard('{Alt>}m{/Alt}');
    const dialog = await screen.findByTestId('tree-move-dialog');
    await user.click(within(dialog).getByRole('radio', { name: /Atyrau › Onshore/ }));
    await user.click(within(dialog).getByTestId('tree-move-confirm'));

    await waitFor(() => expect(onMove).toHaveBeenCalled());
    expect(screen.queryByTestId('tree-move-dialog')).not.toBeNull();
  });

  it('кнопка панелі «Перенести» відкриває той самий діалог', async () => {
    const user = userEvent.setup();
    setup({
      canEdit: true,
      onMove: vi.fn(),
      selectedId: 3,
      renderPanel: (node, actions, path) => <TreeNodePanel node={node} path={path} levels={levels} actions={actions} />,
    });

    await user.click(screen.getByTestId('tree-panel-move'));
    expect(await screen.findByTestId('tree-move-dialog')).toBeTruthy();
  });
});

describe('TreeEditor: пошук', () => {
  it('набір відкладається до паузи й віддається в onSearch обрізаним', async () => {
    const user = userEvent.setup();
    const onSearch = vi.fn();
    setup({ onSearch });

    await user.type(screen.getByTestId('tree-search'), ' 108 ');
    expect(onSearch).not.toHaveBeenCalled();

    await waitFor(() => expect(onSearch).toHaveBeenCalledTimes(1), { timeout: 2000 });
    expect(onSearch).toHaveBeenCalledWith('108');
  });

  it('Escape у полі й кнопка × скидають пошук', async () => {
    const user = userEvent.setup();
    const onSearch = vi.fn();
    setup({ onSearch });
    const input = screen.getByTestId<HTMLInputElement>('tree-search');

    await user.type(input, 'x');
    await user.keyboard('{Escape}');
    expect(input.value).toBe('');
    expect(onSearch).toHaveBeenLastCalledWith('');
  });

  it('із застосованим запитом: збіги підсвічені, усе розгорнуте, лічильник у живому регіоні', () => {
    const found: TreeNode[] = [
      n(1, null, 'Atyrau', 'region'),
      n(2, 1, 'Offshore', 'area'),
      n(3, 2, 'Barges', 'site'),
      n(4, 3, '1085', 'nae', 1, { isMatch: true }),
    ];
    setup({ nodes: found, query: '108' });

    // Усі предки збігу видно без жодного розгортання, збіг - у <mark>.
    expect(items()).toHaveLength(4);
    const row = itemByName('1085');
    expect(row.querySelectorAll('mark')).toHaveLength(1); // у назві (код вузла - C4 - не збігається)
    expect(itemByName('Barges').querySelector('mark')).toBeNull();
    expect(screen.getByTestId('tree-status').textContent).toContain('count=1');
  });

  it('порожній результат: «нічого не знайдено» і скидання', async () => {
    const user = userEvent.setup();
    const onSearch = vi.fn();
    setup({ nodes: [], query: 'zzz', onSearch });

    expect(screen.getByTestId('tree-empty').textContent).toContain('⟦treeEditor.nothingFound⟧');
    await user.click(within(screen.getByTestId('tree-empty')).getByRole('button'));
    expect(onSearch).toHaveBeenCalledWith('');
  });

  it('перемикач «Виведені» - aria-pressed', async () => {
    const user = userEvent.setup();
    const onShowRetiredChange = vi.fn();
    setup({ showRetired: false, onShowRetiredChange });

    const toggle = screen.getByTestId('tree-show-retired');
    expect(toggle.getAttribute('aria-pressed')).toBe('false');
    await user.click(toggle);
    expect(onShowRetiredChange).toHaveBeenCalledWith(true);
  });
});

describe('TreeEditor: великі дерева', () => {
  const big: TreeNode[] = Array.from({ length: 3000 }, (_, at) => n(at + 1, null, `N${String(at + 1).padStart(4, '0')}`, 'region', at));

  it('у DOM лише вікно рядків, але setsize правдивий', () => {
    setup({ nodes: big });

    const rendered = items();
    expect(rendered.length).toBeGreaterThan(5);
    expect(rendered.length).toBeLessThan(80);
    expect(rendered[0]?.getAttribute('aria-setsize')).toBe('3000');
  });

  it('End переносить фокус на далекий рядок, якого ще не було в DOM', async () => {
    const user = userEvent.setup();
    setup({ nodes: big });

    items()[0]?.focus();
    await user.keyboard('{End}');

    await waitFor(() => expect(document.activeElement?.getAttribute('aria-posinset')).toBe('3000'));
    expect(document.activeElement?.textContent).toContain('N3000');
  });

  it('активний рядок лишається в DOM і в таб-послідовності, коли його прокрутили геть', async () => {
    const user = userEvent.setup();
    setup({ nodes: big });

    items()[0]?.focus();
    await user.keyboard('{End}');
    await waitFor(() => expect(document.activeElement?.getAttribute('aria-posinset')).toBe('3000'));

    const viewport = screen.getByTestId('tree-viewport');
    viewport.scrollTop = 0;
    viewport.dispatchEvent(new Event('scroll'));

    await waitFor(() => expect(items().filter((element) => element.tabIndex === 0)).toHaveLength(1));
    expect(items().find((element) => element.tabIndex === 0)?.textContent).toContain('N3000');
  });
});

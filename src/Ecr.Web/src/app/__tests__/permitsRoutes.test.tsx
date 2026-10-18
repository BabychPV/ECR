import { describe, expect, it, vi } from 'vitest';
import { render } from '@testing-library/react';
import { QueryClient } from '@tanstack/react-query';
import { matchRoutes } from 'react-router-dom';
import { buildCrumbChain, crumbGroupLabelKey, type CrumbMatch } from '@/app/Breadcrumbs';
import { childPath, navGroups, navRoutes, routeList, routes, type RouteEntry } from '@/app/routes';
import { PermitsPaths } from '@/features/permits/shell/permitPaths';
import { PermitTypesPermission } from '@/features/permits/permitRights';
import { NavIcon } from '@/shared/ui/navIcons';

/**
 * Маршрути Permits (PM-06): статичні підсторінки мусять вигравати в `/admin/permits/:id`, дерево
 * крихт — Configure › Permits › …, пункт меню — один, одразу після Registries.
 *
 * Мутаційний доказ (вручну, відновлено): шлях `adminPermitLists` змінений на `/admin/permits/:code`
 * валив «статичні маршрути виграють» і «унікальні шляхи»; прибраний `'admin-permit-types'` з
 * `ancestorIds` редактора типу валив ланцюжок крихт.
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string) => key,
}));

const permitRoutes: RouteEntry[] = routeList.filter((route) => route.path.startsWith('/admin/permits'));

function resolve(pathname: string, entries: readonly RouteEntry[] = permitRoutes): string | null {
  const tree = entries.map((entry) => ({ path: `/${childPath(entry)}`, id: entry.id }));
  const hit = matchRoutes(tree, pathname);
  return hit === null ? null : (hit[hit.length - 1]?.route.id ?? null);
}

describe('Permits: маршрути', () => {
  it('у реєстрі рівно сім маршрутів Permits', () => {
    expect(permitRoutes.map((route) => route.path)).toEqual([
      '/admin/permits',
      '/admin/permits/types',
      '/admin/permits/types/:code',
      '/admin/permits/lists',
      '/admin/permits/bindings',
      '/admin/permits/bindings/:id',
      '/admin/permits/:id',
    ]);
  });

  it.each([
    ['/admin/permits', 'admin-permits'],
    ['/admin/permits/types', 'admin-permit-types'],
    ['/admin/permits/types/WaterDischarge', 'admin-permit-type-editor'],
    ['/admin/permits/lists', 'admin-permit-lists'],
    ['/admin/permits/bindings', 'admin-permit-bindings'],
    ['/admin/permits/bindings/B-01', 'admin-permit-binding'],
    ['/admin/permits/P-0001', 'admin-permit'],
    ['/admin/permits/typesx', 'admin-permit'],
  ])('%s → %s', (pathname, id) => {
    expect(resolve(pathname)).toBe(id);
  });

  it('статичні маршрути виграють у `:id` за будь-якого порядку оголошення', () => {
    const reversed = [...permitRoutes].reverse();
    for (const pathname of ['/admin/permits/types', '/admin/permits/lists', '/admin/permits/bindings']) {
      expect(resolve(pathname, reversed)).not.toBe('admin-permit');
      expect(resolve(pathname, reversed)).toBe(resolve(pathname));
    }
    expect(resolve('/admin/permits/P-0001', reversed)).toBe('admin-permit');
  });

  it('адреси смуги розділів у features збігаються з реєстром маршрутів', () => {
    expect(PermitsPaths).toEqual({
      permits: routes.adminPermits.path,
      types: routes.adminPermitTypes.path,
      lists: routes.adminPermitLists.path,
      bindings: routes.adminPermitBindings.path,
    });
  });

  it('читання охоронене правом Registry.View, а типи й їхній редактор — Registry.EditDefinition (PM-06b)', () => {
    expect(permitRoutes.map((route) => [route.id, route.handle.permission])).toEqual([
      ['admin-permits', 'Registry.View'],
      ['admin-permit-types', PermitTypesPermission],
      ['admin-permit-type-editor', PermitTypesPermission],
      ['admin-permit-lists', 'Registry.View'],
      ['admin-permit-bindings', 'Registry.View'],
      ['admin-permit-binding', 'Registry.View'],
      ['admin-permit', 'Registry.View'],
    ]);
  });

  it('право посилання на типи у смузі розділів збігається з правом маршруту', () => {
    expect(PermitTypesPermission).toBe(routes.adminPermitTypes.handle.permission);
    expect(PermitTypesPermission).toBe(routes.adminPermitTypeEditor.handle.permission);
  });

  it('у меню лише «Permits», і він стоїть у Configure одразу після Registries', () => {
    expect(navRoutes.filter((route) => route.path.startsWith('/admin/permits')).map((r) => r.id)).toEqual(['admin-permits']);

    const configure = navGroups.find((group) => group.id === 'configure')!;
    const ids = configure.routes.map((route) => route.id);
    expect(ids.indexOf('admin-permits')).toBe(ids.indexOf('admin-registries') + 1);
  });

  it('іконка пункту `permits` існує й малюється', () => {
    expect(routes.adminPermits.handle.icon).toBe('permits');
    const { container } = render(<NavIcon name={routes.adminPermits.handle.icon} />);
    expect(container.querySelector('svg')).not.toBeNull();
  });

  it('пояснення є в чотирьох розділів (список, типи, списки, привʼязки), решти — немає', () => {
    expect(
      permitRoutes.filter((route) => route.handle.descriptionKey !== undefined).map((route) => route.path),
    ).toEqual(['/admin/permits', '/admin/permits/types', '/admin/permits/lists', '/admin/permits/bindings']);
  });

  it('предки крихт існують у реєстрі', () => {
    const ids = new Set(routeList.map((route) => route.id));
    for (const route of permitRoutes) {
      for (const ancestor of route.handle.crumb?.ancestorIds ?? []) expect(ids.has(ancestor)).toBe(true);
    }
  });

  function chainFor(entry: RouteEntry, pathname: string, params: Record<string, string>) {
    const matches: CrumbMatch[] = [{ pathname, params, handle: entry.handle }];
    return {
      chain: buildCrumbChain(matches, new QueryClient()),
      group: crumbGroupLabelKey(matches),
    };
  }

  it('крихти: Configure › Permits › Permit types › Permit type', () => {
    const { chain, group } = chainFor(routes.adminPermitTypeEditor, '/admin/permits/types/WaterDischarge', {
      code: 'WaterDischarge',
    });
    expect(group).toBe('nav.group.configure');
    expect(chain.map((c) => c.text)).toEqual(['nav.permits', 'permits.types.title', 'permits.typeEditor.title']);
    expect(chain.map((c) => c.href)).toEqual(['/admin/permits', '/admin/permits/types', undefined]);
  });

  it('крихти: Configure › Permits › Sheet bindings › Sheet binding', () => {
    const { chain } = chainFor(routes.adminPermitBinding, '/admin/permits/bindings/B-01', { id: 'B-01' });
    expect(chain.map((c) => c.text)).toEqual(['nav.permits', 'permits.bindings.title', 'permits.binding.title']);
    expect(chain.map((c) => c.href)).toEqual(['/admin/permits', '/admin/permits/bindings', undefined]);
  });

  it('крихти: Configure › Permits › Permit, і розділи — Permits › Lists', () => {
    const permit = chainFor(routes.adminPermit, '/admin/permits/P-0001', { id: 'P-0001' });
    expect(permit.chain.map((c) => c.text)).toEqual(['nav.permits', 'permits.detail.title']);
    expect(permit.group).toBe('nav.group.configure');

    const lists = chainFor(routes.adminPermitLists, '/admin/permits/lists', {});
    expect(lists.chain.map((c) => c.text)).toEqual(['nav.permits', 'permits.lists.title']);
  });
});

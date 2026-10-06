import { describe, expect, it } from 'vitest';
import type { CurrentUserDto } from '@/api/types';
import { canAccessRoute, missingRoutePermission } from '@/app/routeAccess';
import { routes } from '@/app/routes';

function meWith(permissions: string[]): CurrentUserDto {
  return {
    userId: 1,
    userName: 'tester',
    language: 'en',
    permissions,
    denies: [],
    grants: {},
    isSimulation: false,
    simulatedForUserId: null,
    mustChangePassword: false,
  };
}

describe('canAccessRoute — «permission АБО будь-яке з alsoPermittedBy»', () => {
  const handle = { labelKey: 'x', permission: 'A.View', alsoPermittedBy: ['A.Manage', 'A.Own'] } as const;

  it.each([
    [['A.View'], true],
    [['A.Manage'], true],
    [['A.Own'], true],
    [['A.View', 'A.Manage'], true],
    [['B.View'], false],
    [[], false],
  ])('права %j → %s', (permissions, expected) => {
    expect(canAccessRoute(meWith(permissions), handle)).toBe(expected);
  });

  it('без permission маршрут відкритий навіть без профілю; з правом — зачинений без профілю', () => {
    expect(canAccessRoute(undefined, { labelKey: 'x' })).toBe(true);
    expect(canAccessRoute(undefined, handle)).toBe(false);
  });

  it('alsoPermittedBy без permission нічого не зачиняє', () => {
    expect(canAccessRoute(meWith([]), { labelKey: 'x', alsoPermittedBy: ['A.Manage'] })).toBe(true);
  });

  it('реєстр: /admin/sources — View або Manage; /admin/mapping — лише Manage', () => {
    expect(canAccessRoute(meWith(['Integration.View']), routes.adminSources.handle)).toBe(true);
    expect(canAccessRoute(meWith(['Integration.Manage']), routes.adminSources.handle)).toBe(true);
    expect(canAccessRoute(meWith([]), routes.adminSources.handle)).toBe(false);
    expect(canAccessRoute(meWith(['Integration.View']), routes.adminMapping.handle)).toBe(false);
  });
});

describe('canAccessRoute — «… І кожне з alsoRequires» (A2)', () => {
  const handle = {
    labelKey: 'x',
    permission: 'A.View',
    alsoPermittedBy: ['A.Manage'],
    alsoRequires: ['B.View', 'C.View'],
  } as const;

  it.each([
    [['A.View', 'B.View', 'C.View'], true, undefined],
    [['A.Manage', 'B.View', 'C.View'], true, undefined],
    [['A.View', 'B.View'], false, 'C.View'],
    [['A.View'], false, 'B.View'],
    [['B.View', 'C.View'], false, 'A.View'],
    [[], false, 'A.View'],
  ])('права %j → %s, бракує %s', (permissions, expected, missing) => {
    expect(canAccessRoute(meWith(permissions), handle)).toBe(expected);
    expect(missingRoutePermission(meWith(permissions), handle)).toBe(missing);
  });

  it('alsoRequires без permission нічого не зачиняє', () => {
    expect(canAccessRoute(meWith([]), { labelKey: 'x', alsoRequires: ['B.View'] })).toBe(true);
  });

  // ⛔ Права ролей — з `09-seed.sql`: `Viewer` і `Approver` мають
  // `Calculation.View`, але не `Template.View`, а сторінка виразів на старті
  // читає перелік шаблонів — сервер відповідав їм 403, хоч пункт був у меню.
  it('реєстр: /admin/expressions — Calculation.View І Template.View', () => {
    expect(canAccessRoute(meWith(['Calculation.View', 'Document.View']), routes.adminExpressions.handle)).toBe(false);
    expect(missingRoutePermission(meWith(['Calculation.View']), routes.adminExpressions.handle)).toBe('Template.View');
    expect(canAccessRoute(meWith(['Calculation.View', 'Template.View']), routes.adminExpressions.handle)).toBe(true);
    expect(canAccessRoute(meWith(['Template.View']), routes.adminExpressions.handle)).toBe(false);
  });
});

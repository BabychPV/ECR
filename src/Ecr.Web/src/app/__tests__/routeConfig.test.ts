import { describe, expect, it } from 'vitest';
import { can } from '@/shared/session/useSession';
import { childPath, navRoutes, routeList, routes } from '@/app/routes';

/**
 * Реєстр маршрутів (`PR nav-arch #1`).
 *
 * ⛔ Це та сама дисципліна, що й у `queryKeys.test.ts`: перевіряється не
 * «функція щось повертає», а інваріант, який справді захищає від регресу —
 * унікальність `path`/`id` (два записи з однаковим шляхом означали б, що
 * навбар чи `router.tsx` посилаються не туди, куди думають), і що
 * фільтрація навбару за правом дає той самий результат, що й стара логіка
 * `AppLayout.tsx` (`item.permission === undefined || can(me, item.permission)`).
 *
 * Мутаційна перевірка (RED → GREEN, вручну): дублювання шляху
 * (`adminUnits.path` тимчасово змінено на `'/admin/templates'`) валило
 * «шляхи маршрутів унікальні»; порожній рядок замість `permission` на
 * записі з правом валив «readerWithoutPermission»-перевірку нижче. Обидва
 * відновлено після підтвердження RED.
 */
describe('app/routes — реєстр маршрутів', () => {
  it('шляхи маршрутів унікальні', () => {
    const paths = routeList.map((route) => route.path);
    const unique = new Set(paths);

    expect(unique.size, 'два записи реєстру ведуть на ту саму адресу').toBe(paths.length);
  });

  it('ідентифікатори записів унікальні', () => {
    const ids = routeList.map((route) => route.id);
    const unique = new Set(ids);

    expect(unique.size, 'два записи реєстру мають той самий id').toBe(ids.length);
  });

  it('childPath знімає провідний "/", а корінь дає порожній сегмент дитини', () => {
    expect(childPath(routes.adminTemplates)).toBe('admin/templates');
    expect(childPath(routes.adminTemplateVersion)).toBe(
      'admin/templates/:id/versions/:versionId',
    );
    expect(childPath(routes.home)).toBe('');
  });

  it('navRoutes — підмножина реєстру, точно ті записи, де showInNav: true', () => {
    const expectedNavIds = routeList
      .filter((route) => route.showInNav === true)
      .map((route) => route.id);

    expect(navRoutes.map((route) => route.id)).toEqual(expectedNavIds);

    // ⛔ Жоден запис поза `showInNav: true` не повинен просочитися в навбар:
    // глибокі маршрути (версія шаблону, зв'язки таблиць, конструктор
    // довідника…) не мають своєї кнопки в навбарі — лише посилання зсередини
    // відповідної сторінки.
    for (const route of navRoutes) {
      expect(route.showInNav).toBe(true);
    }
  });

  it('пункт навбару без permission доступний усім, пункт із permission — лише за правом (той самий фільтр, що й старий AppLayout.tsx)', () => {
    const withoutPermission = navRoutes.filter((route) => route.handle.permission === undefined);
    const withPermission = navRoutes.filter((route) => route.handle.permission !== undefined);

    // ⚠ `/` (документи) і `/my-groups` — єдині два пункти навбару БЕЗ права
    // (`H-21`): решта захищені відповідним правом. Змінити цю кількість,
    // не помітивши, означало б випадково відкрити чи закрити пункт меню.
    expect(withoutPermission.map((r) => r.id).sort()).toEqual(['home', 'my-groups'].sort());
    expect(withPermission.length).toBe(navRoutes.length - withoutPermission.length);

    const anonymous = undefined;

    for (const route of withoutPermission) {
      expect(
        route.handle.permission === undefined || can(anonymous, route.handle.permission),
      ).toBe(true);
    }

    for (const route of withPermission) {
      // Профіль без жодного права не бачить пункт, що вимагає права.
      expect(can(anonymous, route.handle.permission as string)).toBe(false);
    }
  });
});

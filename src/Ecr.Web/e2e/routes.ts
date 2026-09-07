/**
 * Маршрути застосунку, які знімаються (`D-142`).
 *
 * ⛔ Перелік лежить ОКРЕМИМ модулем, а не всередині `screenshots.spec.ts`, бо
 * споживачів у нього тепер два: знімки в браузері (потребують стенда) і сторож
 * дрейфу `src/app/__tests__/routes.test.ts` (виконується під vitest без
 * браузера). Друга копія переліку розійшлася б із першою рівно тоді, коли
 * додали маршрут, — тобто в єдиний момент, заради якого сторож і написаний.
 *
 * ⛔ Модуль навмисно НЕ імпортує `@playwright/test` і взагалі нічого. Його
 * читає vitest, а будь-яка згадка Playwright у ланцюгу імпортів завалила б
 * прогін чужою помилкою («Playwright Test did not expect test() to be called
 * here») — рівно тим падінням, через яке `e2e/**` виключений із vitest
 * (`vite.config.ts`).
 *
 * ⚠ Перелік узгоджений із `router.tsx` руками — і це його слабке місце:
 * доданий маршрут сюди не потрапить сам. Автоматичного джерела немає, бо
 * маршрути оголошені всередині JSX; тому поруч стоїть сторож, який падає,
 * щойно в роутері з'явився шлях, якого тут немає.
 */
export const Routes = [
  { path: '/', name: 'documents' },
  { path: '/my-groups', name: 'my-groups' },
  { path: '/admin/templates', name: 'templates' },
  { path: '/admin/registries', name: 'registries' },
  { path: '/admin/methodologies', name: 'methodologies' },
  { path: '/admin/expressions', name: 'expressions' },
  { path: '/admin/units', name: 'units' },
  { path: '/admin/security', name: 'security' },
  { path: '/admin/periods', name: 'periods' },
  { path: '/admin/sources', name: 'sources' },
  { path: '/admin/mapping', name: 'mapping' },
  { path: '/admin/jobs', name: 'jobs' },
  { path: '/admin/health', name: 'health' },
  { path: '/admin/snapshots', name: 'snapshots' },
  { path: '/admin/audit', name: 'audit' },
  { path: '/admin/ui-strings', name: 'ui-strings' },
];

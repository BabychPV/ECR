import { describe, expect, it } from 'vitest';
import { matchRoutes } from 'react-router-dom';
import { router } from '@/app/router';

/**
 * `ФВ-14.3`: склад інтерфейсу — 15 функціональних областей (перелік у
 * `docs/tz/02-requirements.md`, таблиця під вимогою; деталізація — `B21` §2).
 *
 * ⚠ «Область є» тут означає рівно одне перевірюване: у РЕАЛЬНОМУ дереві
 * `router.tsx` (не в копії) адреса області знаходить власний маршрут, а не
 * пастку `*` (`NotFoundPage`). Це не доводить, що екран повний, — лише що до
 * нього можна дійти. Повнота кожної області — предмет її власних вимог.
 *
 * ⚠ Деякі області ділять екран — так задумано, не підгонка:
 *  - 6 «Workflow, імпорт/експорт, i18n» — переклади на `/admin/ui-strings`,
 *    а подання/погодження й імпорт/експорт живуть на сторінці документа;
 *  - 13 «Розклад збору» — вкладка розкладу на екрані джерел
 *    (`CollectionScheduleTab.tsx`, `DataSourceScheduleTab.tsx`);
 *  - 15 «Операційний дашборд» — стан (`/admin/health`) і консистентність
 *    (`/admin/consistency`).
 *
 * ⛔ Область 9 «Редактор конвеєра» екрана НЕ МАЄ (`DIRECTIVE-14.md` T-03).
 * Тест це фіксує явно, а не мовчки пропускає рядок: коли екран з'явиться,
 * останнє твердження почервоніє — і область треба перенести в таблицю.
 */
const areas: readonly (readonly [number, string, readonly string[]])[] = [
  [1, 'Grid-редактор', ['/documents/1']],
  [2, 'Конструктор шаблону', ['/admin/templates/1/versions/2']],
  [3, 'Конструктор реєстрів', ['/admin/registries/EQUIP/definition']],
  [4, 'Конфігуратор методологій', ['/admin/methodologies/1/versions']],
  [5, 'Безпека: ролі, матриця, ефективні права', ['/admin/security']],
  [6, 'Workflow, імпорт/експорт, i18n', ['/documents/1', '/admin/ui-strings']],
  [7, "Редактор зв'язків таблиць", ['/admin/templates/1/versions/2/relations']],
  [8, 'Конфігуратор джерел', ['/admin/sources']],
  [10, 'Редактор виразів', ['/admin/expressions']],
  [11, "Правила прив'язки", ['/admin/mapping']],
  [12, 'Періоди і матриця доступу', ['/admin/periods']],
  [13, 'Розклад збору', ['/admin/sources']],
  [14, 'Перерахунок: запуск, черга, прогрес', ['/admin/jobs']],
  [15, 'Операційний дашборд', ['/admin/health', '/admin/consistency']],
];

/** Шлях листового маршруту, який знаходить адреса; `null` — жодного. */
function leafPath(url: string): string | null {
  const matches = matchRoutes(router.routes, url);
  const leaf = matches?.[matches.length - 1];

  return leaf?.route.path ?? null;
}

describe('Склад інтерфейсу: функціональні області мають маршрут', () => {
  it('ФВ-14.3: кожна реалізована область досяжна за адресою, а не падає в NotFound', () => {
    const unreachable = areas.flatMap(([number, name, urls]) =>
      urls
        .filter((url) => {
          const path = leafPath(url);

          return path === null || path === '*';
        })
        .map((url) => `${String(number)} ${name}: ${url}`),
    );

    expect(unreachable).toEqual([]);

    // ⚠ Контроль самого методу: невідома адреса справді падає в `*`. Без
    // цього «жодна область не впала в NotFound» могло б означати, що пастки
    // в дереві немає зовсім і перевірка нічого не розрізняє.
    expect(leafPath('/admin/no-such-screen')).toBe('*');

    // 14 з 15: область 9 (редактор конвеєра) відсутня — див. коментар угорі.
    expect(new Set(areas.map(([number]) => number)).size).toBe(14);
    expect(leafPath('/admin/pipeline')).toBe('*');
  });
});

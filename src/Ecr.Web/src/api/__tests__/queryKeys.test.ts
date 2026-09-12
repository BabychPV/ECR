import { describe, expect, it } from 'vitest';
import { queryKeys } from '@/api/queryKeys';

/**
 * Фабрика ключів TanStack Query (`PR nav-arch #1`).
 *
 * ⛔ Доказ тут — не «ключі повертаються» (це довів би зелений тест на
 * наївній, неправильній фабриці так само), а що ІЄРАРХІЯ ключів справді
 * працює так, як її читає `invalidateQueries`: `queryKey` — префіксний
 * збіг за елементами масиву (`partialMatchKey` TanStack Query), а не
 * рівність рядка цілком. {@link isPrefixOf} нижче відтворює РІВНО це
 * правило локально, без залежності від внутрішньої реалізації бібліотеки,
 * і саме ним звіряється очікувана поведінка: чи справді `templates.all()`
 * зачепить усе під доменом, і чи справді вузький `templates.version(id)` НЕ
 * зачепить сусідню версію чи сусідній домен.
 *
 * Мутаційна перевірка (RED → GREEN) проведена вручну: `templates.version` і
 * `templates.versionsOf` тимчасово зведено до одного й того самого масиву
 * (`['templates', id]` для обох) — «Ключі різних сутностей…» і тест
 * ієрархії нижче червоніли (перший — колізія `version(5)` проти
 * `versionsOf(5)`; другий — `allVersionsOf()` став префіксом `version(id)`,
 * чого бути не повинно), після повернення справжньої фабрики — знову
 * зелені. Форму факторки відновлено; сама перевірка лишається в цьому
 * файлі як інваріант, а не одноразовий прогін.
 */

/** Те саме правило, за яким `queryClient.invalidateQueries({queryKey})` зіставляє записи кешу. */
function isPrefixOf(prefix: readonly unknown[], key: readonly unknown[]): boolean {
  if (prefix.length > key.length) return false;

  return prefix.every((value, index) => Object.is(value, key[index]));
}

describe('queryKeys — фабрика ключів TanStack Query', () => {
  it('ключі різних сутностей і різних доменів не збігаються (колізія кешу)', () => {
    // ⚠ Навмисно беруться ОДНАКОВІ числові аргументи (5) для сутностей
    // різної природи — саме такий збіг найлегше не помітити в наївній
    // фабриці («один параметр — один патерн масиву»), і саме він ламає
    // інвалідацію мовчки: запис одного кешу підмінює інший.
    const sample = 5;
    const keys: unknown[][] = [
      [...queryKeys.templates.all()],
      [...queryKeys.templates.list()],
      [...queryKeys.templates.versionsOf(sample)],
      [...queryKeys.templates.allVersionsOf()],
      [...queryKeys.templates.version(sample)],
      [...queryKeys.templates.versionDiff(sample, sample + 1)],
      [...queryKeys.templates.accessMatrix(sample)],
      [...queryKeys.registries.all()],
      [...queryKeys.registries.list()],
      [...queryKeys.registries.entries('CODE')],
      [...queryKeys.registries.definition('CODE')],
      [...queryKeys.registries.history('CODE')],
      [...queryKeys.methodologies.all()],
      [...queryKeys.methodologies.list()],
      [...queryKeys.methodologies.versionsOf(sample)],
      [...queryKeys.methodologies.allVersionsOf()],
      [...queryKeys.methodologies.formulas(sample)],
      [...queryKeys.methodologies.allFormulas()],
      [...queryKeys.methodologies.constants(sample)],
      [...queryKeys.methodologies.rules(sample)],
      [...queryKeys.methodologies.outputs(sample)],
      [...queryKeys.methodologies.tests(sample)],
      [...queryKeys.methodologies.bindings(sample)],
    ];

    const serialized = keys.map((key) => JSON.stringify(key));
    const unique = new Set(serialized);

    expect(unique.size, 'два різні виклики фабрики дали ідентичний ключ кешу').toBe(
      serialized.length,
    );
  });

  it('templates.all() — префікс УСЬОГО домену templates (широка інвалідація зачіпає вузькі ключі)', () => {
    const all = queryKeys.templates.all();
    const narrow = [
      queryKeys.templates.list(),
      queryKeys.templates.versionsOf(1),
      queryKeys.templates.version(1),
      queryKeys.templates.versionDiff(1, 2),
      queryKeys.templates.accessMatrix(1),
    ];

    for (const key of narrow) {
      expect(isPrefixOf(all, key), `templates.all() не є префіксом ${JSON.stringify(key)}`).toBe(
        true,
      );
    }
  });

  it('templates.allVersionsOf() — префікс versionsOf(*) для БУДЬ-ЯКОГО templateId, і НЕ зачіпає version() однієї конкретної версії', () => {
    const allVersionsOf = queryKeys.templates.allVersionsOf();

    expect(isPrefixOf(allVersionsOf, queryKeys.templates.versionsOf(1))).toBe(true);
    expect(isPrefixOf(allVersionsOf, queryKeys.templates.versionsOf(999))).toBe(true);

    // ⛔ Це РІВНО той інваріант, що ламався в наївній фабриці (RED вище):
    // «перелік версій шаблону» і «одна версія» — різні сутності, і широка
    // інвалідація першої не повинна зачепити кеш другої.
    expect(isPrefixOf(allVersionsOf, queryKeys.templates.version(1))).toBe(false);
  });

  it('registries.all() і methodologies.all() — той самий інваріант широкої інвалідації', () => {
    expect(isPrefixOf(queryKeys.registries.all(), queryKeys.registries.entries('X'))).toBe(true);
    expect(isPrefixOf(queryKeys.registries.all(), queryKeys.registries.definition('X'))).toBe(
      true,
    );
    expect(isPrefixOf(queryKeys.registries.all(), queryKeys.registries.history('X'))).toBe(true);

    expect(isPrefixOf(queryKeys.methodologies.all(), queryKeys.methodologies.versionsOf(1))).toBe(
      true,
    );
    expect(isPrefixOf(queryKeys.methodologies.all(), queryKeys.methodologies.bindings(1))).toBe(
      true,
    );
    expect(isPrefixOf(queryKeys.methodologies.all(), queryKeys.methodologies.formulas(1))).toBe(
      true,
    );
  });

  it('вузький ключ (version, entries, bindings…) не є префіксом сусіднього домену чи сусідньої сутності', () => {
    // ⚠ Зворотний бік попередніх перевірок: вузький ключ не повинен випадково
    // зачепити чужий кеш через невдалий спільний префікс.
    expect(isPrefixOf(queryKeys.templates.version(1), queryKeys.templates.versionsOf(1))).toBe(
      false,
    );
    expect(isPrefixOf(queryKeys.registries.entries('X'), queryKeys.methodologies.all())).toBe(
      false,
    );
  });

  it('formulas(undefined) і versionsOf(undefined) лишаються стабільним ключем, а не порожнім масивом', () => {
    // ⚠ Обидва домени приймають `undefined` (версія/шаблон ще не обрані на
    // екрані) — запит вимкнений (`enabled`), але ключ мусить лишатися
    // структурно стабільним між рендерами, інакше TanStack Query завів би
    // новий запис кешу на кожен рендер компонента.
    expect(queryKeys.methodologies.formulas(undefined)).toEqual(['methodologies', 'formulas', undefined]);
    expect(queryKeys.templates.versionsOf(undefined)).toEqual(['templates', 'versionsOf', undefined]);
  });
});

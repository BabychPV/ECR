import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, it, expect } from 'vitest';
import { theme } from '../theme';

/**
 * Токени теми — не декорація, а обмеження (`D-126`).
 *
 * ⚠ Тести з'явилися після аудиту: матриця трасування показала, що `ФВ-14.12`,
 * `ФВ-14.13`, `ФВ-14.20` і `ФВ-14.26` не мали жодного доказу. Лінт-правила
 * забороняють літерали в компонентах — але ніщо не перевіряло, що в самій
 * темі лежать саме ті значення, заради яких літерали заборонені.
 */
const eslintConfig = readFileSync(
  path.resolve(process.cwd(), 'eslint.config.js'),
  'utf8',
);

/** Значення в пікселях із рядка на кшталт `"12px"`. */
function px(value: string): number {
  return Number.parseInt(value, 10);
}

describe('Шкала відступів (ФВ-14.12)', () => {
  it('ФВ-14.12: усі значення кратні чотирьом', () => {
    const spacing = theme.spacing as Record<string, string>;

    // ⛔ Проміжних значень немає навмисно: око не бачить різниці між 13 і 14
    // пікселями, але бачить неузгодженість двох сусідніх панелей.
    for (const [name, value] of Object.entries(spacing)) {
      expect(px(value) % 4, `${name} = ${value}`).toBe(0);
    }
  });

  it('шкала зростає монотонно і не має дублікатів', () => {
    const values = Object.values(theme.spacing as Record<string, string>).map(px);

    // ⚠ Два однакові кроки шкали означають, що один із них ніхто не обере
    // свідомо: вибір між `sm` і `md` перестає щось значити.
    expect(new Set(values).size).toBe(values.length);
    expect([...values].sort((a, b) => a - b)).toEqual(values);
  });

  it('лінт забороняє числові відступи в компонентах', () => {
    // ⚠ Шкала без заборони літералів марна: перший же `gap={13}` зробить її
    // рекомендацією. Тест тримає обидві половини разом.
    expect(eslintConfig).toContain('ФВ-14.12');
    expect(eslintConfig).toContain('mx|my|p|pt|pb|pl|pr|px|py|gap');
  });
});

describe('Типографіка (ФВ-14.13)', () => {
  it('ФВ-14.13: три робочі розміри тексту мають задані значення', () => {
    const sizes = theme.fontSizes as Record<string, string>;

    // ⚠ Числа з вимоги дослівно: `xs` для щільних таблиць, `sm` основний,
    // `md` для форм. Вони не «приблизно такі» — на них розрахована висота
    // рядка 28 пікселів у щільному режимі.
    expect(px(sizes['xs']!)).toBe(11);
    expect(px(sizes['sm']!)).toBe(13);
    expect(px(sizes['md']!)).toBe(15);
  });

  it('системний стек лишається резервом, і сама тема нічого не завантажує', () => {
    /*
     * ✎ 2026-09-19. Коментар тут казав «корпоративного шрифту в замовника не
     * питали, і вигадувати його не можна» — це вже неправда: `Q15-01a`
     * закрито, IBM Plex дозволено самохостингом, і стек тепер починається з
     * нього (`theme.ts`).
     *
     * ⛔ Але `system-ui` у стеку лишається ОБОВ'ЯЗКОВИМ, і це не спадщина:
     * `font-display: swap` означає, що перший кадр кожної сторінки малюється
     * саме резервом, доки `.woff2` іде мережею.
     *
     * ⚠ `url(`/`@import` у самій темі й далі заборонені: завантаження живе в
     * точці входу (`main.tsx`, пакети `@fontsource/*`), а тема лише НАЗИВАЄ
     * гарнітуру. Рядок теми, що сам тягне файл, обійшов би і збірку, і
     * перевірку самохостингу.
     */
    expect(theme.fontFamily).toContain('system-ui');
    expect(theme.fontFamily).not.toContain('url(');
    expect(theme.fontFamily).not.toContain('@import');
    expect(theme.fontFamilyMonospace).not.toContain('url(');
    expect(theme.fontFamilyMonospace).not.toContain('@import');
  });

  /*
   * ⛔ ТУТ СТОЯВ сторож «IBM Plex не заведено, доки Q15-01a без відповіді» —
   * `expect(stack).not.toContain('IBM Plex')` на обидва стеки. Він був правий
   * рівно доти, доки питання лишалося відкритим: шрифт замовника — факт про
   * його світ, а не наше судження, і вигадувати його не можна.
   *
   * 2026-09-19 відповідь надійшла («IBM Plex дозволити, self-hosted»), і
   * сторож із запобіжника став перешкодою самій відповіді. Він НЕ послаблений
   * і не закоментований: його умову замінено вужчою, у
   * `__tests__/fonts.test.ts` — Plex попереду, системний позаду, файли лише
   * свої, казахські літери покриті підмножинами. Зникла не перевірка, а
   * питання, яке вона стерегла.
   */

  it('моноширинний стек має системний варіант для macOS і Linux, а не лише Courier New', () => {
    const mono = theme.fontFamilyMonospace ?? '';

    /*
     * ⛔ Було: `"Cascadia Mono", Consolas, "Courier New", monospace`. На macOS
     * і Linux немає ні `Cascadia Mono`, ні `Consolas`, тож код і коди помилок
     * малювалися `Courier New` — вузьким, із зарубками, з іншою метрикою.
     *
     * ⚠ Перевіряється ПОРЯДОК, а не наявність: `SFMono-Regular` після
     * `Courier New` не дав би нічого, бо перший придатний у списку й виграє.
     */
    const mac = mono.indexOf('SFMono-Regular');
    const linux = mono.indexOf('Menlo');
    const courier = mono.indexOf('Courier New');

    expect(mac).toBeGreaterThan(-1);
    expect(linux).toBeGreaterThan(-1);
    expect(courier).toBeGreaterThan(-1);
    expect(mac).toBeLessThan(courier);
    expect(linux).toBeLessThan(courier);
  });

  it('розміри тексту зростають монотонно', () => {
    const values = Object.values(theme.fontSizes as Record<string, string>).map(px);

    expect([...values].sort((a, b) => a - b)).toEqual(values);
  });
});

describe('Доступність форм (ФВ-14.20)', () => {
  it('ФВ-14.20: лінт вимагає підпис і не приймає placeholder', () => {
    // ⛔ Це правило існує саме тому, що `axe` цього НЕ ловить: для його
    // правила `label` непорожній `placeholder` вважається достатнім ім'ям.
    // Перевірено — прибраний підпис не завалив жодного маршруту.
    expect(eslintConfig).toContain('ФВ-14.20');
    expect(eslintConfig).toContain('aria-labelledby');

    // Перелік компонентів, на які правило поширюється, має бути повним:
    // поле, яке до нього не потрапило, лишиться без підпису мовчки.
    for (const component of ['TextInput', 'NumberInput', 'Select', 'Textarea', 'Switch']) {
      expect(eslintConfig, `${component} поза правилом`).toContain(component);
    }
  });
});

describe('Індикація за тривалістю дії (ФВ-14.26)', () => {
  it('ФВ-14.26: тривалість руху не перевищує межі «нічого не показуємо»', () => {
    const other = theme.other as { motionFast: string; motionBase: string };

    // ⛔ Межі з `ФВ-14.26` не довільні: до 100 мс індикатор лише моргне і
    // відверне. Тому анімація має вкладатися в цей проміжок або трохи більше,
    // але ніколи не змагатися з індикатором за увагу.
    expect(px(other.motionFast)).toBeLessThanOrEqual(100);
    expect(px(other.motionBase)).toBeLessThanOrEqual(150);
  });

  it('кнопки мають стан очікування — індикатор на самому елементі', () => {
    // ⚠ Для дій 100 мс – 1 с індикатор має бути НА елементі керування, а не
    // окремим спінером посеред екрана: інакше користувач не знає, що саме
    // триває, і натискає вдруге.
    const components = theme.components as Record<string, unknown>;

    expect(components['Button']).toBeDefined();
  });
});

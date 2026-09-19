import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, it, expect } from 'vitest';
import { theme } from '../theme';

/**
 * `Q15-01a`: IBM Plex дозволено — **лише самохостингом**.
 *
 * ⛔ Умова замовника стосується не вигляду, а мережі, і саме її найлегше
 * втратити: один рядок `<link href="https://fonts.googleapis.com/…">` у
 * `index.html` виглядає як прискорення й нічого не ламає ні в типах, ні в
 * тестах, ні на екрані розробника. Наслідок побачив би лише оператор у
 * закритому контурі — порожнім шрифтом — і третя сторона, яка дізнавалася б
 * час і адресу кожного входу.
 *
 * ⚠ Перевірки нижче свідомо різні за родом: дві читають ТЕМУ (що вибирає
 * браузер), дві — ДЖЕРЕЛА (звідки беруться файли), одна — самі підмножини
 * шрифта (чи є в ньому потрібні літери). Жодна не замінює іншу.
 */

const webRoot = path.resolve(process.cwd());

function read(relative: string): string {
  return readFileSync(path.join(webRoot, relative), 'utf8');
}

describe('Q15-01a: гарнітура інтерфейсу', () => {
  it('стек починається з IBM Plex, але системний лишається позаду', () => {
    expect(theme.fontFamily ?? '').toMatch(/^"IBM Plex Sans"/);

    /*
     * ⛔ `system-ui` — окрема умова відповіді, не залишок. `font-display: swap`
     * означає, що ПЕРШИЙ кадр кожної сторінки малюється саме резервним стеком,
     * доки `.woff2` іде мережею; стек із самого лише `IBM Plex Sans` дав би на
     * цей час родовий `sans-serif` — тобто Times-подібний на частині систем.
     */
    expect(theme.fontFamily ?? '').toContain('system-ui');
    expect(theme.fontFamily ?? '').toMatch(/sans-serif$/);
  });

  it('моноширинний теж Plex, із системними позаду', () => {
    expect(theme.fontFamilyMonospace ?? '').toMatch(/^"IBM Plex Mono"/);
    expect(theme.fontFamilyMonospace ?? '').toMatch(/monospace$/);
  });

  it('шрифт береться з власної збірки, а не з чужого вузла', () => {
    const entry = read('src/main.tsx');

    expect(entry).toMatch(/@fontsource\/ibm-plex-sans\/400\.css/);
    expect(entry).toMatch(/@fontsource\/ibm-plex-mono\/400\.css/);

    /*
     * ⛔ Жодного зовнішнього вузла шрифтів — ні в точці входу, ні в сторінці.
     *
     * ⚠ Шукається саме ПОСИЛАННЯ (`//вузол`), а не згадка імені: перша
     * редакція шукала голий підрядок і впала на власному ж коментарі в
     * `main.tsx`, який пояснює, ЧОМУ цього вузла тут немає. Сторож, що ловить
     * пояснення замість факту, навчає прибирати пояснення.
     */
    for (const [name, source] of [
      ['src/main.tsx', entry],
      ['index.html', read('index.html')],
    ] as const) {
      expect(source, name).not.toMatch(/\/\/fonts\.googleapis\.com/);
      expect(source, name).not.toMatch(/\/\/fonts\.gstatic\.com/);
      expect(source, name).not.toMatch(/\/\/use\.typekit\.net|\/\/fonts\.bunny\.net/);
    }
  });

  it('усі оголошені `@font-face` посилаються на локальні файли', () => {
    const css = read('node_modules/@fontsource/ibm-plex-sans/400.css');
    const urls = [...css.matchAll(/url\(([^)]+)\)/g)].map((m) => m[1] ?? '');

    expect(urls.length, 'у файлі підмножин має бути хоч одне посилання').toBeGreaterThan(0);

    for (const url of urls) {
      expect(url, url).toMatch(/^\.\/files\//);
    }
  });
});

describe('Q15-01a: казахські літери покриті підмножинами, а не надією', () => {
  /**
   * Ә Ғ Қ Ң Ө Ұ Ү Һ І та їхні рядкові — окрема вимога відповіді замовника.
   *
   * ⛔ Не «кирилиця ж є». Підмножини Google/`@fontsource` ріжуть кирилицю
   * НАДВОЄ: базова (`cyrillic`) не містить ані `Ә`, ані `Ғ`, ані `Қ` — вони
   * лежать у `cyrillic-ext`, яку легко не підключити й не помітити, бо
   * український і російський текст при цьому малюється бездоганно. Побачив би
   * це лише казахський користувач — порожніми прямокутниками.
   */
  const Kazakh = [
    0x04d8, 0x04d9, // Ә ә
    0x0492, 0x0493, // Ғ ғ
    0x049a, 0x049b, // Қ қ
    0x04a2, 0x04a3, // Ң ң
    0x04e8, 0x04e9, // Ө ө
    0x04b0, 0x04b1, // Ұ ұ
    0x04ae, 0x04af, // Ү ү
    0x04ba, 0x04bb, // Һ һ
    0x0406, 0x0456, // І і
  ];

  /** Усі кодові точки, які оголошує хоч один `@font-face` цього файлу. */
  function declaredRanges(css: string): readonly (readonly [number, number])[] {
    return [...css.matchAll(/unicode-range:\s*([^;]+);/g)].flatMap((match) =>
      (match[1] ?? '')
        .split(',')
        .map((part) => part.trim())
        .map((part) => {
          const span = /^U\+([0-9A-F]+)-([0-9A-F]+)$/i.exec(part);
          if (span !== null) {
            return [parseInt(span[1] ?? '', 16), parseInt(span[2] ?? '', 16)] as const;
          }

          const one = /^U\+([0-9A-F]+)$/i.exec(part);
          if (one !== null) {
            const code = parseInt(one[1] ?? '', 16);

            return [code, code] as const;
          }

          // ⚠ Не `return []`: незнайома форма запису має ПАДАТИ, а не мовчки
          // зменшувати покриття — інакше зміна формату фонтсорса зробила б цю
          // перевірку зеленою і порожньою.
          throw new Error(`незнайома форма unicode-range: «${part}»`);
        }),
    );
  }

  it.each(['400', '500', '600', '700'])('IBM Plex Sans, вага %s', (weight) => {
    const ranges = declaredRanges(read(`node_modules/@fontsource/ibm-plex-sans/${weight}.css`));

    expect(ranges.length, 'діапазонів не знайдено — файл підмножин змінив форму').toBeGreaterThan(0);

    for (const code of Kazakh) {
      const covered = ranges.some(([from, to]) => code >= from && code <= to);

      expect(
        covered,
        `U+${code.toString(16).toUpperCase().padStart(4, '0')} (${String.fromCodePoint(code)}) не покрито жодною підмножиною ваги ${weight}`,
      ).toBe(true);
    }
  });

  it('IBM Plex Mono, вага 400', () => {
    const ranges = declaredRanges(read('node_modules/@fontsource/ibm-plex-mono/400.css'));

    for (const code of Kazakh) {
      const covered = ranges.some(([from, to]) => code >= from && code <= to);

      expect(
        covered,
        `U+${code.toString(16).toUpperCase().padStart(4, '0')} (${String.fromCodePoint(code)}) не покрито`,
      ).toBe(true);
    }
  });
});

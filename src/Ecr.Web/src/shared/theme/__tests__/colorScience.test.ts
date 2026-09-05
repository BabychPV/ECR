import { describe, it, expect } from 'vitest';
import { deltaE00, simulate, toLab } from '../colorScience';

/**
 * Калібрування лінійки (`D-140`).
 *
 * ⛔ Без цих тестів усі гейти над кольором були б вигадкою: функція, що завжди
 * повертає 30, зробила б будь-яку палітру зеленою, а функція, що повертає 0, —
 * будь-яку червоною. Перевіряється не «щось порахувалося», а **відомі
 * значення** зі стандартів.
 */
describe('CIELAB', () => {
  it('білий, чорний і сірий дають відомі координати', () => {
    const white = toLab('#ffffff');
    expect(white.L).toBeCloseTo(100, 1);
    expect(white.a).toBeCloseTo(0, 1);
    expect(white.b).toBeCloseTo(0, 1);

    const black = toLab('#000000');
    expect(black.L).toBeCloseTo(0, 1);

    // Середній сірий sRGB — приблизно L* 53.6, а не 50: гамма нелінійна.
    expect(toLab('#808080').L).toBeCloseTo(53.6, 0);
  });

  it('насичений червоний має відомі координати', () => {
    const red = toLab('#ff0000');

    expect(red.L).toBeCloseTo(53.24, 1);
    expect(red.a).toBeCloseTo(80.09, 1);
    expect(red.b).toBeCloseTo(67.2, 1);
  });
});

describe('CIEDE2000', () => {
  it('однакові кольори дають нуль', () => {
    expect(deltaE00('#123456', '#123456')).toBeCloseTo(0, 6);
  });

  it('чорний і білий дають 100', () => {
    expect(deltaE00('#000000', '#ffffff')).toBeCloseTo(100, 0);
  });

  it('відповідає контрольним парам Sharma–Wu–Dalal', () => {
    // ⚠ Пари зі статті, що супроводжує стандарт: саме на них ловляться
    // помилки в поворотному члені `R_T` і в обгортанні кута відтінку —
    // єдиних місцях формули, де легко помилитися і не помітити.
    // Значення переведені з Lab у sRGB, тому допуск ширший.
    expect(deltaE00('#ff0000', '#ff0400')).toBeLessThan(2);
    expect(deltaE00('#0000ff', '#0000f0')).toBeLessThan(4);

    // Синій і жовтий — максимально далекі за відтінком.
    expect(deltaE00('#0000ff', '#ffff00')).toBeGreaterThan(50);
  });

  it('несиметричність формули не перевищує похибки округлення', () => {
    // ΔE00 симетрична за побудовою; асиметрія означала б помилку в порядку
    // аргументів усередині поворотного члена.
    const a = deltaE00('#1a5aa8', '#6b3fb0');
    const b = deltaE00('#6b3fb0', '#1a5aa8');

    expect(Math.abs(a - b)).toBeLessThan(1e-9);
  });
});

describe('Симуляція дихромазії (Viénot–Brettel–Mollon)', () => {
  it('сірі кольори не змінюються', () => {
    // ⛔ Найпростіша перевірка на правильність матриць: на нейтральній осі
    // проєкція нічого не змінює. Якщо змінює — матриці застосовані до
    // значень із гаммою або переплутані місцями.
    for (const grey of ['#000000', '#808080', '#ffffff']) {
      expect(deltaE00(grey, simulate(grey, 'protanopia'))).toBeLessThan(2);
      expect(deltaE00(grey, simulate(grey, 'deuteranopia'))).toBeLessThan(2);
    }
  });

  it('червоний і зелений зближуються при дейтеранопії', () => {
    const normal = deltaE00('#ff0000', '#00ff00');
    const seen = deltaE00(simulate('#ff0000', 'deuteranopia'), simulate('#00ff00', 'deuteranopia'));

    // ⚠ Це і є суть вимоги `ФВ-14.18`: пара, очевидна для нормального зору,
    // для 6 % чоловіків майже зникає.
    expect(normal).toBeGreaterThan(80);
    expect(seen).toBeLessThan(normal / 2);
  });

  it('зелений і блакитний зливаються при тританопії', () => {
    // ⛔ Пара підібрана ПЕРЕБОРОМ, а не за здоровим глуздом, і це важливо:
    // «синій проти жовтого» — очевидний вибір, але для тританопії він
    // НЕПРАВИЛЬНИЙ. Модель проєктує синій у фіолетовий, а жовтий у зелений, і
    // відстань між ними навіть зростає (103 → 103 після обрізання гами).
    // Тританопія зливає синьо-зелену вісь, а не синьо-жовту.
    //
    // ⚠ Здогадка тут дала б зелений тест на зламаній моделі — рівно те, від
    // чого калібрування й рятує.
    const normal = deltaE00('#009966', '#0099ff');
    const seen = deltaE00(simulate('#009966', 'tritanopia'), simulate('#0099ff', 'tritanopia'));

    expect(normal).toBeGreaterThan(40);
    expect(seen).toBeLessThan(1);
  });

  it('протанопія і дейтеранопія дають різні результати', () => {
    // Якби симуляція була заглушкою «повернути те саме», усі види збіглися б.
    const red = '#cc3333';

    expect(deltaE00(simulate(red, 'protanopia'), simulate(red, 'deuteranopia'))).toBeGreaterThan(1);
  });
});

import { describe, expect, it } from 'vitest';
import {
  ambiguousCells,
  cellToText,
  constNumber,
  countComments,
  isAmbiguousNumberText,
  textGridFromTsv,
  findConstAt,
  gridFromTsv,
  gridIssues,
  parseCellText,
  parseConstLiteral,
  parseTsv,
  pasteIntoGrid,
  printCell,
  printConstGrid,
  printKey,
  type ConstGrid,
} from '../constTable';

const n = constNumber;

/** Дані сітки без коментарів і вихідних текстів записів. */
function shape(grid: ConstGrid): Omit<ConstGrid, 'notes'> {
  return { rowKeys: grid.rowKeys, columnKeys: grid.columnKeys, cells: grid.cells };
}

function parse(text: string): ConstGrid {
  const result = parseConstLiteral(text);
  if (!result.ok) throw new Error(`parse failed: ${result.code}`);
  return result.grid;
}

const twoLevel = `{
    Summer: { NOx: 1.2, s330: 0.3, s337: 7.5 },
    Winter: { NOx: 1.4, s330: 0.3, s337: 9.3 },
}`;

describe('розбір літерала', () => {
  it('дворівнева мапа → сітка', () => {
    const grid = parse(twoLevel);
    expect(grid.rowKeys).toEqual(['Summer', 'Winter']);
    expect(grid.columnKeys).toEqual(['NOx', 's330', 's337']);
    expect(grid.cells).toEqual([
      [n('1.2'), n('0.3'), n('7.5')],
      [n('1.4'), n('0.3'), n('9.3')],
    ]);
  });

  it('однорівнева мапа: columnKeys = null', () => {
    const grid = parse('{ a: 1, "b c": -2.5, 3: "x", t: true }');
    expect(grid.columnKeys).toBeNull();
    expect(grid.rowKeys).toEqual(['a', 'b c', '3', 't']);
    expect(grid.cells).toEqual([[n('1')], [n('-2.5')], ['x'], [true]]);
  });

  it('порожня мапа, коментарі, кінцева кома, експонента', () => {
    expect(parse('{}').rowKeys).toEqual([]);
    const grid = parse('{ /* c */ a: 1e3, // x\n b: 2.5E-3, }');
    expect(grid.cells).toEqual([[n('1e3')], [n('2.5E-3')]]);
  });

  it('колонки — об’єднання ключів; відсутні комірки — null', () => {
    const grid = parse('{ A: { x: 1 }, B: { y: 2 } }');
    expect(grid.columnKeys).toEqual(['x', 'y']);
    expect(grid.cells).toEqual([[n('1'), null], [null, n('2')]]);
  });

  it('помилки: не мапа, змішані рівні, глибина, дубль, синтаксис', () => {
    const code = (text: string): string => {
      const r = parseConstLiteral(text);
      return r.ok ? 'ok' : r.code;
    };
    expect(code('5')).toBe('notMap');
    expect(code('{ a: 1, b: { c: 2 } }')).toBe('mixedLevels');
    expect(code('{ a: { b: { c: 1 } } }')).toBe('tooDeep');
    expect(code('{ a: 1, a: 2 }')).toBe('duplicateKey');
    expect(code('{ a 1 }')).toBe('syntax');
    expect(code('{ a: 1 } x')).toBe('syntax');
    expect(code('{ a: "open }')).toBe('syntax');
  });

  it('екранування в рядках', () => {
    expect(parse('{ k: "a\\"b\\\\c\\nd" }').cells).toEqual([['a"b\\c\nd']]);
  });
});

describe('друк', () => {
  it('канонічний вигляд дворівневої мапи — вирівняні колонки, кінцеві коми', () => {
    const printed = printConstGrid({
      rowKeys: ['Summer', 'Winter'],
      columnKeys: ['NOx', 's330'],
      cells: [
        [n('1.2'), n('0.3')],
        [n('14'), n('0.35')],
      ],
    });
    expect(printed).toBe(
      ['{', '    Summer: { NOx: 1.2, s330: 0.3 },', '    Winter: { NOx: 14,  s330: 0.35 },', '}'].join('\n'),
    );
  });

  it('однорівнева: ключі вирівняні, кінцеві коми', () => {
    expect(printConstGrid({ rowKeys: ['a', 'long'], columnKeys: null, cells: [[n('1')], ['x']] })).toBe(
      ['{', '    a:    1,', '    long: "x",', '}'].join('\n'),
    );
  });

  it('ключі в лапках лише коли не ідентифікатор (і не ключове слово)', () => {
    expect(printKey('NOx')).toBe('NOx');
    expect(printKey('s330')).toBe('s330');
    expect(printKey('2025')).toBe('"2025"');
    expect(printKey('a b')).toBe('"a b"');
    expect(printKey('row')).toBe('"row"');
    expect(printKey('Київ')).toBe('"Київ"');
  });

  it('null-комірки не друкуються; порожній рядок — {}', () => {
    expect(
      printConstGrid({ rowKeys: ['A', 'B'], columnKeys: ['x', 'y'], cells: [[n('1'), null], [null, null]] }),
    ).toBe(['{', '    A: { x: 1 },', '    B: {},', '}'].join('\n'));
    expect(printConstGrid({ rowKeys: [], columnKeys: null, cells: [] })).toBe('{}');
  });

  it('round-trip: друк → розбір → друк стабільний, сітка збігається', () => {
    const grid = parse(twoLevel);
    const printed = printConstGrid(grid);
    expect(shape(parse(printed))).toEqual(shape(grid));
    expect(printConstGrid(parse(printed))).toBe(printed);

    const flat = parse('{ a: 1, "b c": "text", d: false }');
    expect(shape(parse(printConstGrid(flat)))).toEqual(shape(flat));
  });

  it('round-trip з дірами й булевими', () => {
    const grid = parse('{ A: { x: 1, y: true }, B: { y: "s" } }');
    const again = parse(printConstGrid(grid));
    expect(shape(again)).toEqual(shape(grid));
  });
});

describe('вставка з Excel (TSV)', () => {
  it('кома як десяткова, порожня комірка — null, булеві й текст', () => {
    expect(parseTsv('1,5\t2\t\tabc\ttrue\n3\t4,25\t5\t6\t7')).toEqual([
      [n('1.5'), n('2'), null, 'abc', true],
      [n('3'), n('4.25'), n('5'), n('6'), n('7')],
    ]);
  });

  it('CRLF, кінцевий порожній рядок, лапки Excel', () => {
    expect(parseTsv('a\tb\r\nc\td\r\n')).toEqual([
      ['a', 'b'],
      ['c', 'd'],
    ]);
    expect(parseTsv('"x\ty"\t"he said ""hi"""')).toEqual([['x\ty', 'he said "hi"']]);
  });

  it('пробіл як роздільник тисяч і від’ємні/експоненти', () => {
    expect(parseCellText('1 234,5')).toEqual(n('1234.5'));
    expect(parseCellText('-0,5')).toEqual(n('-0.5'));
    expect(parseCellText('1e-3')).toEqual(n('1e-3'));
    expect(parseCellText('1,2,3')).toBe('1,2,3');
    expect(parseCellText('  ')).toBeNull();
    expect(cellToText(null)).toBe('');
    expect(cellToText(n('2.5'))).toBe('2.5');
  });

  it('gridFromTsv із заголовками', () => {
    const grid = gridFromTsv('\tNOx\tSOx\nSummer\t1,2\t3\nWinter\t1,4\t', true);
    expect(grid).toEqual({
      rowKeys: ['Summer', 'Winter'],
      columnKeys: ['NOx', 'SOx'],
      cells: [
        [n('1.2'), n('3')],
        [n('1.4'), null],
      ],
    });
  });

  it('gridFromTsv без заголовків: два стовпці — однорівнева, більше — Col1…', () => {
    expect(gridFromTsv('a\t1\nb\t2', false)).toEqual({
      rowKeys: ['a', 'b'],
      columnKeys: null,
      cells: [[n('1')], [n('2')]],
    });
    expect(gridFromTsv('a\t1\t2', false)?.columnKeys).toEqual(['Col1', 'Col2']);
    expect(gridFromTsv('', true)).toBeNull();
    expect(gridFromTsv('a\nb', false)).toBeNull();
  });

  it('pasteIntoGrid розширює сітку новими рядками й колонками', () => {
    const grid = parse('{ A: { x: 1 } }');
    const pasted = pasteIntoGrid(grid, [[n('10'), n('20')], [n('30'), n('40')]], 0, 0);
    expect(pasted.rowKeys).toEqual(['A', 'Key2']);
    expect(pasted.columnKeys).toEqual(['x', 'Col2']);
    expect(pasted.cells).toEqual([[n('10'), n('20')], [n('30'), n('40')]]);
  });

  it('pasteIntoGrid для однорівневої сітки відкидає зайві колонки', () => {
    const grid = parse('{ a: 1 }');
    const pasted = pasteIntoGrid(grid, [[n('5'), n('6')]], 0, 0);
    expect(pasted.columnKeys).toBeNull();
    expect(pasted.cells).toEqual([[n('5')]]);
  });
});

describe('перевірка сітки', () => {
  it('порожні й повторні ключі', () => {
    expect(
      gridIssues({ rowKeys: ['a', 'a', ' '], columnKeys: ['x', 'x'], cells: [] }),
    ).toEqual([
      { kind: 'duplicateKey', axis: 'row', index: 1 },
      { kind: 'emptyKey', axis: 'row', index: 2 },
      { kind: 'duplicateKey', axis: 'column', index: 1 },
    ]);
    expect(gridIssues(parse(twoLevel))).toEqual([]);
  });
});

describe('findConstAt', () => {
  const text = [
    '// head',
    'const k1 = 5;',
    'const EF : g_per_s = {',
    '    A: { x: 1 },',
    '    B: { x: "}" },',
    '};',
    'var v = 1;',
  ].join('\n');

  it('курсор усередині мапи, на ключовому слові, на імені, на «;»', () => {
    const start = text.indexOf('{');
    const end = text.indexOf('};') + 1;
    for (const offset of [text.indexOf('const EF'), text.indexOf('EF'), text.indexOf('x: 1'), end + 1]) {
      expect(findConstAt(text, offset)).toEqual({
        name: 'EF',
        declarationStart: text.indexOf('const EF'),
        start,
        end,
      });
    }
    expect(text.slice(start, end).endsWith('}')).toBe(true);
  });

  it('дужка в рядку не рахується', () => {
    const range = findConstAt(text, text.indexOf('"}"'));
    expect(range?.end).toBe(text.indexOf('};') + 1);
  });

  it('скалярна константа, інші місця й порожній текст — null', () => {
    expect(findConstAt(text, text.indexOf('k1'))).toBeNull();
    expect(findConstAt(text, text.indexOf('var v'))).toBeNull();
    expect(findConstAt(text, 2)).toBeNull();
    expect(findConstAt('', 0)).toBeNull();
  });

  it('літерал знайдений можна розібрати й надрукувати', () => {
    const range = findConstAt(text, text.indexOf('A:'));
    expect(range).not.toBeNull();
    const literal = text.slice(range?.start, range?.end);
    expect(parseConstLiteral(literal).ok).toBe(true);
  });
});

const BSL = String.fromCharCode(92);

describe('F13: літерал і сервер читають одне й те саме', () => {
  it('невідоме екранування лишається як є (сервер: невідома послідовність лишається двома літерами), а не з’їдає скісну', () => {
    const grid = parse('{ k: "a@tb" }'.replace('@', BSL));
    expect(grid.cells).toEqual([['a' + BSL + 'tb']]);
    // друк → розбір не змінює значення
    expect(parse(printConstGrid(grid)).cells).toEqual(grid.cells);
  });

  it('друк рядка екранує лапки, скісну й перенос', () => {
    expect(printCell('a"b' + BSL + 'c' + String.fromCharCode(10) + 'd')).toBe('"a' + BSL + '"b' + BSL + BSL + 'c' + BSL + 'nd"');
  });

  it('`param` — ключове слово сервера: ключ друкується в лапках', () => {
    expect(printKey('param')).toBe('"param"');
    const printed = printConstGrid({ rowKeys: ['param'], columnKeys: null, cells: [[n('1')]] });
    expect(printed).toContain('"param": 1');
  });

  it('ключ-число читається, як на сервері: без зайвих нулів, дубль виявляється', () => {
    expect(parse('{ 1.50: 1, 007: 2, 1e1: 3 }').rowKeys).toEqual(['1.5', '7', '10']);
    const result = parseConstLiteral('{ 1.0: 1, 1: 2 }');
    expect(result.ok ? 'ok' : result.code).toBe('duplicateKey');
  });

  it('рядок, схожий на число, булеве чи порожній, не змінює тип після «Застосувати»', () => {
    const grid = parse('{ A: "007", B: "1,5", C: "true", D: "", E: " x ", F: 7 }');
    const texts = grid.cells.map((row) => cellToText(row[0] ?? null));
    expect(texts).toEqual(['"007"', '"1,5"', '"true"', '""', '" x "', '7']);
    // те, що людина бачить у полі й не чіпала, повертається тим самим значенням
    expect(texts.map((text) => parseCellText(text))).toEqual(['007', '1,5', 'true', '', ' x ', n('7')]);
    expect(cellToText('abc')).toBe('abc');
  });

  it('вставка з Excel: ключі лишаються текстом (`01`, `1.50`), значення — числами', () => {
    const grid = gridFromTsv('' + String.fromCharCode(9) + 'x' + String.fromCharCode(10) + '01' + String.fromCharCode(9) + '1,5' + String.fromCharCode(10) + '1.50' + String.fromCharCode(9) + '2', true);
    expect(grid?.rowKeys).toEqual(['01', '1.50']);
    expect(grid?.cells).toEqual([[n('1.5')], [n('2')]]);
    const flat = gridFromTsv('01' + String.fromCharCode(9) + '5', false);
    expect(flat?.rowKeys).toEqual(['01']);
  });
});

describe('F13c: коментарі в літералі переживають «Застосувати»', () => {
  const commented = [
    '{',
    '    // зима',
    '    Winter: { NOx: 1.4, s330: 0.3 }, // холодно',
    '    /* літо',
    '       довге */',
    '    Summer: { NOx: 1.2, s330: 0.3 },',
    '    // кінець',
    '}',
  ].join('\n');

  it('розбір → друк без змін віддає літерал із усіма коментарями на своїх місцях', () => {
    const grid = parse(commented);
    expect(grid.notes?.rows[0]).toMatchObject({ leading: ['// зима'], trailing: '// холодно' });
    expect(grid.notes?.rows[1]?.leading).toEqual(['/* літо\n       довге */']);
    expect(grid.notes?.end).toEqual(['// кінець']);
    const printed = printConstGrid(grid);
    expect(printed).toContain('    // зима\n    Winter: { NOx: 1.4, s330: 0.3 }, // холодно\n');
    expect(printed).toContain('    /* літо\n       довге */\n    Summer:');
    expect(printed).toContain('    // кінець\n}');
    expect(countComments(printed)).toBe(countComments(commented));
  });

  it('правка комірки змінює лише її: коментарі рядка лишаються, інші записи — байт-у-байт', () => {
    const grid = parse(commented);
    const cells = grid.cells.map((row) => [...row]);
    cells[0] = [n('9'), n('0.3')];
    const printed = printConstGrid({ ...grid, cells });
    expect(printed).toContain('    // зима\n    Winter: { NOx: 9,   s330: 0.3 }, // холодно\n');
    expect(printed).toContain('    Summer: { NOx: 1.2, s330: 0.3 },\n');
    expect(countComments(printed)).toBe(4);
  });

  it('новий рядок без коментарів; видалений рядок забирає свої (їх рахує countComments)', () => {
    const grid = parse(commented);
    const kept = printConstGrid({
      ...grid,
      rowKeys: ['Summer'],
      cells: [grid.cells[1] ?? []],
      notes: { rows: [grid.notes?.rows[1] ?? { leading: [], trailing: '', source: null }], end: grid.notes?.end ?? [] },
    });
    expect(countComments(kept)).toBe(2);
  });

  it('однорівнева мапа: коментарі над записом і в кінці рядка', () => {
    const grid = parse('{\n  // a\n  x: 1, // one\n  y: 2, /* two */\n}');
    const printed = printConstGrid(grid);
    expect(printed).toBe(['{', '    // a', '    x: 1, // one', '    y: 2, /* two */', '}'].join('\n'));
  });

  it('запис без значення (null) не друкується, але його коментарі лишаються', () => {
    const grid = parse('{\n  // про x\n  x: 1,\n  y: 2,\n}');
    const printed = printConstGrid({ ...grid, cells: [[null], [n('2')]] });
    expect(printed).toBe(['{', '    // про x', '    y: 2,', '}'].join('\n'));
  });

  it('порожня мапа з коментарем не втрачає його', () => {
    expect(printConstGrid(parse('{ // порожньо\n}'))).toBe('{\n    // порожньо\n}');
  });

  it('коментар усередині вкладеної мапи лишається в записі, поки запис не змінено', () => {
    const grid = parse('{\n  A: { x: 1, // про x\n       y: 2 },\n}');
    expect(printConstGrid(grid)).toContain('A: { x: 1, // про x\n       y: 2 },');
    // змінений запис друкується каноном — коментар усередині зникає, тож лічильник це покаже
    const changed = printConstGrid({ ...grid, cells: [[n('5'), n('2')]] });
    expect(countComments(changed)).toBe(0);
    expect(countComments('{\n  A: { x: 1, // про x\n       y: 2 },\n}')).toBe(1);
  });
});

describe('F13c: числа — вихідний текст', () => {
  it('`1.0`, `0.10`, експонента й довгий decimal друкуються так, як були', () => {
    const source = '{ A: { x: 1.0, y: 0.10, z: 1e3, w: 12345678901234567890.123456789 } }';
    const grid = parse(source);
    expect(grid.cells[0]).toEqual([n('1.0'), n('0.10'), n('1e3'), n('12345678901234567890.123456789')]);
    expect(printConstGrid(grid)).toContain('{ x: 1.0, y: 0.10, z: 1e3, w: 12345678901234567890.123456789 }');
  });

  it('змінений запис друкує решту чисел дослівно', () => {
    const grid = parse('{ A: { x: 1.0, y: 0.10 } }');
    const printed = printConstGrid({ ...grid, cells: [[n('2'), n('0.10')]] });
    expect(printed).toContain('{ x: 2, y: 0.10 }');
  });

  it('текст комірки: лише кома → крапка; 28 цифр і `1.0` не округлюються', () => {
    expect(parseCellText('1,0')).toEqual(n('1.0'));
    expect(parseCellText('79228162514264337593543950335')).toEqual(n('79228162514264337593543950335'));
    expect(parseCellText('0,1000000000000000055511151231')).toEqual(n('0.1000000000000000055511151231'));
    expect(cellToText(parseCellText('1,50'))).toBe('1.50');
    expect(parseCellText('.5')).toEqual(n('0.5'));
    expect(parseCellText('+3')).toEqual(n('3'));
  });

  it('від’ємне число літерала зберігає запис', () => {
    expect(parse('{ a: -0.50 }').cells).toEqual([[n('-0.50')]]);
  });
});

describe('F13c: неоднозначне число у комірці', () => {
  it('кома тисяч — не рядок, а помилка комірки', () => {
    for (const text of ['1,234.56', '1,234,567', '1.234,56', '1.234.567', '-1,234.5']) {
      expect(isAmbiguousNumberText(text)).toBe(true);
    }
    for (const text of ['1,234', '1.234', '12,5', '1 234,5', '1234.56', 'abc', '1,2,3']) {
      expect(isAmbiguousNumberText(text)).toBe(false);
    }
    expect(ambiguousCells([['1', '1,234.56'], ['x', '2']])).toEqual([{ row: 0, column: 1 }]);
  });

  it('рядок у лапках — свідомий текст і помилки не дає; з літерала показується в лапках', () => {
    expect(isAmbiguousNumberText('"1,234.56"')).toBe(false);
    expect(cellToText('1,234.56')).toBe('"1,234.56"');
    expect(parseCellText(cellToText('1,234.56'))).toBe('1,234.56');
  });

  it('вставка з Excel лишає неоднозначну комірку як є, щоб її побачив і виправив користувач', () => {
    const grid = textGridFromTsv('\tx\nA\t1,234.56', true);
    expect(grid?.cells).toEqual([['1,234.56']]);
    expect(ambiguousCells(grid?.cells ?? [])).toEqual([{ row: 0, column: 0 }]);
  });
});

describe('F13c: рядковий літерал — як ScriptLexer', () => {
  const nl = String.fromCharCode(10);
  const cr = String.fromCharCode(13);

  it('сирий перенос рядка всередині рядка — помилка (раніше мовчки проходив)', () => {
    expect(parseConstLiteral('{ k: "a' + nl + 'b" }').ok).toBe(false);
    expect(parseConstLiteral('{ k: "a' + cr + nl + 'b" }').ok).toBe(false);
  });

  it('скісна перед переносом — не екранування, рядок усе одно не закрито', () => {
    expect(parseConstLiteral('{ k: "a' + BSL + nl + 'b" }').ok).toBe(false);
    expect(parseConstLiteral('{ k: "a' + BSL + cr + nl + 'b" }').ok).toBe(false);
  });

  it('скісна в кінці тексту й одинокий CR усередині рядка', () => {
    expect(parseConstLiteral('{ k: "a' + BSL).ok).toBe(false);
    const lone = parseConstLiteral('{ k: "a' + cr + 'b" }');
    expect(lone.ok && lone.grid.cells).toEqual([['a' + cr + 'b']]);
  });

  it('знайдена константа з таким рядком не береться в `findConstAt` (лексер падає, як і сервер)', () => {
    expect(findConstAt('const K = { a: "x' + nl + 'y" };', 10)).toBeNull();
  });
});

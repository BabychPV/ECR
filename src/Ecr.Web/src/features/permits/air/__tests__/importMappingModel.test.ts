import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ImportIssue, ImportSpec } from '../airTypes';
import { fieldCounts, fieldRows, mappingVerdict, withPickedColumn, type MatchedColumn } from '../import/fieldRows';
import { profileCodeFrom, specPending, transliterate, UnknownRead } from '../import/importModel';
import { columnNumber, sheetParts, sheetSamples, sharedStrings, workbookSamples, xmlText } from '../import/xlsxSamples';
import { RelsXml as Rels, SharedXml as Shared, SheetXml as Sheet2, WorkbookXml as Workbook, sampleWorkbook, withDecompression } from './xlsxZip';

/**
 * UX 20.11 — the core of the simplified «Mapping» step: the field rows and their states, the verdict, the column picked for a
 * field, the «re-read needed» check by content, the code generated from a profile name, and the values read from the workbook.
 *
 * Mutations (each run by hand in the worktree, red, restored):
 * - `fieldRows`: `required ? 'missing' : 'notInFile'` swapped → «a required field … is missing» red;
 * - `fieldRows`: name compared with case (no `toLowerCase`) → «names without case» red;
 * - `fieldRows`: sort without the state rank → «what needs a decision comes first» red;
 * - `mappingVerdict`: `blocked` returned before `missing` is looked at → «missing wins over blocked» red;
 * - `withPickedColumn`: the other field not freed → «one column, one field» red;
 * - `specPending`: `canonical` without the key sort → «same content, other key order» red. (Without the explicit `UnknownRead`
 *   line it stays green — an equivalent mutant: the sentinel never equals a specification by content either; the line is kept
 *   to say the intent.)
 * - `transliterate`: `щ` → `sh` → «Cyrillic names» red; `profileCodeFrom` without `transliterate` → same test red;
 * - `sheetSamples`: `list.includes(value)` dropped → «distinct values» red; rows before `fromRow` not skipped → «below the
 *   header» red; shared strings not resolved (`t="s"`) → «shared strings» red.
 */

const spec = (): ImportSpec => ({
  fields: [
    { name: 'NaeCode', patterns: ['Source number'], level: 'Parent', parser: 'Code', required: true },
    { name: 'TonsPerYear', patterns: ['t/year'], level: 'Child', parser: 'Decimal', required: true },
    { name: 'Height', patterns: ['Height'], level: 'Parent', parser: 'Decimal' },
    { name: 'Remark', patterns: [], column: 'C', level: 'Child', parser: 'Text' },
  ],
});

const mapped = (field: string, column: number, letter: string, header = field): MatchedColumn => ({
  field,
  column,
  columnLetter: letter,
  headerText: header,
  level: 'Parent',
});

describe('fieldRows — what the step shows per field', () => {
  it('a required field that is not in the file is missing; an optional one is «not in this file»', () => {
    const rows = fieldRows(spec(), [mapped('NaeCode', 7, 'G')], []);
    const state = Object.fromEntries(rows.map((row) => [row.name, row.state]));

    expect(state).toEqual({ NaeCode: 'found', TonsPerYear: 'missing', Height: 'notInFile', Remark: 'notInFile' });
    expect(fieldCounts(rows)).toEqual({ found: 1, missing: 1, notInFile: 2 });
  });

  it('names are compared without case, as the server does; a field the spec does not list is still shown as found', () => {
    const rows = fieldRows(spec(), [mapped('naecode', 7, 'G'), mapped('tonsperyear', 27, 'AA'), mapped('FILE_B', 2, 'B')], []);

    expect(rows.map((row) => [row.name, row.state, row.index])).toEqual([
      ['FILE_B', 'found', null],
      ['NaeCode', 'found', 0],
      ['TonsPerYear', 'found', 1],
      ['Height', 'notInFile', 2],
      ['Remark', 'notInFile', 3],
    ]);
  });

  it('what needs a decision comes first, then the found ones left to right as in the file, then the optional absent ones', () => {
    const rows = fieldRows(spec(), [mapped('Height', 30, 'AD'), mapped('NaeCode', 7, 'G')], []);

    expect(rows.map((row) => row.name)).toEqual(['TonsPerYear', 'NaeCode', 'Height', 'Remark']);
  });

  it('the reason a field was not found is the finding about it that is not about one row', () => {
    const perRow: ImportIssue = { severity: 'Warning', messageKey: 'err.x.row', row: 12, field: 'tonsperyear' };
    const summary: ImportIssue = { severity: 'Error', messageKey: 'err.ECR-IMP-0422.regColumnMissing', field: 'TonsPerYear', args: { field: 'TonsPerYear' } };
    const rows = fieldRows(spec(), [mapped('NaeCode', 7, 'G')], [perRow, summary]);

    expect(rows.find((row) => row.name === 'TonsPerYear')?.issue).toBe(summary);
    expect(rows.find((row) => row.name === 'NaeCode')?.issue).toBeNull();
  });

  it('without a specification (still read) the matched columns are the rows; unnamed and repeated fields are left out', () => {
    expect(fieldRows(null, [mapped('NaeCode', 7, 'G')], []).map((row) => row.name)).toEqual(['NaeCode']);
    const odd: ImportSpec = { fields: [{ name: '' }, { name: 'A' }, { name: 'a' }] };
    expect(fieldRows(odd, [], []).map((row) => [row.name, row.index])).toEqual([['A', 1]]);
  });

  it('a field with header texts may go back to automatic matching; one with only a column may not', () => {
    const rows = fieldRows(spec(), [mapped('Remark', 3, 'C'), mapped('NaeCode', 7, 'G')], []);

    expect(rows.find((row) => row.name === 'NaeCode')?.matchesByHeader).toBe(true);
    expect(rows.find((row) => row.name === 'Remark')?.matchesByHeader).toBe(false);
  });
});

describe('mappingVerdict', () => {
  it('missing wins over blocked; ready only when the server lets the file go on', () => {
    expect(mappingVerdict({ found: 1, missing: 1, notInFile: 0 }, false)).toBe('missing');
    expect(mappingVerdict({ found: 1, missing: 1, notInFile: 0 }, true)).toBe('missing');
    expect(mappingVerdict({ found: 2, missing: 0, notInFile: 3 }, false)).toBe('blocked');
    expect(mappingVerdict({ found: 2, missing: 0, notInFile: 3 }, true)).toBe('ready');
  });
});

describe('withPickedColumn', () => {
  it('sets the explicit column (upper case); the rest of the spec is kept', () => {
    const next = withPickedColumn(spec(), 1, 'ab');

    expect(next.fields?.[1]).toEqual({ ...spec().fields?.[1], column: 'AB' });
    expect(next.fields?.[0]).toEqual(spec().fields?.[0]);
  });

  it('one column, one field: another field that names the same column lets it go', () => {
    const next = withPickedColumn(spec(), 0, 'C');

    expect(next.fields?.[0]?.column).toBe('C');
    expect(next.fields?.[3]?.column).toBeNull();
  });

  it('null goes back to matching by the header texts', () => {
    const picked = withPickedColumn(spec(), 0, 'G');

    expect(withPickedColumn(picked, 0, null).fields?.[0]?.column).toBeNull();
  });
});

describe('specPending — re-read needed', () => {
  const profileSpec = spec();

  it('the same reference or the profile itself: no re-read', () => {
    expect(specPending(null, null, profileSpec)).toBe(false);
    const edited = spec();
    expect(specPending(edited, edited, profileSpec)).toBe(false);
  });

  it('«Edit mapping» opened and nothing changed: the copy equals the profile the file was read with', () => {
    expect(specPending(spec(), null, profileSpec)).toBe(false);
  });

  it('same content, other key order and null for an absent value: no re-read', () => {
    const reordered: ImportSpec = {
      fields: (spec().fields ?? []).map((field) => Object.fromEntries(Object.entries(field).reverse())),
      dataStartRow: null,
    };

    expect(specPending(reordered, null, profileSpec)).toBe(false);
  });

  it('a changed mapping needs a re-read; so does an unknown profile', () => {
    expect(specPending(withPickedColumn(spec(), 0, 'B'), null, profileSpec)).toBe(true);
    expect(specPending(spec(), null, undefined)).toBe(true);
  });

  it('after a save from unread edits the file must be re-read whatever is in hand', () => {
    expect(specPending(null, UnknownRead, profileSpec)).toBe(true);
  });
});

describe('profileCodeFrom — a name is enough', () => {
  it('Latin names: upper case, the rest becomes _', () => {
    expect(profileCodeFrom('ERA 3.3 (Atyrau) v2')).toBe('ERA_3_3_ATYRAU_V2');
    expect(profileCodeFrom('2026 files')).toBe('P_2026_FILES');
  });

  it('Cyrillic names (ru, uk, kz) are transliterated', () => {
    expect(profileCodeFrom('Похожие файлы')).toBe('POKHOZHIE_FAYLY');
    expect(profileCodeFrom('Щільність їжі')).toBe('SHCHILNIST_YIZHI');
    expect(profileCodeFrom('Қазақ өңірі')).toBe('QAZAQ_ONIRI');
    expect(transliterate('Цех №5')).toBe('tsekh №5');
  });

  it('a name of signs only gives no code (the user enters one)', () => {
    expect(profileCodeFrom('— !!! —')).toBe('');
  });
});

describe('xlsx samples', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('xml text, column letters, shared strings without the phonetic guide', () => {
    expect(xmlText('a &amp; b &lt;c&gt; &#1062; &#x435;')).toBe('a & b <c> Ц е');
    expect(columnNumber('A')).toBe(1);
    expect(columnNumber('AA')).toBe(27);
    expect(sharedStrings(Shared)).toEqual(['Source number', 'Цех 1', 'Цех 2', 'A & B']);
  });

  it('sheet names → their parts, relative and absolute targets', () => {
    expect([...sheetParts(Workbook, Rels)]).toEqual([
      ['Summary', 'xl/worksheets/sheet1.xml'],
      ['Table 3.3', 'xl/worksheets/sheet2.xml'],
    ]);
  });

  it('below the header: distinct non-empty values, shared strings resolved, at most three, empty cells skipped', () => {
    const samples = sheetSamples(Sheet2, sharedStrings(Shared), 9, [2, 7, 8, 9]);

    expect(samples.get(2)).toEqual(['Цех 1', 'Цех 2', 'A & B']);
    expect(samples.get(7)).toEqual(['1', '2', '3']);
    expect(samples.get(8)).toEqual(['TRUE']);
    expect(samples.get(9)).toEqual(['inline <x>']);
  });

  it('only the rows asked for are scanned', () => {
    expect(sheetSamples(Sheet2, sharedStrings(Shared), 9, [7], 3, 2).get(7)).toEqual(['1', '2']);
  });

  it('a whole workbook file: deflated and stored parts, the sheet chosen by name', async () => {
    withDecompression();
    const file = sampleWorkbook();

    expect((await workbookSamples(file, 'Table 3.3', 9, [2])).get(2)).toEqual(['Цех 1', 'Цех 2', 'A & B']);
    expect((await workbookSamples(file, 'Summary', 9, [2])).get(2)).toEqual(['summary']);
  });

  it('not a workbook: refused (the step then shows no examples)', async () => {
    await expect(workbookSamples(new Blob([new Uint8Array([80, 75, 3, 4])]), 'Sheet1', 9, [2])).rejects.toThrow();
  });
});

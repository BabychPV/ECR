import { describe, expect, it } from 'vitest';
import type { TemplateVersionSummary } from '@/api/types';
import {
  documentCount,
  draftCount,
  filterTemplateRows,
  publishedVersionCount,
  toTemplateListRow,
  type TemplateListSummary,
} from '@/features/templates/templateListModel';

/**
 * `UI-34`: рядок переліку шаблонів — поточна версія, чернетка, стан.
 *
 * ⛔ Головне: «поточна» — ОСТАННЯ опублікована, а не остання взагалі. Якби
 * модель брала останню версію, шаблон із відкритою чернеткою показував би
 * чернетку як чинну — і перелік обіцяв би нові документи на структурі, якої
 * ще не опубліковано.
 */

const template = (id: number, code: string, extra: Partial<TemplateListSummary> = {}): TemplateListSummary => ({
  id,
  code,
  versionCount: 0,
  documentCount: 0,
  isArchived: false,
  ...extra,
});

const version = (
  id: number,
  status: TemplateVersionSummary['status'],
): TemplateVersionSummary => ({
  id,
  version: `${String(id)}.0.0`,
  status,
  presentationRevision: 0,
  publishedAt: null,
  clonedFromVersionId: null,
});

describe('toTemplateListRow', () => {
  it('поточна — остання ОПУБЛІКОВАНА, чернетка — остання Draft', () => {
    const row = toTemplateListRow(template(1, 'A'), [
      version(1, 'Deprecated'),
      version(2, 'Published'),
      version(3, 'Published'),
      version(4, 'Draft'),
    ]);

    expect(row.current?.id).toBe(3);
    expect(row.draft?.id).toBe(4);
    expect(row.state).toBe('Published');
    // ⚠ Застаріла показується лише без поточної — інакше це шум.
    expect(row.lastDeprecated).toBeNull();
  });

  it('лише чернетка — стан Draft, поточної немає', () => {
    const row = toTemplateListRow(template(1, 'A'), [version(1, 'Draft')]);

    expect(row.current).toBeNull();
    expect(row.draft?.id).toBe(1);
    expect(row.state).toBe('Draft');
  });

  it('лише застарілі — стан Deprecated і остання застаріла названа', () => {
    const row = toTemplateListRow(template(1, 'A'), [version(1, 'Deprecated'), version(2, 'Deprecated')]);

    expect(row.state).toBe('Deprecated');
    expect(row.lastDeprecated?.id).toBe(2);
  });

  it('архів шаблону перемагає опубліковану версію', () => {
    const row = toTemplateListRow(template(1, 'A', { isArchived: true }), [version(1, 'Published')]);

    expect(row.state).toBe('Archived');
    // ⚠ Поточна версія лишається названою: документи на ній працюють далі.
    expect(row.current?.id).toBe(1);
  });

  it('назва — мовою інтерфейсу з nameL10n; без неї порожньо', () => {
    expect(toTemplateListRow(template(1, 'A', { nameL10n: { values: { en: 'Air' } } }), []).name).toBe('Air');
    expect(toTemplateListRow(template(1, 'A'), []).name).toBe('');
  });

  it('без версій — стану немає (не вигадуємо «Draft»)', () => {
    const row = toTemplateListRow(template(1, 'A'), []);

    expect(row.state).toBeNull();
    expect(row.current).toBeNull();
    expect(row.draft).toBeNull();
  });
});

describe('показники смуги', () => {
  const rows = [
    toTemplateListRow(template(1, 'A'), [version(1, 'Published'), version(2, 'Published'), version(3, 'Draft')]),
    toTemplateListRow(template(2, 'B'), [version(4, 'Draft')]),
    toTemplateListRow(template(3, 'C'), [version(5, 'Deprecated'), version(6, 'Published')]),
  ];

  it('опубліковані версії рахуються по всіх шаблонах, не по шаблонах', () => {
    expect(publishedVersionCount(rows)).toBe(3);
  });

  it('чернетки — кількість шаблонів із відкритою чернеткою', () => {
    expect(draftCount(rows)).toBe(2);
  });

  it('документи — сума documentCount по шаблонах', () => {
    const counted = [
      toTemplateListRow(template(1, 'A', { documentCount: 5 }), []),
      toTemplateListRow(template(2, 'B', { documentCount: 0 }), []),
      toTemplateListRow(template(3, 'C', { documentCount: 7 }), []),
    ];

    expect(documentCount(counted)).toBe(12);
  });
});

describe('filterTemplateRows', () => {
  const rows = [
    toTemplateListRow(template(1, 'AIR-QUARTERLY'), [version(1, 'Published'), version(2, 'Draft')]),
    toTemplateListRow(template(2, 'WATER'), [version(3, 'Draft')]),
    toTemplateListRow(template(3, 'WASTE'), [version(4, 'Deprecated')]),
  ];

  const codes = (filter: Parameters<typeof filterTemplateRows>[1]): string[] =>
    filterTemplateRows(rows, filter).map((row) => row.template.code);

  it('без фільтрів — усі рядки', () => {
    expect(codes({ query: null, state: null, stat: null })).toEqual(['AIR-QUARTERLY', 'WATER', 'WASTE']);
  });

  it('пошук за кодом — без регістру й пробілів по краях', () => {
    expect(codes({ query: '  wa ', state: null, stat: null })).toEqual(['WATER', 'WASTE']);
  });

  it('стан шаблону', () => {
    expect(codes({ query: null, state: 'Draft', stat: null })).toEqual(['WATER']);
  });

  it('показник «drafts» — шаблони з чернеткою, навіть опубліковані', () => {
    expect(codes({ query: null, state: null, stat: 'drafts' })).toEqual(['AIR-QUARTERLY', 'WATER']);
  });

  it('показник «published» — шаблони з поточною версією', () => {
    expect(codes({ query: null, state: null, stat: 'published' })).toEqual(['AIR-QUARTERLY']);
  });

  it('пошук бачить і назву, не лише код', () => {
    const named = [
      toTemplateListRow(template(1, 'GEN07131100', { nameL10n: { values: { en: 'Air emissions' } } }), []),
      toTemplateListRow(template(2, 'WATER'), []),
    ];

    expect(filterTemplateRows(named, { query: 'emiss', state: null, stat: null }).map((r) => r.template.code)).toEqual([
      'GEN07131100',
    ]);
  });

  it('показник «documents» — шаблони, на які спираються документи', () => {
    const counted = [
      toTemplateListRow(template(1, 'A', { documentCount: 3 }), []),
      toTemplateListRow(template(2, 'B'), []),
    ];

    expect(filterTemplateRows(counted, { query: null, state: null, stat: 'documents' }).map((r) => r.template.code)).toEqual([
      'A',
    ]);
  });

  it('фільтри складаються через «і»', () => {
    expect(codes({ query: 'air', state: 'Draft', stat: null })).toEqual([]);
  });
});

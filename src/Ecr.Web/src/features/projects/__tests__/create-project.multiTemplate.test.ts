import { describe, expect, it } from 'vitest';
import { createProjectBody, extraTemplateChoices } from '@/features/projects/CreateProjectModal';

/**
 * ✎ B74 P4 (D-576, рішення людини 13.11): проєкт «рік · об'єкт» створюється з КІЛЬКОМА шаблонами —
 * `templateVersionIds`, ПЕРША — основна (та сама, що й `templateVersionId`). Кожен шаблон — один раз
 * (`422 ECR-TMPL-0422 duplicateTemplate`).
 *
 * ⚠ Чисті функції, як і `create-project.test.tsx`: рендер Mantine цієї форми в jsdom — понад дві хвилини.
 */
const form = {
  code: 'P2026-SITE',
  name: { en: 'Site 2026' },
  periodKind: 'Monthly',
  timeZoneId: 'Asia/Atyrau',
  versionId: '42',
  policyId: '7',
};

// Шаблон 1 — версії 41/42, шаблон 2 — 50/51, шаблон 3 — 60.
const options = [
  { value: '41', label: 'GEN · 1.0', templateId: 1 },
  { value: '42', label: 'GEN · 1.1', templateId: 1 },
  { value: '50', label: 'LAND · 1.0', templateId: 2 },
  { value: '51', label: 'LAND · 1.1', templateId: 2 },
  { value: '60', label: 'HSE · 1.0', templateId: 3 },
];

describe('B74 P4: тіло створення проєкту з кількома шаблонами', () => {
  it('додаткові шаблони — `templateVersionIds`, перша — основна версія, числами', () => {
    const body = createProjectBody({ ...form, extraVersionIds: ['51', '60'] });

    expect(body.templateVersionIds).toEqual([42, 51, 60]);
    expect(body.templateVersionId).toBe(42);
  });

  it('без додаткових поле не надсилається — тіло одношаблонного проєкту як було', () => {
    expect(createProjectBody(form)).not.toHaveProperty('templateVersionIds');
    expect(createProjectBody({ ...form, extraVersionIds: [] })).not.toHaveProperty('templateVersionIds');
  });

  it('основна версія серед додаткових не дублюється', () => {
    expect(createProjectBody({ ...form, extraVersionIds: ['42', '60'] }).templateVersionIds).toEqual([42, 60]);
  });
});

describe('B74 P4: вибір додаткових шаблонів — кожен шаблон один раз', () => {
  it('шаблон основної версії не пропонується', () => {
    const { available } = extraTemplateChoices(options, '42', []);

    expect(available.map((option) => option.value)).toEqual(['50', '51', '60']);
  });

  it('з версій уже обраного шаблону лишається лише обрана', () => {
    const { available, chosen } = extraTemplateChoices(options, '42', ['51']);

    expect(chosen).toEqual(['51']);
    expect(available.map((option) => option.value)).toEqual(['51', '60']);
  });

  it('обраний додатковий, чий шаблон став основним, випадає — інакше `duplicateTemplate`', () => {
    const { chosen } = extraTemplateChoices(options, '50', ['51', '60']);

    expect(chosen).toEqual(['60']);
  });

  it('дві версії одного шаблону серед додаткових — лишається перша', () => {
    expect(extraTemplateChoices(options, '42', ['50', '51']).chosen).toEqual(['50']);
  });
});

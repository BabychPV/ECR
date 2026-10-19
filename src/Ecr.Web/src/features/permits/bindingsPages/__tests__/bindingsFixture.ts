import type { TemplateSummary, TemplateVersionSummary } from '@/api/types';
import type { PermitBinding, PermitBindings } from '../../api/bindingsTypes';

/** Шаблон переліку для тестів (поля, яких тест не читає, - нейтральні). */
export function template(id: number, code: string, name = ''): TemplateSummary {
  return {
    id,
    code,
    documentCount: 0,
    isArchived: false,
    versionCount: 1,
    ...(name.length > 0 ? { nameL10n: { values: { en: name } } } : {}),
  } as TemplateSummary;
}

/** Версія шаблону; номер - чотири частини, як на сервері. */
export function version(id: number, status: TemplateVersionSummary['status'], number = '1.0.0.0'): TemplateVersionSummary {
  return { id, status, version: number, presentationRevision: 0, clonedFromVersionId: null, publishedAt: null };
}

/** Прив'язка для тестів. */
export function binding(code: string, patch: Partial<PermitBinding> = {}): PermitBinding {
  return {
    id: 1,
    code,
    sheetTableDefId: 10,
    sheetTableCode: `TBL_${code}`,
    permitTypeCode: 'AirPermit',
    permitTableCode: 'AirEmissions',
    mode: 'Rows',
    selectionRule: 'ByFacilityPeriodEnd',
    headerFieldDefId: null,
    headerFieldCode: null,
    isActive: true,
    rowVersion: 'AAAA',
    columns: [
      { role: 'Limit', permitFieldCode: 'Emission_t_year', sheetColumnDefId: 3, sheetColumnCode: 'LIMIT', ordinal: 2 },
      { role: 'RowKey', permitFieldCode: 'Substance', sheetColumnDefId: 2, sheetColumnCode: 'SUBSTANCE', ordinal: 1 },
    ],
    ...patch,
  };
}

export function envelope(...items: PermitBinding[]): PermitBindings {
  return { isEditable: false, items };
}

export function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

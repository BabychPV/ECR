import { describe, it, expect } from 'vitest';
import type { MappedFieldPreview, MappingPreview, SourceEntityStatus } from '@/api/types';
import type { CollectionSchedule } from '@/features/integration/scheduleApi';
import {
  emittedPoints,
  mappedPoints,
  narrowingStep,
  pipelineSteps,
  scheduleOf,
  type PipelineStep,
} from '@/features/pipeline/pipelineSteps';

/**
 * Модель конвеєра сутності джерела (`ФВ-14.3`, область 9; `B21` §7:
 * «перегляд даних після кожного кроку, підсвітка кроку, що звужує набір до
 * нуля»).
 *
 * ⛔ Перевіряється саме ВИЗНАЧЕННЯ підсвітки: лише перший порожній крок —
 * `zero`, наступні — `idle`. Інакше екран казав би «зламано три місця» там,
 * де причина одна.
 */
const entity = (overrides: Partial<SourceEntityStatus> = {}): SourceEntityStatus =>
  ({
    id: 1,
    code: 'FLARE_01',
    displayName: 'Flare 01',
    entityPath: '\\\\AF\\Plant\\Flare_01',
    dataSourceId: 3,
    dataSourceCode: 'PI-WEST',
    transport: 'PiWebApi',
    isActive: true,
    lastRun: { status: 'Succeeded', finishedAt: '2026-09-30T01:00:00Z', pointsRetrieved: 12 },
    oldestGap: null,
    onMissingInSource: 'Keep',
    validFromAttribute: null,
    validToAttribute: null,
    ...overrides,
  }) as SourceEntityStatus;

const field = (overrides: Partial<MappedFieldPreview> = {}): MappedFieldPreview => ({
  fieldMapId: 1,
  sourceField: 'Flare_01_CO',
  outcome: 'Materialized',
  targetRowKey: 'Flare_01',
  targetColumnDefId: 100,
  targetColumnCode: 'CO_MASS',
  aggregation: 'Sum',
  sourceUnitCode: 'kg',
  targetUnitCode: 't',
  pointCount: 5,
  foldedValue: '42.5',
  isActive: true,
  pendingSourceUnitChange: null,
  ...overrides,
});

const preview = (pointsSeen: number, fields: MappedFieldPreview[]): MappingPreview => ({
  sourceEntityId: 1,
  code: 'FLARE_01',
  displayName: 'Flare 01',
  fromUtc: '2026-09-23T00:00:00Z',
  toUtc: '2026-09-30T00:00:00Z',
  pointsSeen,
  isTruncated: false,
  fields,
  rows: [],
  unmappedSourceFields: [],
  uncoveredColumns: [],
});

const schedule = (overrides: Partial<CollectionSchedule> = {}): CollectionSchedule => ({
  id: 7,
  sourceEntityId: 1,
  sourceEntityCode: 'FLARE_01',
  sourceEntityName: 'Flare 01',
  dataSourceId: 3,
  dataSourceCode: 'PI-WEST',
  cron: '0 15 2 * * ?',
  isEnabled: true,
  lastRunAt: null,
  lastError: null,
  lastErrorAt: null,
  rowVersion: 'AAAA',
  lookbackDays: 7,
  ...overrides,
});

const states = (steps: PipelineStep[]): Record<string, string> =>
  Object.fromEntries(steps.map((step) => [step.key, step.state]));

describe('pipelineSteps', () => {
  it('ФВ-14.3: дані доходять до документа — усі кроки ok, точки після кожного кроку', () => {
    const steps = pipelineSteps({
      entity: entity(),
      schedule: schedule(),
      preview: preview(9, [field({ pointCount: 5 }), field({ fieldMapId: 2, pointCount: 3 })]),
    });

    expect(steps.map((step) => step.key)).toEqual(['source', 'schedule', 'collect', 'map', 'emit']);
    expect(states(steps)).toEqual({ source: 'ok', schedule: 'ok', collect: 'ok', map: 'ok', emit: 'ok' });
    expect(steps.map((step) => step.points)).toEqual([null, null, 9, 8, 8]);
    expect(narrowingStep(steps)).toBeNull();
  });

  it('ФВ-14.3: нічого не зібрано — підсвічено збір, наступні кроки idle, а не zero', () => {
    const steps = pipelineSteps({ entity: entity(), schedule: schedule(), preview: preview(0, [field({ pointCount: 0 })]) });

    expect(states(steps)).toMatchObject({ collect: 'zero', map: 'idle', emit: 'idle' });
    expect(narrowingStep(steps)).toBe('collect');
  });

  it('ФВ-14.3: зібране не лягає під жоден діючий мапінг — підсвічено мапінг', () => {
    // Мапінг призупинено: його точки не рахуються, бо значень він не пише.
    const steps = pipelineSteps({
      entity: entity(),
      schedule: schedule(),
      preview: preview(40, [field({ pointCount: 40, isActive: false })]),
    });

    expect(states(steps)).toMatchObject({ collect: 'ok', map: 'zero', emit: 'idle' });
    expect(narrowingStep(steps)).toBe('map');
  });

  it('ФВ-14.3: мапінг є, але адресата в документі немає — підсвічено запис', () => {
    const steps = pipelineSteps({
      entity: entity(),
      schedule: schedule(),
      preview: preview(6, [field({ pointCount: 6, outcome: 'TargetMissing' })]),
    });

    expect(states(steps)).toMatchObject({ collect: 'ok', map: 'ok', emit: 'zero' });
    expect(steps.find((step) => step.key === 'emit')?.points).toBe(0);
  });

  it('D-118: точки, свідомо лишені сирими (RawOnly), — попередження, а не звуження до нуля', () => {
    const steps = pipelineSteps({
      entity: entity(),
      schedule: schedule(),
      preview: preview(6, [field({ pointCount: 6, outcome: 'RawOnly' })]),
    });

    expect(states(steps)).toMatchObject({ emit: 'warn' });
    expect(narrowingStep(steps)).toBeNull();
  });

  it('стан з’єднання й розкладу: вимкнено, зламано, немає', () => {
    expect(states(pipelineSteps({ entity: entity({ isActive: false }), schedule: null, preview: undefined })))
      .toMatchObject({ source: 'off', schedule: 'warn' });

    expect(
      states(
        pipelineSteps({
          entity: entity({ lastRun: { status: 'Failed', finishedAt: null, pointsRetrieved: 0 } }),
          schedule: schedule({ lastError: 'bad cron' }),
          preview: undefined,
        }),
      ),
    ).toMatchObject({ source: 'error', schedule: 'error' });

    expect(states(pipelineSteps({ entity: entity(), schedule: schedule({ isEnabled: false }), preview: undefined })))
      .toMatchObject({ schedule: 'off' });

    // ⛔ Непрочитаний перелік розкладів — не «розкладу немає».
    expect(states(pipelineSteps({ entity: entity(), schedule: undefined, preview: undefined })))
      .toMatchObject({ schedule: 'ok' });
  });

  it('без перегляду кроки з точками не рахують нічого й нічого не підсвічують', () => {
    const steps = pipelineSteps({ entity: entity(), schedule: schedule(), preview: undefined });

    expect(steps.slice(2).map((step) => step.points)).toEqual([null, null, null]);
    expect(narrowingStep(steps)).toBeNull();
  });
});

describe('помічники', () => {
  it('mappedPoints/emittedPoints рахують лише діючі мапінги', () => {
    const p = preview(20, [
      field({ pointCount: 10 }),
      field({ fieldMapId: 2, pointCount: 4, outcome: 'NoData' }),
      field({ fieldMapId: 3, pointCount: 6, isActive: false }),
    ]);

    expect(mappedPoints(p)).toBe(14);
    expect(emittedPoints(p)).toBe(10);
  });

  it('scheduleOf: undefined — не прочитано, null — немає, інакше розклад своєї сутності', () => {
    expect(scheduleOf(undefined, 1)).toBeUndefined();
    expect(scheduleOf([schedule({ sourceEntityId: 2 })], 1)).toBeNull();
    expect(scheduleOf([schedule({ id: 5, sourceEntityId: 2 }), schedule({ id: 9 })], 1)?.id).toBe(9);
  });
});

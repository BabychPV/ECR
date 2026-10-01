import type { MappingPreview, SourceEntityStatus } from '@/api/types';
import type { CollectionSchedule } from '@/features/integration/scheduleApi';

/**
 * Модель конвеєра даних однієї сутності джерела (`ФВ-14.3`, область 9;
 * `B21` §7, `B22` §2).
 *
 * ⚠ Кроки — ті, що система СПРАВДІ виконує для сутності, а не сім
 * декларативних операцій `B22` (`source → join → filter → group → compute →
 * script → emit`): декларативного конвеєра на сервері немає (`BE-21c` не
 * визначено, `D-235`). Тому тут п'ять кроків, і кожен спирається на наявний
 * ендпоінт: з'єднання → розклад → збір → мапінг → запис у документ.
 *
 * ⛔ Функція чиста й винесена з подання навмисно: «який крок звужує набір до
 * нуля» — це і є вимога `B21` §7, і вона має перевірятися тестом, а не оглядом
 * екрана.
 */
export type PipelineStepKey = 'source' | 'schedule' | 'collect' | 'map' | 'emit';

/**
 * Стан кроку.
 *
 * - `ok` — крок пропускає дані далі;
 * - `zero` — ПЕРШИЙ крок, на якому набір став порожнім (підсвічується);
 * - `idle` — після кроку `zero`: даних сюди не дійшло, власної вини кроку немає;
 * - `warn` — крок працює, але є що перевірити (розкладу немає, прогін
 *   `Degraded`, точки лишаються сирими за свідомим вибором `RawOnly`);
 * - `off` — крок вимкнено людиною (сутність неактивна, розклад вимкнено);
 * - `error` — крок зламаний (останній прогін `Failed`, розклад не поставлено).
 */
export type PipelineStepState = 'ok' | 'zero' | 'idle' | 'warn' | 'off' | 'error';

/** Один крок конвеєра. */
export interface PipelineStep {
  readonly key: PipelineStepKey;
  readonly state: PipelineStepState;

  /** Скільки реальних точок вікна перегляду виходить із кроку; `null` — крок не рахує точок. */
  readonly points: number | null;
}

/** Вхід моделі: те, що сторінка вже прочитала з наявних ендпоінтів. */
export interface PipelineInput {
  readonly entity: SourceEntityStatus;

  /** Розклад сутності: `null` — розкладу немає; `undefined` — перелік ще не прочитано чи відмовлено. */
  readonly schedule: CollectionSchedule | null | undefined;

  /** Перегляд мапінгу на реальних рядках; `undefined` — ще не прочитано. */
  readonly preview: MappingPreview | undefined;
}

/** Розклад сутності з переліку розкладів її з'єднання. */
export function scheduleOf(
  schedules: readonly CollectionSchedule[] | undefined,
  sourceEntityId: number,
): CollectionSchedule | null | undefined {
  if (schedules === undefined) return undefined;

  return schedules.find((row) => row.sourceEntityId === sourceEntityId) ?? null;
}

/** Точки вікна, що лягають під ДІЮЧІ мапінги (призупинений значень не пише). */
export function mappedPoints(preview: MappingPreview): number {
  return preview.fields
    .filter((field) => field.isActive)
    .reduce((sum, field) => sum + field.pointCount, 0);
}

/** Точки вікна, що доходять до комірки документа (`Materialized`). */
export function emittedPoints(preview: MappingPreview): number {
  return preview.fields
    .filter((field) => field.isActive && field.outcome === 'Materialized')
    .reduce((sum, field) => sum + field.pointCount, 0);
}

/**
 * Точки, які діючі мапінги свідомо лишають сирими (`RawOnly`, `D-118`).
 *
 * ⚠ Це вибір людини, а не дефект: такі точки не йдуть у документ, але
 * «звуженням до нуля» крок запису через них не вважається.
 */
function rawOnlyPoints(preview: MappingPreview): number {
  return preview.fields
    .filter((field) => field.isActive && field.outcome === 'RawOnly')
    .reduce((sum, field) => sum + field.pointCount, 0);
}

function sourceState(entity: SourceEntityStatus): PipelineStepState {
  if (!entity.isActive) return 'off';
  if (entity.lastRun?.status === 'Failed') return 'error';
  if (entity.lastRun?.status === 'Degraded') return 'warn';

  return 'ok';
}

function scheduleState(schedule: CollectionSchedule | null | undefined): PipelineStepState {
  // ⚠ Непрочитаний перелік — не «розкладу немає»: сказати «немає» на місці
  // відмови означало б запропонувати створити другий розклад поверх наявного.
  if (schedule === undefined) return 'ok';
  if (schedule === null) return 'warn';
  if (schedule.lastError !== null) return 'error';
  if (!schedule.isEnabled) return 'off';

  return 'ok';
}

/**
 * Кроки конвеєра зі станом і кількістю точок після кожного.
 *
 * ⛔ Підсвічується лише ПЕРШИЙ крок, де набір став порожнім. Наступні
 * позначаються `idle`: підсвітити всі три означало б сказати людині «зламано
 * три місця», коли причина одна — і шукати її почнуть не там.
 */
export function pipelineSteps({ entity, schedule, preview }: PipelineInput): PipelineStep[] {
  const steps: PipelineStep[] = [
    { key: 'source', state: sourceState(entity), points: null },
    { key: 'schedule', state: scheduleState(schedule), points: null },
  ];

  // ⚠ Неактивну сутність сервер не переглядає (перегляд мапінгу бере лише
  // активні, інакше 404): даних до кроків 3–5 не доходить — причина вже
  // названа на кроці 1 («Off»), тож решта — `idle`, а не помилка.
  if (!entity.isActive) {
    return [
      ...steps,
      { key: 'collect', state: 'idle', points: null },
      { key: 'map', state: 'idle', points: null },
      { key: 'emit', state: 'idle', points: null },
    ];
  }

  if (preview === undefined) {
    return [
      ...steps,
      { key: 'collect', state: 'ok', points: null },
      { key: 'map', state: 'ok', points: null },
      { key: 'emit', state: 'ok', points: null },
    ];
  }

  const counted: { key: PipelineStepKey; points: number; raw?: number }[] = [
    { key: 'collect', points: preview.pointsSeen },
    { key: 'map', points: mappedPoints(preview) },
    { key: 'emit', points: emittedPoints(preview), raw: rawOnlyPoints(preview) },
  ];

  let narrowed = false;

  for (const step of counted) {
    let state: PipelineStepState;

    if (narrowed) {
      state = 'idle';
    } else if (step.points > 0) {
      state = 'ok';
    } else if ((step.raw ?? 0) > 0) {
      // Усе дійшло до запису, але лишається сирим за вибором людини.
      state = 'warn';
    } else {
      state = 'zero';
      narrowed = true;
    }

    steps.push({ key: step.key, state, points: step.points });
  }

  return steps;
}

/** Крок, що звузив набір до нуля; `null` — дані доходять до кінця. */
export function narrowingStep(steps: readonly PipelineStep[]): PipelineStepKey | null {
  return steps.find((step) => step.state === 'zero')?.key ?? null;
}

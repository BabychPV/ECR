import type { RegistryDefDto } from '@/api/types';

/**
 * D-211: записи довідника з master-джерелом `External` вручну не змінюються —
 * master AF, дані приходять синком. Сервер відмовляє `409 ECR-REG-0409`
 * (`externalSource`); інтерфейс не пропонує дії, яка напевно впаде.
 * `Hybrid` і `Local` — редагуються.
 */
export function isExternalRegistry(registry: Pick<RegistryDefDto, 'sourceKind'> | undefined): boolean {
  return registry?.sourceKind === 'External';
}

/**
 * Коди довідників-дзеркал складу газу FLERT (D-476; сервер — `ExternalRegistryGuard.IsFlertMirror`). До першого
 * увімкненого синку їх немає зовсім: сторінка даних показує нейтральний стан, а не червоне «не знайдено» і «New entry».
 */
export const FlertMirrorRegistryCodes: readonly string[] = ['STREAM', 'STREAM_CASE', 'GAS_COMPOSITION'];

/** Чи код — довідник-дзеркало FLERT (без огляду на регістр, як на сервері). */
export function isFlertMirrorCode(code: string): boolean {
  return FlertMirrorRegistryCodes.includes(code.toUpperCase());
}

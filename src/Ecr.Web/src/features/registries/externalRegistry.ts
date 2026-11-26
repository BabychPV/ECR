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
 * Коди довідників складу газу (D-NEW; сервер — `ExternalRegistryGuard.IsGasCompositionRegistry`): їх пише лише сторінка
 * «Склад газу» (шкала версій, замкнені періоди, Σ). Загальний редактор показує їх лише для читання, з посиланням туди; до імпорту
 * з FLERT чи першого потоку їх немає зовсім — нейтральний стан, а не червоне «не знайдено» і «New entry».
 */
export const GasCompositionRegistryCodes: readonly string[] = ['STREAM', 'STREAM_CASE', 'GAS_COMPOSITION'];

/** Чи код — довідник складу газу (без огляду на регістр, як на сервері). */
export function isGasCompositionRegistryCode(code: string): boolean {
  return GasCompositionRegistryCodes.includes(code.toUpperCase());
}

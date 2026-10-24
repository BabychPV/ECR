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

import { t } from '@/shared/i18n';

/**
 * Текст повідомлення, ключ якого прийшов ІЗ СЕРВЕРА: помилка рядка пакета
 * (`RegistryBatchRowError.messageKey`) чи порушення правила довідника
 * (`RegistryRuleViolationDto.messageKey`).
 *
 * ⚠ Одне місце на весь редактор master-detail — так сторож `EndpointCoverageTests.DynamicKeySites`
 * знає рівно один виклик `t()` зі змінною з цієї лінії. Набір ключів відкритий (валідація записів
 * і правил на сервері), клієнт його не перелічує.
 */
export function serverMessage(messageKey: string, params: Readonly<Record<string, string>>): string {
  return t(messageKey, { ...params });
}

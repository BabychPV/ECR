import { hasText, t } from '@/shared/i18n';

/**
 * Підписи журналу Audit (RC19, «мова на сторінці Audit»): операція, сутність і тип події безпеки
 * приходять із сервера сирими ідентифікаторами (`Update`, `cfg.RegistryDef`, `JobRestarted`), і на
 * ru/kz інтерфейсі вони стояли англійськими посеред перекладеного рядка.
 *
 * ⚠ Ключі — `audit.operationLabel.*`, `audit.entityLabel.*`, `audit.eventLabel.*`. Невідоме значення
 * (нову подію ще не додали в каталог) не ховається і не вгадується: показується читабельна форма
 * самого ідентифікатора (`SaveCollectionSchedule` → «Save collection schedule»), а точне значення
 * лишається в `title` комірки — журнал є доказом, і сирий код мусить бути доступним.
 */

/** `SaveCollectionSchedule` → `Save collection schedule`; схема й крапки (`cfg.TableDef`) розбираються по словах. */
export function humanizeIdentifier(raw: string): string {
  const words = raw
    .replace(/^[a-z]+\./, '')
    .replace(/[._]+/g, ' ')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .trim();

  if (words === '') return raw;

  return words.charAt(0).toUpperCase() + words.slice(1).toLowerCase();
}

export function operationLabel(operation: string): string {
  const key = `audit.operationLabel.${operation}`;

  return hasText(key) ? t(key) : humanizeIdentifier(operation);
}

export function entityTypeLabel(entityType: string): string {
  const key = `audit.entityLabel.${entityType}`;

  return hasText(key) ? t(key) : humanizeIdentifier(entityType);
}

export function securityEventLabel(eventType: string): string {
  const key = `audit.eventLabel.${eventType}`;

  return hasText(key) ? t(key) : humanizeIdentifier(eventType);
}

import { t } from '@/shared/i18n';
import type { ResourceGrantDto } from '@/api/types';

/**
 * Людські підписи переліків гранта (аудит U6): вид ресурсу й рівень доступу.
 *
 * ⛔ Доти `GrantsPanel` друкував значення переліку як є — `Project`, `Sheet`,
 * `Read`, `Manage`: слово з коду, однакове для будь-якої мови інтерфейсу.
 *
 * ⚠ Ключі — літерали в кожній гілці, а не шаблон зі значенням: так їх бачить
 * сторож каталогу і вимагає рядок сіду (той самий вибір, що в
 * `features/templates/enumLabels.ts`).
 *
 * ⚠ Невідоме значення (новий член переліку на сервері раніше за клієнт) —
 * саме значення, а не порожньо: людина має бачити, ЩО там стоїть.
 */

export type ResourceKind = ResourceGrantDto['resourceKind'];
type GrantLevel = ResourceGrantDto['level'];

/**
 * ⚠ `Registry` тут є, хоча старий перелік його не пропонував: сервер знає такий
 * вид (`RegistryAccess.cs`), і грант на довідник, збережений інакше, показувався
 * порожнім полем виду — тобто «якийсь грант», про який не скажеш, на що він.
 */
export const ResourceKinds: readonly ResourceKind[] = ['Project', 'Sheet', 'Table', 'Column', 'Registry'];

/** ⚠ `None` не пропонується: відсутність доступу — це відсутність гранта або заборона. */
export const GrantLevels: readonly GrantLevel[] = ['Read', 'Write', 'Submit', 'Approve', 'Manage'];

export function resourceKindLabel(kind: string): string {
  switch (kind) {
    case 'Project':
      return t('enum.resourceKind.Project');
    case 'Sheet':
      return t('enum.resourceKind.Sheet');
    case 'Table':
      return t('enum.resourceKind.Table');
    case 'Column':
      return t('enum.resourceKind.Column');
    case 'Registry':
      return t('enum.resourceKind.Registry');
    default:
      return kind;
  }
}

export function grantLevelLabel(level: string): string {
  switch (level) {
    case 'Read':
      return t('enum.grantLevel.Read');
    case 'Write':
      return t('enum.grantLevel.Write');
    case 'Submit':
      return t('enum.grantLevel.Submit');
    case 'Approve':
      return t('enum.grantLevel.Approve');
    case 'Manage':
      return t('enum.grantLevel.Manage');
    default:
      return level;
  }
}

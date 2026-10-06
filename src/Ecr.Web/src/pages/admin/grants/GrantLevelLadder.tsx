import type { JSX } from 'react';
import { GrantLevels, grantLevelLabel } from '@/pages/admin/grants/grantLabels';
import { t } from '@/shared/i18n';
import '@/features/security/securityRoles.css';

/**
 * Шкала рівня гранта (UI-37, макет `screens-ops.js` `ladder()` / `.ops-ladder`):
 * N рисок, із них увімкнено стільки, який рівень, і слово рівня поруч.
 *
 * ⚠ Рисок п'ять, а не чотири, як у макеті: у макеті рівні Read · Write ·
 * Approve · Manage, а сервер знає ще `Submit` між Write і Approve
 * (`GrantLevel` в `Enums.cs`). Шкала йде за даними, а не за малюнком.
 *
 * ⚠ Заборона (`isDeny`) — та сама шкала червоним і словом «Deny» поруч:
 * заборона перекриває будь-який дозвіл (ФВ-6.6), і людина має бачити, що цей
 * рівень НЕ відкривається, а закривається.
 *
 * ⛔ Риски — `aria-hidden`: читалка чує слово рівня, а не «п'ять порожніх
 * елементів». Пояснення рівня — у `title` (наведення).
 */
export function GrantLevelLadder({
  level,
  deny = false,
  withLabel = true,
}: {
  readonly level: string;
  readonly deny?: boolean;
  /** `false` — лише риски: слово рівня вже стоїть поруч у полі вибору. */
  readonly withLabel?: boolean;
}): JSX.Element {
  const index = GrantLevels.indexOf(level as (typeof GrantLevels)[number]);
  const hint = levelHint(level);

  return (
    <span className="ecr-ladder" data-level={level} data-deny={deny ? '' : undefined} title={hint}>
      <span className="ecr-ladder-ld" aria-hidden="true">
        {GrantLevels.map((step, i) => (
          <i key={step} data-on={i <= index ? '' : undefined} />
        ))}
      </span>
      {withLabel && grantLevelLabel(level)}
      {withLabel && deny && ` · ${t('grants.deny')}`}
    </span>
  );
}

/** Що дає рівень — одним реченням (макет `LEVEL_TEXT`). Невідомий рівень — без пояснення. */
export function levelHint(level: string): string | undefined {
  switch (level) {
    case 'Read':
      return t('grants.levelHint.Read');
    case 'Write':
      return t('grants.levelHint.Write');
    case 'Submit':
      return t('grants.levelHint.Submit');
    case 'Approve':
      return t('grants.levelHint.Approve');
    case 'Manage':
      return t('grants.levelHint.Manage');
    default:
      return undefined;
  }
}

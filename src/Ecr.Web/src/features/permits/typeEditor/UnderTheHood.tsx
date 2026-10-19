import type { JSX } from 'react';
import { Text } from '@mantine/core';
import { t } from '@/shared/i18n';
import './typeEditor.css';

export interface UnderTheHoodProps {
  /** Код реєстру таблиці (`PRM_<тип>_<таблиця>`). */
  readonly registryCode: string;
  /** Код реєстру шапки, частиною якого є таблиця; для самої шапки не задається. */
  readonly parentCode?: string | undefined;
}

/**
 * Згорнута довідка «Under the hood» (мокап: `E.Collapsible`): таблиця типу зберігається як реєстр, що
 * належить шапці. Нативний `<details>`: розгортається з клавіатури й читалкою без стану React.
 */
export function UnderTheHood({ registryCode, parentCode }: UnderTheHoodProps): JSX.Element {
  return (
    <details className="pt-hood">
      <summary>
        {t('permits.editor.hood.title')}
        <span>{registryCode}</span>
      </summary>
      <Text size="sm" mt="xs">
        {parentCode === undefined
          ? t('permits.editor.hood.noteHead', { registry: registryCode })
          : t('permits.editor.hood.note', { registry: registryCode, parent: parentCode })}
      </Text>
      <dl>
        <dt>{t('permits.editor.hood.registry')}</dt>
        <dd className="pt-mono">{registryCode}</dd>
        {parentCode !== undefined && (
          <>
            <dt>{t('permits.editor.hood.partOf')}</dt>
            <dd className="pt-mono">{parentCode}</dd>
          </>
        )}
        <dt>{t('permits.editor.hood.rows')}</dt>
        <dd>{t('permits.editor.hood.rowsValue')}</dd>
      </dl>
    </details>
  );
}

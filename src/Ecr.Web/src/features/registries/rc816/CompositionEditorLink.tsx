import type { JSX } from 'react';
import { Anchor } from '@mantine/core';
import { Link } from 'react-router-dom';
import { t } from '@/shared/i18n';

/**
 * Посилання з конструктора довідника на його редактор master-detail (`ФВ-8.16`).
 *
 * ⚠ Показується завжди, а не лише довіднику з частинами: вхідних зв'язків опис не несе, і
 * дізнатися, чи є в довідника частини, можна лише прочитавши сусідів — це робить сама сторінка
 * редактора й чесно каже «частин немає».
 */
export function CompositionEditorLink({ code }: { readonly code: string }): JSX.Element {
  return (
    <Anchor component={Link} to={`/admin/registries/${encodeURIComponent(code)}/composition`} size="sm">
      {t('registries.rc816.openEditor')}
    </Anchor>
  );
}

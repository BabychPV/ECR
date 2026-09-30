import type { JSX } from 'react';
import { Anchor } from '@mantine/core';
import { Link } from 'react-router-dom';
import { t } from '@/shared/i18n';

/**
 * Посилання з картки довідника на сторінку «Вплив правки довідника» (RT-25).
 *
 * ⚠ Лише посилання (мікро-модуль): сама сторінка — окремий лінивий чанк у `router.tsx`.
 */
export function RegistryImpactLink({ code }: { readonly code: string }): JSX.Element {
  return (
    <Anchor component={Link} to={`/admin/registries/${encodeURIComponent(code)}/impact`} size="sm">
      {t('registries.impact.open')}
    </Anchor>
  );
}

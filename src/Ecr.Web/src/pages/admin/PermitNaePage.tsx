import type { JSX } from 'react';
import { NaeStructurePage } from '@/features/permits/nae/NaeStructurePage';

/** Структура НАВ (`/admin/permits/nae`): дерево регіон › район › майданчик › обладнання › НАВ, панель вузла. */
export function PermitNaePage(): JSX.Element {
  return <NaeStructurePage />;
}

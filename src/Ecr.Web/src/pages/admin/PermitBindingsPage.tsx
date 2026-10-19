import type { JSX } from 'react';
import { BindingsListView } from '@/features/permits/bindingsPages/BindingsListView';

/**
 * Прив'язки аркушів до пермітів (`/admin/permits/bindings`, макет `screens-permits.js` §5). Перелік збирається з
 * версій шаблонів порціями (див. `BindingsListView`): окремого ендпоінта «усі прив'язки» немає.
 */
export function PermitBindingsPage(): JSX.Element {
  return <BindingsListView />;
}

import type { JSX } from 'react';
import { GroupsPage } from '@/features/permits/groups/GroupsPage';

/** Групи типів дозволів (`/admin/permits/groups`): перелік, шторка створення/редагування, склад. */
export function PermitGroupsPage(): JSX.Element {
  return <GroupsPage />;
}

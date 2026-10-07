import type { JSX } from 'react';
import { Button, Code, Group, Text } from '@mantine/core';
import { Link, useLocation } from 'react-router-dom';
import { permissionLabel } from '@/features/security/permissionLabel';
import { t } from '@/shared/i18n';
import { showApiError, showDone } from '@/shared/ui/notify';
import { ServiceStatePage } from './ServiceStatePage';

/**
 * Стан локації, яким `RouteGuard` передає причину відмови (`UI-09`).
 *
 * ⚠ `state`, а не query-рядок (`?permission=...`) — той самий інваріант, що й
 * для редиректу на `/login`: право — не параметр, яким користувач керує з
 * адресного рядка.
 */
interface ForbiddenLocationState {
  /** Право, якого бракує (`RouteHandle.permission`). */
  permission?: string;
}

/** Стабільний код відмови — той самий, що в серверній `ECR-AUTH-0403` (`Q-242`). */
export const ForbiddenErrorCode = 'ECR-AUTH-0403';

/**
 * Окрема сторінка маршруту `/403` (`UI-09`; `router.tsx`), вигляд — за макетом
 * (b4b; `screens-work.js` `/403`, D15-01): піктограма, заголовок, яке право
 * потрібне ЛЮДСЬКОЮ назвою, «нічого не змінено, попросіть адміністратора»,
 * чипи коду права й коду помилки з копіюванням, «See my access» і головна
 * «Back to Documents».
 *
 * ⚠ Ролі, що мають право, і хто їх видає, макет показує поіменно — клієнт цього
 * не знає без `Security.View` (D15-06: елемент без даних не малюється).
 *
 * ⚠ `permission` у `state` може бути відсутнім (закладка, ручний URL) — тоді
 * лише заголовок, пояснення й дії, без рядка про право.
 *
 * ⛔ Не тупиковий екран (`ФВ-14.24`): «Back to Documents» і «See my access» —
 * обидва маршрути без права.
 */
export function ForbiddenPage(): JSX.Element {
  const location = useLocation();
  const state = location.state as ForbiddenLocationState | null;
  const permission = state?.permission;

  async function copyRequest(): Promise<void> {
    // ⛔ `try`/`catch` — як у `CodeText`: на `http://` поза localhost буфера обміну немає.
    try {
      await navigator.clipboard.writeText(
        `ECR access request · ${permission ?? ''} (${permission === undefined ? '' : permissionLabel(permission)}) · ${ForbiddenErrorCode}`,
      );
      showDone(t('nav.accessDenied.copied'));
    } catch (failure) {
      showApiError(failure);
    }
  }

  return (
    <ServiceStatePage icon="ban" title={t('nav.accessDenied.title')}>
      {permission !== undefined && (
        <Text size="sm" c="dimmed" ta="center">
          {t('err.ECR-AUTH-0403.requiresPermission')}{' '}
          <Text span fw={600} c="var(--mantine-color-text)">
            «{permissionLabel(permission)}»
          </Text>
        </Text>
      )}

      <Text size="sm" c="dimmed" ta="center">
        {t('nav.accessDenied.text')}
      </Text>

      <Group gap="xs" justify="center">
        {permission !== undefined && <Code data-forbidden-permission="">{permission}</Code>}
        <Code>{ForbiddenErrorCode}</Code>
        {permission !== undefined && (
          <Button size="xs" variant="subtle" onClick={() => void copyRequest()}>
            {t('nav.accessDenied.copy')}
          </Button>
        )}
      </Group>

      <Group gap="xs" justify="center" mt="xs">
        <Button component={Link} to="/my-groups" variant="default">
          {t('nav.accessDenied.myAccess')}
        </Button>
        <Button component={Link} to="/">
          {`← ${t('nav.backToDocuments')}`}
        </Button>
      </Group>
    </ServiceStatePage>
  );
}

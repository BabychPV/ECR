import { useState, type JSX } from 'react';
import { Anchor, Box, Group, Paper, Progress, Stack, Text, UnstyledButton } from '@mantine/core';
import { useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import type { CurrentUserDto } from '@/api/types';
import { can, MeQueryKey } from '@/shared/session/useSession';
import { t } from '@/shared/i18n';
import { RecalcFailedDocuments } from './RecalcFailedDocuments';
import { inProjects, isRecalcActive, isRecentlyFinished, recalcProgressText, type RecalcProgress } from './recalcProgress';
import { useProjectRecalcProgress } from './useProjectRecalcProgress';

/*
 * ✎ fg6 (людина 08.11): кожна інформаційна панель — компактна (≤ 2 рядки при 1335 px) і згортається; згорнутий стан
 * пам'ятається для користувача (localStorage, кожне звернення в try/catch) і показує підсумок одним рядком.
 */
const Prefix = 'ecr.recalcBanner';

/** Ключ сховища для користувача (`null` — профіль ще не прочитано). */
export function bannerStorageKey(userId: number | null, part: string): string {
  return `${Prefix}.${userId === null ? 'anon' : String(userId)}.${part}`;
}

function read(key: string): boolean {
  try {
    return globalThis.localStorage?.getItem(key) === '1';
  } catch {
    return false;
  }
}

function write(key: string, on: boolean): void {
  try {
    if (on) globalThis.localStorage?.setItem(key, '1');
    else globalThis.localStorage?.removeItem(key);
  } catch {
    // Сховище недоступне — стан живе до перезавантаження.
  }
}

/** Які перерахунки показати банером: ті, що йдуть, і нещодавно завершені, яких людина не закрила. */
export function visibleRecalcs(
  items: readonly RecalcProgress[],
  isDismissed: (jobId: string) => boolean,
  now: number,
): RecalcProgress[] {
  return items.filter((item) => isRecalcActive(item) || (isRecentlyFinished(item, now) && !isDismissed(item.jobId)));
}

interface ProjectRecalcBannerProps {
  /** Проєкт; `null` — останні власні перерахунки будь-якого проєкту (перелік документів «All projects»). */
  readonly projectId: number | null;
  /**
   * ✎ g2: показувати перерахунки лише цих проєктів (перелік документів — проєкти обраного року); `undefined` — усі
   * рядки запиту, як і раніше (сторінка документа, Periods).
   */
  readonly onlyProjects?: readonly number[] | undefined;
}

/**
 * ✎ UAT recalc-progress (людина 08.11: «як мені зрозуміти що перерахунок завершен?»): прогрес перерахунку проєкту,
 * відновлений із СЕРВЕРА на будь-якій сторінці, де банер змонтовано (перелік документів, Periods, документ).
 *
 * ⚠ Самодостатній (лише `projectId`): той самий компонент переїде на вкладку Periods сторінки проєкту.
 */
export function ProjectRecalcBanner({ projectId, onlyProjects }: ProjectRecalcBannerProps): JSX.Element | null {
  const queryClient = useQueryClient();
  const me = queryClient.getQueryData<CurrentUserDto>(MeQueryKey);
  const userId = me?.userId ?? null;
  const collapsedKey = bannerStorageKey(userId, 'collapsed');

  const { items } = useProjectRecalcProgress(projectId);
  const [collapsedState, setCollapsedState] = useState(() => ({ key: collapsedKey, on: read(collapsedKey) }));
  const collapsed = collapsedState.key === collapsedKey ? collapsedState.on : read(collapsedKey);
  const [dismissed, setDismissed] = useState<readonly string[]>([]);
  const isDismissed = (jobId: string): boolean =>
    dismissed.includes(jobId) || read(bannerStorageKey(userId, `dismissed.${jobId}`));

  const shown = visibleRecalcs(inProjects(items, onlyProjects), isDismissed, Date.now());
  if (shown.length === 0) return null;

  const toggle = (): void => {
    write(collapsedKey, !collapsed);
    setCollapsedState({ key: collapsedKey, on: !collapsed });
  };

  return (
    <Stack gap="xs" mb="xs" data-recalc-banners="">
      {shown.map((item) => (
        <RecalcRow
          key={item.jobId}
          item={item}
          collapsed={collapsed}
          onToggle={toggle}
          canSeeJobs={can(me, 'System.ViewHealth')}
          onDismiss={() => {
            write(bannerStorageKey(userId, `dismissed.${item.jobId}`), true);
            setDismissed((list) => [...list, item.jobId]);
          }}
        />
      ))}
    </Stack>
  );
}

function RecalcRow({
  item,
  collapsed,
  onToggle,
  canSeeJobs,
  onDismiss,
}: {
  readonly item: RecalcProgress;
  readonly collapsed: boolean;
  readonly onToggle: () => void;
  readonly canSeeJobs: boolean;
  readonly onDismiss: () => void;
}): JSX.Element {
  const [showFailed, setShowFailed] = useState(false);
  const active = isRecalcActive(item);
  const tone = item.phase === 'failed' || item.phase === 'finishedWithErrors' ? 'statusWarning' : undefined;
  const percent = item.total === null || item.total === 0 ? 0 : Math.round((item.done * 100) / item.total);
  const text = recalcProgressText(item);

  return (
    <Paper
      withBorder
      px="sm"
      py="xs"
      role="status"
      aria-live="polite"
      data-recalc-banner={item.jobId}
      data-recalc-phase={item.phase}
      data-collapsed={collapsed ? 'true' : 'false'}
    >
      <Group gap="xs" wrap="nowrap" justify="space-between">
        <Text
          size="sm"
          fw={500}
          {...(tone === undefined ? {} : { c: tone })}
          className="ecr-ellipsis"
          title={text}
          data-recalc-text=""
        >
          {text}
        </Text>
        <Group gap="xs" wrap="nowrap">
          {!collapsed && canSeeJobs && (
            <Anchor component={Link} to={`/admin/jobs?mine=1&parent=${encodeURIComponent(item.jobId)}`} size="xs">
              {t('recalcProgress.openJobs')}
            </Anchor>
          )}
          {!collapsed && !active && item.projectId !== null && (
            <Anchor component={Link} to={`/?projectId=${String(item.projectId)}`} size="xs" data-recalc-open-list="">
              {t('recalcProgress.openList')}
            </Anchor>
          )}
          {!collapsed && !active && (
            <Anchor component="button" type="button" size="xs" onClick={onDismiss} data-recalc-dismiss="">
              {t('recalcProgress.dismiss')}
            </Anchor>
          )}
          <UnstyledButton
            onClick={onToggle}
            aria-expanded={!collapsed}
            aria-label={collapsed ? t('recalcProgress.expand') : t('recalcProgress.collapse')}
            data-recalc-toggle=""
            fz="xs"
          >
            {collapsed ? '▸' : '▾'}
          </UnstyledButton>
        </Group>
      </Group>

      {!collapsed && (active || item.failed > 0) && (
        <Group gap="xs" wrap="nowrap" mt="xs">
          {active && (
            <Progress
              value={percent}
              size="sm"
              style={{ flex: 1 }}
              aria-label={t('recalcProgress.progressLabel', { project: item.projectCode })}
            />
          )}
          {item.failed > 0 && (
            <Anchor
              component="button"
              type="button"
              size="xs"
              onClick={() => setShowFailed((value) => !value)}
              aria-expanded={showFailed}
              data-recalc-show-failed=""
            >
              {t('recalcProgress.showFailed', { failed: item.failed })}
            </Anchor>
          )}
        </Group>
      )}

      {!collapsed && showFailed && (
        <Box mt="xs">
          <RecalcFailedDocuments parentJobId={item.jobId} />
        </Box>
      )}
    </Paper>
  );
}

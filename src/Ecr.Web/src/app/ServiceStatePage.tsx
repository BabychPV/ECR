import { useEffect, useRef, type JSX, type ReactNode } from 'react';
import { Box, Center, Stack, Title } from '@mantine/core';
import { announceRoute } from '@/shared/ui/RouteAnnouncer';
import { RouteHeadingClass } from '@/shared/theme/routeHeading';

/** Піктограма службової сторінки (`ban` — 403, `route` — 404), набір `navIcons`: 24×24, обвідка. */
export type ServiceStateIcon = 'ban' | 'route';

function StateIcon({ icon }: { icon: ServiceStateIcon }): JSX.Element {
  return (
    <svg
      width={20}
      height={20}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.75}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      {icon === 'ban' ? (
        <>
          <circle cx="12" cy="12" r="9" />
          <path d="M5.7 5.7l12.6 12.6" />
        </>
      ) : (
        <>
          <circle cx="6" cy="19" r="2" />
          <circle cx="18" cy="5" r="2" />
          <path d="M12 19h4.5a3.5 3.5 0 0 0 0-7h-8a3.5 3.5 0 0 1 0-7H12" />
        </>
      )}
    </svg>
  );
}

/**
 * Службова сторінка 403/404 за макетом (b4b; `screens-work.js` `statePage`,
 * `index.html` `.state`/`.state .ico`, `.work-state` 560 px): піктограма в
 * квадраті 40 px, заголовок 15 px/600, далі — вміст сторінки по центру.
 *
 * ⚠ Той самий прийом фокуса й `aria-live`, що був у кожній із двох сторінок
 * окремо (`Q-279`): заголовок отримує фокус РІВНО раз за монтування і
 * оголошується. Винесено сюди, щоб 403 і 404 не розійшлися знову.
 */
export function ServiceStatePage({
  icon,
  title,
  children,
}: {
  icon: ServiceStateIcon;
  title: string;
  children: ReactNode;
}): JSX.Element {
  const heading = useRef<HTMLHeadingElement>(null);
  const focused = useRef(false);

  useEffect(() => {
    if (focused.current) return;

    focused.current = true;
    heading.current?.focus();
  }, []);

  useEffect(() => {
    announceRoute(title);
  }, [title]);

  return (
    <Center py="xl">
      <Stack gap="sm" align="center" maw={560} role="alert" data-service-state={icon}>
        <Box
          w={40}
          h={40}
          bg="var(--mantine-color-default-hover)"
          c="dimmed"
          style={{
            display: 'grid',
            placeItems: 'center',
            borderRadius: 'var(--mantine-radius-md)',
            border: '1px solid var(--mantine-color-default-border)',
          }}
        >
          <StateIcon icon={icon} />
        </Box>

        {/* ✎ 2026-10-06, «текст в одну строку»: 15px/600 (`.work-state-h`), не 26px. */}
        <Title order={2} fz="md" fw={600} ref={heading} tabIndex={-1} ta="center" className={RouteHeadingClass}>
          {title}
        </Title>

        {children}
      </Stack>
    </Center>
  );
}

import { useId, type JSX, type ReactNode } from 'react';
import { Button, Group, Stack, Text } from '@mantine/core';
import { t } from '@/shared/i18n';
import { EmptyState } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { PermitsSubnav, type PermitsSection } from './PermitsSubnav';

export interface PermitsEmptySectionProps {
  /** Розділ для смуги посилань. */
  readonly current: PermitsSection;
  /** Заголовок сторінки (з каталогу, вирішує сторінка). */
  readonly title: string;
  /** Рядок під заголовком, якщо це не сторінка з описом маршруту (напр. номер перміта). */
  readonly meta?: ReactNode;
  /** «← Back to …» для вкладеної сторінки. */
  readonly back?: { readonly label: string; readonly href: string } | undefined;
  readonly emptyTitle: string;
  readonly emptyHint: string;
  /** Кнопки порожнього стану; усі недоступні, доки немає сервісу пермітів. */
  readonly actions?: readonly { readonly label: string; readonly primary?: boolean }[] | undefined;
}

/**
 * Сторінка розділу Permits до появи сервісу пермітів (PM-06): шапка, смуга розділів і чесний
 * порожній стан за текстом макета. Дії видно, але вони недоступні — і поруч сказано чому: кнопка,
 * яка нічого не робить без пояснення, виглядає як зламана (`ФВ-14.23`).
 *
 * ⛔ Жодних запитів до `/api/v1`: ендпоінтів ще немає, а сторож `EndpointCoverageTests` червоніє
 * на виклик неіснуючого шляху.
 */
export function PermitsEmptySection({
  current,
  title,
  meta,
  back,
  emptyTitle,
  emptyHint,
  actions = [],
}: PermitsEmptySectionProps): JSX.Element {
  const reasonId = useId();

  return (
    <>
      <PageHeader title={title} back={back} meta={meta} />
      <PermitsSubnav current={current} />
      <EmptyState
        title={emptyTitle}
        headingOrder={2}
        hint={emptyHint}
        action={
          <Stack gap="xs" align="center">
            {actions.length > 0 && (
              <Group gap="xs" justify="center">
                {actions.map((action) => (
                  <Button
                    key={action.label}
                    variant={action.primary === true ? 'filled' : 'default'}
                    disabled
                    aria-describedby={reasonId}
                  >
                    {action.label}
                  </Button>
                ))}
              </Group>
            )}
            <Text id={reasonId} size="sm" c="dimmed" ta="center">
              {t('permits.unavailable')}
            </Text>
          </Stack>
        }
      />
    </>
  );
}

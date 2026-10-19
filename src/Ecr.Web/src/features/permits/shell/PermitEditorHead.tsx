import { useEffect, useRef, type JSX, type ReactNode } from 'react';
import { Anchor, Text, Title } from '@mantine/core';
import { Link } from 'react-router-dom';
import { t } from '@/shared/i18n';
import { RouteHeadingClass } from '@/shared/theme/routeHeading';
import { announceRoute } from '@/shared/ui/RouteAnnouncer';
import { permitStates, permitStateLabel, permitStateStep } from './permitStatus';
import './permitsShell.css';

/**
 * Лінія станів Draft → Active → Expired (`E.Stepper` макета). Поточний крок — `aria-current="step"`;
 * невідомий стан не підсвічує жодного.
 */
export function PermitStateStepper({ state }: { readonly state: string }): JSX.Element {
  const current = permitStateStep(state);

  return (
    <ol className="pm-stepper" aria-label={t('permits.state.aria')}>
      {permitStates.map((step, index) => (
        <li key={step} aria-current={index === current ? 'step' : undefined}>
          {permitStateLabel(step)}
        </li>
      ))}
    </ol>
  );
}

export interface PermitEditorHeadProps {
  /** Посилання «← Back to …» (`label` — весь напис). */
  readonly back: { readonly label: string; readonly href: string };
  /** Заголовок сторінки: номер перміта, назва типу чи привʼязки. */
  readonly title: string;
  /** Номер/код читається як код — моноширинним шрифтом. */
  readonly mono?: boolean | undefined;
  /** Рядок під заголовком: «Water discharge permit · Central processing facility». */
  readonly subtitle?: string | undefined;
  /** Бейдж стану поруч (наприклад, `PermitStateBadge`). */
  readonly badge?: ReactNode;
  /** Лінія станів або будь-який рядок стану («Saved 12:03»). */
  readonly progress?: ReactNode;
  /** Кнопки дій праворуч. */
  readonly actions?: ReactNode;
}

/**
 * Шапка редактора перміта / типу / привʼязки (макет `.editor-head`): назад, заголовок, підзаголовок,
 * бейдж, лінія станів і дії. Заголовок — єдиний `h1`; він, як і `PageHeader`, оголошується читалці
 * при відкритті екрана й приймає фокус, якщо його ще ніхто не взяв (`tabIndex={-1}` — не зупинка Tab).
 */
export function PermitEditorHead({
  back,
  title,
  mono = false,
  subtitle,
  badge,
  progress,
  actions,
}: PermitEditorHeadProps): JSX.Element {
  const heading = useRef<HTMLHeadingElement>(null);

  useEffect(() => {
    announceRoute(title);
  }, [title]);

  useEffect(() => {
    const active = document.activeElement;
    // Фокус, який людина вже поставила сама, не відбираємо.
    if (active === null || active === document.body || active === document.documentElement) {
      heading.current?.focus({ preventScroll: true });
    }
  }, []);

  const hasContext = badge !== undefined || progress !== undefined || actions !== undefined;

  return (
    <header className="pm-editor-head">
      <div className="pm-eh-title">
        <Anchor component={Link} to={back.href} size="sm">
          {`← ${back.label}`}
        </Anchor>
        <Title
          order={1}
          size="h3"
          ref={heading}
          tabIndex={-1}
          className={RouteHeadingClass}
          {...(mono ? { ff: 'monospace' } : {})}
        >
          {title}
        </Title>
        {subtitle !== undefined && (
          <Text size="sm" c="dimmed">
            {subtitle}
          </Text>
        )}
      </div>
      {hasContext && (
        <div className="pm-eh-ctx">
          {badge}
          {progress}
          {actions}
        </div>
      )}
    </header>
  );
}

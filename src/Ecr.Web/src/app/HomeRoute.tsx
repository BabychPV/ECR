import type { JSX, ReactNode } from 'react';
import { Navigate } from 'react-router-dom';
import type { CurrentUserDto } from '@/api/types';
import { can, useSession } from '@/shared/session/useSession';
import { routes } from './routes';

/**
 * Чи є в користувача лише «звітна» стартова сторінка: перегляд зрізів є, а перегляду
 * документів — немає (вбудована роль `ReportViewer`).
 *
 * ⛔ FP-1 (прохід «від першого обличчя» 2026-10-11, P1). Головна — перелік документів, а
 * роль, створена для читання регуляторних зрізів, `Document.View` не має: після входу вона
 * бачила червону відмову на першому ж екрані. Стартова сторінка такої ролі — зрізи.
 *
 * ⚠ Умова вузька свідомо: перенаправлення лише коли немає `Document.View` І є
 * `Report.ViewRegulatory`. Усі, хто має перегляд документів (оператор, погоджувач,
 * аудитор…), і далі потрапляють на головну без змін; користувач без жодного з двох прав
 * бачить чесну відмову головної, а не мовчазний перехід.
 */
export function startsOnSnapshots(me: CurrentUserDto | undefined): boolean {
  return me !== undefined && !can(me, 'Document.View') && can(me, 'Report.ViewRegulatory');
}

/** Домашній маршрут: перелік документів або, для ролі без документів, зрізи звітів. */
export function HomeRoute({ children }: { children: ReactNode }): JSX.Element {
  const session = useSession();

  if (startsOnSnapshots(session.data)) {
    return <Navigate to={routes.adminSnapshots.path} replace />;
  }

  return <>{children}</>;
}

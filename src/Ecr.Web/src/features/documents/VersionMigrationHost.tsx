import { lazy, Suspense, useState, type JSX, type RefObject } from 'react';
import { isActiveJob } from '@/features/jobs/myTasks';
import { useMigrationJobCompletion } from './useMigrationJobCompletion';

/**
 * ✎ b18 D-132: задача переносу версії шаблону, її завершення й діалог (D-2) — окремий чанк за першим відкриттям
 * (`VersionMigrationAction` лишає в маршруті пункт меню). Хост лишається змонтованим, тож завершення задачі переживає
 * закриття діалогу.
 */
const loadVersionMigrationDialog = () => import('./VersionMigrationDialog');
const VersionMigrationDialog = lazy(async () => ({
  default: (await loadVersionMigrationDialog()).VersionMigrationDialog,
}));

export interface VersionMigrationHostProps {
  readonly documentId: number;
  readonly opened: boolean;
  readonly allowed: boolean;
  /** Кнопка меню, з якого відкрито діалог: їй повертається фокус при закритті. */
  readonly trigger: RefObject<HTMLElement | null>;
  readonly onOpenedChange: (opened: boolean) => void;
}

export default function VersionMigrationHost({
  documentId,
  opened,
  allowed,
  trigger,
  onOpenedChange,
}: VersionMigrationHostProps): JSX.Element | null {
  // D-2: задача переносу і її завершення живуть тут і переживають закриття діалогу.
  const [jobId, setJobId] = useState<string | null>(null);
  const job = useMigrationJobCompletion(jobId, opened, () => {
    setJobId(null);
    onOpenedChange(false);
  });
  const jobActive = jobId !== null && (job.data === undefined ? job.error === null : isActiveJob(job.data.state));

  const close = () => {
    onOpenedChange(false);
    window.setTimeout(() => trigger.current?.focus(), 0);
    // Завершена (відмовлена) задача не тягнеться в наступне відкриття; та, що йде, - лишається.
    if (!jobActive) setJobId(null);
  };

  if (!allowed || !opened) return null;

  return (
    <Suspense fallback={null}>
      <VersionMigrationDialog documentId={documentId} onClose={close} jobId={jobId} onJobChange={setJobId} />
    </Suspense>
  );
}

import { useCallback, useEffect, useRef, useState, type JSX } from 'react';
import { Button, Text, Title } from '@mantine/core';
import { Link } from 'react-router-dom';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { language, t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showDone } from '@/shared/ui/notify';
import { PermitsPaths } from '../../shell/permitPaths';
import type { AirAnalysis, AirImportResult, AirPreview } from '../airTypes';
import {
  useAnalyseAirImport,
  useApplyAirImport,
  useImportProfile,
  usePreviewAirImport,
  useSaveImportProfile,
} from '../useAirLimits';
import { ApplyStep } from './ApplyStep';
import { FileStep } from './FileStep';
import {
  buildOptions,
  confirmedOwnerKeys,
  EmptyDraft,
  fileStepProblem,
  ImportSteps,
  previewSignature,
  type ImportDraft,
  type ImportStep,
} from './importModel';
import { MappingStep } from './MappingStep';
import { PreviewStep } from './PreviewStep';
import '../air.css';

export interface AirImportWizardProps {
  readonly permitId: number;
  /** Право `Registry.EditData` і не симуляція; без нього кроки лише читаються, а «Next»/«Apply» вимкнено. */
  readonly mayImport: boolean;
  /** Право `Registry.EditDefinition`: збереження профілю імпорту. */
  readonly mayEditProfile: boolean;
}

function stepLabel(step: ImportStep): string {
  switch (step) {
    case 'file':
      return t('permits.air.import.step.file');
    case 'mapping':
      return t('permits.air.import.step.mapping');
    case 'preview':
      return t('permits.air.import.step.preview');
    case 'apply':
      return t('permits.air.import.step.apply');
  }
}

function stepHint(step: ImportStep): string {
  switch (step) {
    case 'file':
      return t('permits.air.import.hint.file');
    case 'mapping':
      return t('permits.air.import.hint.mapping');
    case 'preview':
      return t('permits.air.import.hint.preview');
    case 'apply':
      return t('permits.air.import.hint.apply');
  }
}

/**
 * Майстер імпорту лімітів із Excel (NAE-план N5; макет «Wizard»): 1 Файл → 2 Відповідність → 3 Перегляд (+ «Apply»)
 * → 4 Результат. Все, що змінюється на сервері, — лише кроком 3; кроки 1-2 (`analyse`) нічого не пишуть.
 */
export function AirImportWizard({ permitId, mayImport, mayEditProfile }: AirImportWizardProps): JSX.Element {
  const [step, setStep] = useState<ImportStep>('file');
  const [draft, setDraft] = useState<ImportDraft>(EmptyDraft);
  const [analysis, setAnalysis] = useState<AirAnalysis | null>(null);
  // Специфікація, з якою файл розкладено востаннє: перегляд дозволено лише для неї (інакше бачили б чужу розкладку).
  const [analysedSpec, setAnalysedSpec] = useState<ImportDraft['spec']>(null);
  const [preview, setPreview] = useState<AirPreview | null>(null);
  const [previewSig, setPreviewSig] = useState('');
  const [forceStale, setForceStale] = useState(false);
  const [result, setResult] = useState<AirImportResult | null>(null);
  const [problem, setProblem] = useState<ReturnType<typeof fileStepProblem>>(null);
  const [failure, setFailure] = useState<unknown>(null);
  const [savedCode, setSavedCode] = useState<string | null>(null);
  const heading = useRef<HTMLHeadingElement>(null);
  const arrived = useRef(false);

  const analyse = useAnalyseAirImport();
  const previewCall = usePreviewAirImport(permitId);
  const apply = useApplyAirImport(permitId);
  const saveProfile = useSaveImportProfile();
  const profile = useImportProfile(step === 'mapping' ? draft.profileCode : undefined);
  const analysing = usePendingLoading(analyse.isPending);
  const previewing = usePendingLoading(previewCall.isPending);
  const applying = usePendingLoading(apply.isPending);
  const saving = usePendingLoading(saveProfile.isPending);

  const patchDraft = useCallback((patch: Partial<ImportDraft>) => setDraft((current) => ({ ...current, ...patch })), []);

  // Фокус на заголовок кроку при зміні кроку (не на першому показі: там фокус бере шапка сторінки).
  useEffect(() => {
    if (arrived.current) heading.current?.focus({ preventScroll: false });
    arrived.current = true;
  }, [step]);

  const goTo = (next: ImportStep): void => {
    setFailure(null);
    setStep(next);
  };

  const runAnalyse = (then: ImportStep | null): void => {
    if (analyse.isPending || draft.file === null) return;
    setFailure(null);
    const snapshot = draft.spec;
    analyse.mutate(
      { file: draft.file, options: buildOptions(draft) },
      {
        onSuccess: (data) => {
          setAnalysis(data);
          setAnalysedSpec(snapshot);
          if (then !== null) goTo(then);
        },
        onError: setFailure,
      },
    );
  };

  const runPreview = (): void => {
    if (previewCall.isPending || draft.file === null) return;
    setFailure(null);
    const missing = preview?.preview.missing ?? [];
    const snapshot = draft;
    previewCall.mutate(
      { file: draft.file, options: buildOptions(snapshot, { missing }) },
      {
        onSuccess: (data) => {
          setPreview(data);
          setPreviewSig(previewSignature(snapshot, data.preview.missing));
          setForceStale(false);
          goTo('preview');
        },
        onError: setFailure,
      },
    );
  };

  const runApply = (): void => {
    if (apply.isPending || draft.file === null || preview === null) return;
    setFailure(null);
    apply.mutate(
      {
        file: draft.file,
        options: buildOptions(draft, {
          missing: preview.preview.missing,
          ownerKeys: confirmedOwnerKeys(preview, draft.confirmedReplace),
          fingerprint: preview.preview.fingerprint,
        }),
      },
      {
        onSuccess: (data) => {
          setResult(data);
          showDone(t('permits.air.import.done.toast', { permit: data.permitNumber }));
          goTo('apply');
        },
        onError: (error) => {
          // Сервер відхилив план (`regPlanChanged`/`regReplaceNotConfirmed`): перегляд більше не діє, потрібен новий.
          setFailure(error);
          setForceStale(true);
        },
      },
    );
  };

  const onSaveProfile = (code: string, name: string): void => {
    if (saveProfile.isPending || draft.spec === null) return;
    saveProfile.mutate(
      { code, body: { nameL10n: { [language()]: name, en: name }, spec: draft.spec, rowVersion: null } },
      {
        onSuccess: (saved) => {
          setSavedCode(saved.code);
          setAnalysedSpec(null); // збережена специфікація = та, з якою файл щойно розкладено
          patchDraft({ profileCode: saved.code, spec: null });
        },
        onError: setFailure,
      },
    );
  };

  const next = (): void => {
    if (step === 'file') {
      const found = fileStepProblem(draft);
      setProblem(found);
      if (found === null) runAnalyse('mapping');
    } else if (step === 'mapping') {
      runPreview();
    }
  };

  const back = (): void => {
    if (step === 'mapping') goTo('file');
    else if (step === 'preview') goTo('mapping');
  };

  const stale = forceStale || (preview !== null && previewSignature(draft, preview.preview.missing) !== previewSig);
  const index = ImportSteps.indexOf(step);

  return (
    <div className="pi-page">
      <ol className="pi-steps" aria-label={t('permits.air.import.steps.aria')}>
        {ImportSteps.map((id, at) => (
          <li key={id} aria-current={id === step ? 'step' : undefined} data-done={at < index}>
            {`${String(at + 1)} ${stepLabel(id)}`}
          </li>
        ))}
      </ol>
      <div>
        <Title order={2} size="h4" tabIndex={-1} ref={heading}>
          {stepLabel(step)}
        </Title>
        <Text size="sm" c="dimmed">
          {stepHint(step)}
        </Text>
      </div>
      {failure !== null && <ErrorAlert error={failure} />}

      {step === 'file' && <FileStep draft={draft} onChange={patchDraft} problem={problem} />}
      {step === 'mapping' && analysis !== null && (
        <MappingStep
          analysis={analysis}
          profile={profile.data}
          spec={draft.spec}
          onSpec={(spec) => patchDraft({ spec })}
          onReanalyse={() => runAnalyse(null)}
          reanalysing={analysing}
          mayEditProfile={mayEditProfile}
          onSaveProfile={onSaveProfile}
          saving={saving}
          savedCode={savedCode}
        />
      )}
      {step === 'preview' && preview !== null && (
        <PreviewStep
          preview={preview}
          draft={draft}
          onDraft={patchDraft}
          stale={stale}
          onRerun={runPreview}
          rerunning={previewing}
          onApply={runApply}
          applying={applying}
          mayApply={mayImport}
        />
      )}
      {step === 'apply' && result !== null && <ApplyStep result={result} permitId={permitId} />}

      {step !== 'apply' && (
        <div className="pi-actions">
          {step === 'file' ? (
            <Button component={Link} variant="default" to={`${PermitsPaths.permits}/${String(permitId)}?tab=air`}>
              {t('common.cancel')}
            </Button>
          ) : (
            <Button variant="default" onClick={back}>
              {t('wizard.back')}
            </Button>
          )}
          {step === 'file' && (
            <Button disabled={!mayImport} loading={analysing} onClick={next}>
              {t('permits.air.import.analyse')}
            </Button>
          )}
          {step === 'mapping' && (
            <Button
              disabled={!mayImport || analysis === null || !analysis.analysis.canContinue || draft.spec !== analysedSpec}
              loading={previewing}
              onClick={next}
            >
              {t('permits.air.import.toPreview')}
            </Button>
          )}
        </div>
      )}
      {step === 'mapping' && draft.spec !== analysedSpec && (
        <Text size="sm" c="dimmed" role="status">
          {t('permits.air.import.map.reanalyseFirst')}
        </Text>
      )}
    </div>
  );
}

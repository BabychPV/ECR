import { lazy, Suspense, useCallback, useEffect, useRef, useState, type JSX } from 'react';
import { Button, FileButton, Skeleton, Text, Title } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { language, t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showDone } from '@/shared/ui/notify';
import { PermitsPaths } from '../../shell/permitPaths';
import type { AirAnalysis, AirImportResult, AirPreview, ImportSpec } from '../airTypes';
import {
  AddColumnError,
  addPermitTableColumn,
  AirType,
  fieldFor,
  withColumnField,
  type AddColumnProblem,
  type NewColumnRequest,
} from './addColumn';
import {
  useAirScopeCodes,
  useAirScopeFields,
  useAnalyseAirImport,
  useApplyAirImport,
  useImportProfile,
  usePreviewAirImport,
  useSaveImportProfile,
} from '../useAirLimits';
import { ApplyStep } from './ApplyStep';
import { ScopeStep } from './ScopeStep';
import { scopeFormFields, scopeProblem } from './scopeModel';
import { FileStep } from './FileStep';
import {
  buildOptions,
  confirmedOwnerKeys,
  EmptyDraft,
  fileStepProblem,
  ImportSteps,
  previewSignature,
  profileSaveConflict,
  sendFailureOf,
  type ImportDraft,
  type ImportStep,
  type ProfileSaveConflict,
  type SendFailure,
} from './importModel';
import { getImportProfile } from '../airApi';
import { useAirColumnNames } from './fieldLabels';
// «Mapping» (header rows, added columns, rules editor, profile form) is its own chunk: the page stays within the client
// budget, and the step is only needed after the file is analysed.
const MappingStep = lazy(() => import('./MappingStep').then((module) => ({ default: module.MappingStep })));
import { PreviewStep } from './PreviewStep';
import '../air.css';

export interface AirImportWizardProps {
  readonly permitId: number;
  /** Номер дозволу: крок «Регіон і ділянка» показує його, як діалог «Import Parameters» в Excel. */
  readonly permitNumber: string;
  /** Право `Registry.EditData` і не симуляція; без нього кроки лише читаються, а «Next»/«Apply» вимкнено. */
  readonly mayImport: boolean;
  /** Право `Registry.EditDefinition`: збереження профілю імпорту. */
  readonly mayEditProfile: boolean;
  /** Права `Permit.EditType` + `Permit.PublishType`: «Add as new column» (нова колонка таблиці дозволу). */
  readonly mayAddColumn?: boolean;
}

function stepLabel(step: ImportStep): string {
  switch (step) {
    case 'scope':
      return t('permits.air.import.step.scope');
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
    case 'scope':
      return t('permits.air.import.hint.scope');
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
 * Майстер «Import parameters…» (NAE-план N5 + AIR-ON-AIR; макет «Wizard»): 1 Регіон і ділянка → 2 Файл →
 * 3 Відповідність → 4 Перегляд (+ «Apply») → 5 Результат. Все, що змінюється на сервері, — лише кроком 4; кроки 1-3
 * нічого не пишуть.
 */
export function AirImportWizard({ permitId, permitNumber, mayImport, mayEditProfile, mayAddColumn = false }: AirImportWizardProps): JSX.Element {
  const [step, setStep] = useState<ImportStep>('scope');
  const [scopeIssue, setScopeIssue] = useState<'region' | 'area' | null>(null);
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
  // A request with the workbook that got no answer: the file became unreadable, or the server was not reached.
  const [sendFailure, setSendFailure] = useState<SendFailure | null>(null);
  // What «Try again» repeats (read at click time with the current draft, not a closure over an old one).
  const [again, setAgain] = useState<{ readonly call: 'analyse'; readonly then: ImportStep | null } | { readonly call: 'preview' }>({
    call: 'analyse',
    then: null,
  });
  const [savedCode, setSavedCode] = useState<string | null>(null);
  const [saveConflict, setSaveConflict] = useState<ProfileSaveConflict | null>(null);
  const [readingCurrent, setReadingCurrent] = useState(false);
  const heading = useRef<HTMLHeadingElement>(null);
  const arrived = useRef(false);

  const analyse = useAnalyseAirImport();
  const previewCall = usePreviewAirImport(permitId);
  const apply = useApplyAirImport(permitId);
  const saveProfile = useSaveImportProfile();
  const scopeFields = useAirScopeFields();
  const scopeCodes = useAirScopeCodes();
  const scopeForm = (snapshot: ImportDraft): Readonly<Record<string, string>> =>
    scopeFields.data === undefined || scopeCodes.data === undefined
      ? {}
      : scopeFormFields(scopeFields.data, scopeCodes.data, snapshot.region, snapshot.area);
  const profile = useImportProfile(step === 'mapping' ? draft.profileCode : undefined);
  const columnNames = useAirColumnNames(step === 'mapping' || step === 'preview');
  const analysing = usePendingLoading(analyse.isPending);
  const previewing = usePendingLoading(previewCall.isPending);
  const applying = usePendingLoading(apply.isPending);
  const saving = usePendingLoading(saveProfile.isPending || readingCurrent);
  const queryClient = useQueryClient();
  const addColumn = useMutation<string, Error, NewColumnRequest>({
    mutationKey: ['permits', 'types', AirType, 'add-column'],
    mutationFn: (request) =>
      addPermitTableColumn(request, t('permits.air.import.add.reason', { name: request.name, column: request.letter })),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['permits', 'types'] }),
  });
  const addingColumn = usePendingLoading(addColumn.isPending);
  const [addProblem, setAddProblem] = useState<AddColumnProblem>(null);

  const patchDraft = useCallback((patch: Partial<ImportDraft>) => setDraft((current) => ({ ...current, ...patch })), []);

  // Фокус на заголовок кроку при зміні кроку (не на першому показі: там фокус бере шапка сторінки).
  useEffect(() => {
    if (arrived.current) heading.current?.focus({ preventScroll: false });
    arrived.current = true;
  }, [step]);

  const clearFailure = (): void => {
    setFailure(null);
    setSendFailure(null);
  };

  /** Shows a refusal: a server answer as is (`ErrorAlert`), a request that got no answer with its reason and a way forward. */
  const fail = (error: unknown, file: File | null, repeat: typeof again): void => {
    setAgain(repeat);
    void sendFailureOf(error, file).then((kind) => {
      setSendFailure(kind);
      setFailure(error);
    });
  };

  const goTo = (next: ImportStep): void => {
    clearFailure();
    setStep(next);
  };

  /**
   * Re-reads the file; `override` — with this specification (set into the draft at the same time); `fileOverride` — a file
   * chosen again after the first one became unreadable (set into the draft too; the specification is kept).
   */
  const runAnalyse = (then: ImportStep | null, override?: ImportSpec, fileOverride?: File): void => {
    const file = fileOverride ?? draft.file;
    if (analyse.isPending || file === null) return;
    clearFailure();
    const current = { ...draft, file, ...(override === undefined ? {} : { spec: override }) };
    if (override !== undefined || fileOverride !== undefined) patchDraft({ file, spec: current.spec });
    const snapshot = current.spec;
    analyse.mutate(
      { file, options: buildOptions(current) },
      {
        onSuccess: (data) => {
          setAnalysis(data);
          setAnalysedSpec(snapshot);
          if (then !== null) goTo(then);
        },
        onError: (error) => fail(error, file, { call: 'analyse', then }),
      },
    );
  };

  /** «Choose the file again» after the chosen one became unreadable: the same mapping, the file read anew. */
  const onFileAgain = (file: File | null): void => {
    if (file === null) return;
    if (fileStepProblem({ ...draft, file }) !== null) {
      setProblem(fileStepProblem({ ...draft, file }));
      return;
    }
    runAnalyse(step === 'file' ? 'mapping' : null, undefined, file);
  };

  const runPreview = (): void => {
    if (previewCall.isPending || draft.file === null) return;
    clearFailure();
    const missing = preview?.preview.missing ?? [];
    const snapshot = draft;
    previewCall.mutate(
      { file: draft.file, options: buildOptions(snapshot, { missing }), scope: scopeForm(snapshot) },
      {
        onSuccess: (data) => {
          setPreview(data);
          setPreviewSig(previewSignature(snapshot, data.preview.missing));
          setForceStale(false);
          goTo('preview');
        },
        onError: (error) => fail(error, snapshot.file, { call: 'preview' }),
      },
    );
  };

  const runApply = (): void => {
    if (apply.isPending || draft.file === null || preview === null) return;
    clearFailure();
    const file = draft.file;
    apply.mutate(
      {
        file: draft.file,
        options: buildOptions(draft, {
          missing: preview.preview.missing,
          ownerKeys: confirmedOwnerKeys(preview, draft.confirmedReplace),
          fingerprint: preview.preview.fingerprint,
        }),
        scope: scopeForm(draft),
      },
      {
        onSuccess: (data) => {
          setResult(data);
          showDone(t('permits.air.import.done.toast', { permit: data.permitNumber }));
          goTo('apply');
        },
        onError: (error) => {
          // Сервер відхилив план (`regPlanChanged`/`regReplaceNotConfirmed`): перегляд більше не діє, потрібен новий.
          // A retry after an apply that got no answer starts from a new preview (the old one is stale now).
          fail(error, file, { call: 'preview' });
          setForceStale(true);
        },
      },
    );
  };

  const onAddColumn = (request: NewColumnRequest): void => {
    const base = draft.spec ?? profile.data?.spec;
    if (addColumn.isPending || base === undefined) return;
    clearFailure();
    setAddProblem(null);
    addColumn.mutate(request, {
      onSuccess: (code) => {
        showDone(t('permits.air.import.add.done', { name: request.name }));
        runAnalyse(null, withColumnField(base, fieldFor(code, request)));
      },
      onError: (error) => (error instanceof AddColumnError ? setAddProblem(error.problem) : setFailure(error)),
    });
  };

  /**
   * The one «Save as profile…»: the current specification — edited (header rows, sheet, added columns, rules) or, untouched,
   * the profile's own — goes into a new profile, which the wizard then uses (and the File step lists).
   *
   * `rowVersion` — «Overwrite» after «a profile with this code already exists» (pass 2, 07.11): the token of the profile as it is
   * now; `null` — a new profile. A taken code or the built-in profile comes back as `saveConflict` for the form, not as a failure.
   */
  const onSaveProfile = (code: string, name: string, rowVersion: string | null = null): void => {
    const spec = draft.spec ?? profile.data?.spec;
    if (saveProfile.isPending || spec === undefined) return;
    clearFailure();
    setSaveConflict(null);
    // A new save starts without the old status: its success (even for the same code — «Overwrite») closes the form again.
    setSavedCode(null);
    const analysed = draft.spec === analysedSpec;
    saveProfile.mutate(
      { code, body: { nameL10n: { [language()]: name, en: name }, spec, rowVersion } },
      {
        onSuccess: (saved) => {
          setSavedCode(saved.code);
          // The file was read with this very specification: it stays read (profile instead of spec). Edits that were not
          // re-read yet still ask for a re-read before the preview.
          setAnalysedSpec(analysed ? null : spec);
          patchDraft({ profileCode: saved.code, spec: null });
        },
        onError: (error) => {
          const kind = profileSaveConflict(error);
          if (kind === null) setFailure(error);
          else setSaveConflict({ code, name, kind });
        },
      },
    );
  };

  /** «Overwrite»: reads the profile as it is now (its token) and saves over it; the built-in one is refused here too. */
  const onOverwriteProfile = (): void => {
    if (saveConflict === null || saveConflict.kind !== 'taken' || readingCurrent || saveProfile.isPending) return;
    const { code, name } = saveConflict;
    clearFailure();
    setReadingCurrent(true);
    getImportProfile(code)
      .then((current) => {
        if (current.builtIn) setSaveConflict({ code, name, kind: 'builtIn' });
        else onSaveProfile(code, name, current.rowVersion);
      })
      .catch(setFailure)
      .finally(() => setReadingCurrent(false));
  };

  const next = (): void => {
    if (step === 'scope') {
      if (scopeFields.data === undefined || scopeCodes.data === undefined) return;
      const found = scopeProblem(scopeFields.data, scopeCodes.data, draft.region, draft.area);
      setScopeIssue(found);
      if (found === null) goTo('file');
    } else if (step === 'file') {
      const found = fileStepProblem(draft);
      setProblem(found);
      if (found === null) runAnalyse('mapping');
    } else if (step === 'mapping') {
      runPreview();
    }
  };

  const back = (): void => {
    if (step === 'file') goTo('scope');
    else if (step === 'mapping') goTo('file');
    else if (step === 'preview') goTo('mapping');
  };

  const stale = forceStale || (preview !== null && previewSignature(draft, preview.preview.missing) !== previewSig);
  const index = ImportSteps.indexOf(step);

  return (
    <div className="pi-page">
      {/*
       * Pass 2 (07.11, P3): a completed step is a real button that goes BACK to it (as «Back» does — the draft is kept); the current
       * and the later steps are plain text, so a step is never skipped forward past its check. After «Apply» (Result) nothing goes
       * back: the preview there is used up.
       */}
      <ol className="pi-steps" aria-label={t('permits.air.import.steps.aria')}>
        {ImportSteps.map((id, at) => {
          const text = `${String(at + 1)} ${stepLabel(id)}`;
          const reachable = at < index && step !== 'apply';

          return (
            <li key={id} aria-current={id === step ? 'step' : undefined} data-done={at < index} data-link={reachable}>
              {reachable ? (
                <button type="button" className="pi-step-link" onClick={() => goTo(id)}>
                  {text}
                </button>
              ) : (
                text
              )}
            </li>
          );
        })}
      </ol>
      <div>
        <Title order={2} size="h4" tabIndex={-1} ref={heading}>
          {stepLabel(step)}
        </Title>
        <Text size="sm" c="dimmed">
          {stepHint(step)}
        </Text>
      </div>
      {failure !== null && sendFailure === null && <ErrorAlert error={failure} />}
      {failure !== null && sendFailure !== null && (
        <div className="pi-panel" data-testid="air-send-failure">
          <Banner
            tone="danger"
            title={sendFailure === 'fileUnreadable' ? t('permits.air.import.fileUnreadable.title') : t('permits.air.import.network.title')}
            text={sendFailure === 'fileUnreadable' ? t('permits.air.import.fileUnreadable.text') : t('permits.air.import.network.text')}
          />
          <div className="pi-actions">
            <FileButton onChange={onFileAgain} accept=".xlsx">
              {(props) => (
                <Button {...props} variant="default" loading={analysing}>
                  {t('permits.air.import.chooseAgain')}
                </Button>
              )}
            </FileButton>
            {sendFailure === 'network' && (
              <Button variant="default" onClick={() => (again.call === 'analyse' ? runAnalyse(again.then) : runPreview())}>
                {t('common.retry')}
              </Button>
            )}
          </div>
        </div>
      )}

      {step === 'scope' && (
        <ScopeStep
          permitNumber={permitNumber}
          fields={scopeFields.data}
          codes={scopeCodes.data}
          failure={scopeFields.error ?? scopeCodes.error}
          onRetry={() => {
            void scopeFields.refetch();
            void scopeCodes.refetch();
          }}
          draft={draft}
          onChange={patchDraft}
          problem={scopeIssue}
        />
      )}
      {step === 'file' && <FileStep draft={draft} onChange={patchDraft} problem={problem} />}
      {step === 'mapping' && analysis !== null && (
        <Suspense fallback={<Skeleton height={240} role="status" aria-label={t('common.loading')} />}>
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
          saveConflict={saveConflict}
          onOverwriteProfile={onOverwriteProfile}
          onChooseAnotherCode={() => setSaveConflict(null)}
          onApplySpec={(spec) => runAnalyse(null, spec)}
          mayAddColumn={mayAddColumn}
          onAddColumn={onAddColumn}
          addingColumn={addingColumn}
          addProblem={addProblem}
          columnNames={columnNames}
          />
        </Suspense>
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
          columnNames={columnNames}
        />
      )}
      {step === 'apply' && result !== null && <ApplyStep result={result} permitId={permitId} />}

      {step === 'mapping' && draft.spec !== analysedSpec && (
        // The way forward is right here: after a save or «Discard» the editor (and its own «Re-analyse») may be closed.
        <div className="pi-actions pi-end">
          <Text size="sm" c="dimmed" role="status">
            {t('permits.air.import.map.reanalyseFirst')}
          </Text>
          {draft.spec === null && (
            <Button variant="default" loading={analysing} onClick={() => runAnalyse(null)}>
              {t('permits.air.import.map.reanalyse')}
            </Button>
          )}
        </div>
      )}
      {step !== 'apply' && (
        <div className="pi-actions pi-footer">
          {step === 'scope' ? (
            <Button component={Link} variant="default" to={`${PermitsPaths.permits}/${String(permitId)}?tab=air`}>
              {t('common.cancel')}
            </Button>
          ) : (
            <Button variant="default" onClick={back}>
              {t('wizard.back')}
            </Button>
          )}
          {step === 'scope' && (
            <Button disabled={!mayImport || scopeFields.data === undefined || scopeCodes.data === undefined} onClick={next}>
              {t('wizard.next')}
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
    </div>
  );
}

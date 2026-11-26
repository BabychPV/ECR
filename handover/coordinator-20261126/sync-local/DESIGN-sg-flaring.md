# DESIGN — Sour-gas flaring + thermal oxidizer on the ECR gas-composition registry

Author: analyst agent, 25.11 (READ-ONLY: no code, no commits, SELECT only). **Rev 2 (25.11):** revised after R3 review FAIL
(`.sync-local\design\REVIEW-sg-flaring.md`) and the human decisions of 25.11. Changed: §1.2 (TOX count), §2.1/§2.2 (isOnshore, Stream_ID, L6/L7/L8/X1, 481 note),
§4.1/§4.2, **§5 rewritten**, §6, §7.
**Rev 3 (25.11):** human H11 (per row, composition at event start, any period) → new §5.2a "per-row dated registry read" (engine change).
§5.3 rewritten (monthly-template guard removed). Re-review conditions C1 (AF-side fluid check), C3 (no `??`), C4 (lock overload + guard on all writer
paths), C5 (seed name, unmapped location) folded into §5.2/§5.4/§5.5/§5.7. §5.9, §6 and §7 updated.
Card: `.sync-local\ready\integ-sg-flaring-composition.md` (R3). Builds on `.sync-local\reports\FLERT-plan-1111.md` (§2, verified here)
and `.sync-local\design\DESIGN-gas-composition.md` §2–§4.1 (ECR registry, verified here).
Human 25.11: «Подивися БД ecr та сорс дані і ассемблі і зрозумій що з компонентного складу та де використовується».

Sources and abbreviations used below
- **SRC** = `docs\source\ECR Current Back end\Assembly\DllProject\DllProject\` (legacy C#, SQL CLR assembly). `Sec\` = `SRC\Realization\Sections\`.
- **Assembly check:** compiled `bin/Debug/DllProject.dll` exists only inside `Assembly\DllProject_20260710.zip` and
  `DllProject_20260715_after_rename.zip` (1 176 576 B each; on disk only `External\NCalc.dll`, `Antlr3.Runtime.dll`). The 0715 DLL contains
  the type names `R_Air_HSE30X_Int_SG_Calculate`, `R_Air_HSE300_Int_SG_Calculate`, `R_Air_HSE30X_Cont_General`,
  `R_Air_HSE_Thermaloxidizer_SG_Calculate`, `FlertAllDataVersions` and the user strings `dbo.Flert_GetAllStreamDataFlattened`,
  `HSE_TO_SG_481_`, `Error gas composition Stream Onshore_Sour_Analyzers` (byte scan, not executed). The 21 relevant `.cs` files in the
  0715 zip are byte-identical (MD5) to the extracted tree → the sources below are what the DLL was built from.
- **LDB** = legacy DB copy `ECR` on localhost (events and results 2026-01-01…2026-10-07). `EcrLegacyProd` is **not present** on localhost.
  **FDB** = local `FLERT`. **EDB** = `EcrLandUi` (ECR, live import 25.11). All queries were `SELECT` via `sqlcmd -E`.
- Formula texts: `LDB dbo.AF_Formulas` (2010 rows; read by `SRC\Database\Queries\Common\Q_Common.cs:12-16`, filter
  `FInfo_IsAvailable=1` and date window), constants `dbo.AF_Constants` (`Q_Common.cs:64-68`).

---

## 1. Component usage matrix

### 1.1 How the composition reaches a formula (all consumers)

| Step | Rule | Source |
|---|---|---|
| Read | whole FLERT set once per run: `EXEC dbo.Flert_GetAllStreamDataFlattened` → `[srv].FLERT.dbo.usp_GetAllStreamDataFlattened` (LINKED/LOCAL from `Settings`) | `SRC\Database\Queries\Land\Q_Air_HSE30X_Int.cs:14-16`; LDB `sys.sql_modules` of `Flert_GetAllStreamDataFlattened` |
| Row shape | `FlertAllDataVersions`: 72 properties = stream attrs + type + `GasComposition_ID` + `Temperature` + 45 component columns + `Carbon_Monoxide, Density, Hydrogen, Molecular_Weight, Oxygen, Z_CompressibilityFactor` + validity | `SRC\Types\Air\T_Air_HSE30X_Int.cs:268-346` |
| Argument passing | every property → argument `GCV_<Property>` (TOx: `GCV_<SOR\|EOR>_<Property>`) as string; **NULL → "0"** | `Sec\R_Air_HSE30X_Int_SG\R_Air_HSE30X_Int_SG_Calculate.cs:68-81`; `Sec\R_Air_HSE30X_Cont\R_Air_HSE30X_Cont_SG_Calculate.cs:26-40`; `R_Air_HSE300_Cont_SG_Calculate.cs:35-36`; `Sec\R_Air_HSE_Thermaloxidizer\R_Air_HSE_Thermaloxidizer_SG_Calculate.cs:26-37` |
| Formula reference | `@GCV.<X>` (Flert) or `@GCV_SOR_<X>` / `@GCV_EOR_<X>` (Thermaloxidizer) | LDB `AF_Formulas.FInfo_Text` |
| Per-component coefficients | constants `CST.MW_GCV_<X>`, `CST.LHV_GCV_<X>`, `CST.CmHnP_GCV_<X>`, `nCmHnP`, `mn4CmHnP`, `LHV0` — all in methodology **Common** (53 MW, 51 LHV, 51 CmHnP, 48 nCmHnP, 37 mn4CmHnP, 35 LHV0) | LDB `AF_Constants` (only `MInfo_Name='Common'` has `%_GCV_%`); same counts in EDB `calc.MethodologyConstant` of `Common` |
| Formula set | `MInfo_Name IN (<methodology>, 'Common')` and `FInfo_Report` contains the report name | `R_Air_HSE30X_Int_SG_Calculate.cs:91`; `R_Air_HSE30X_Int_SG_General.cs:83` |

Unit: mol % used directly (no conversion, no rounding on read). Component `MW/LHV/…` come from **constants**, not from FLERT and not from the
ECR `COMPONENT` registry (EDB: `COMPONENT` fields `MW, N_C, N_H, N_S, LHV, FORMULA, SOURCE` have **0 values**, only `HYSYS_NAME` 46).

### 1.2 Matrix — component × consumer (role of the component inside the formulas)

Consumers (legacy calc types; "formulas with @GCV" counted from LDB `AF_Formulas`, available, by name prefix):

| Code | Calc type (report, methodology) | Events / grain | Formulas total / with `@GCV` |
|---|---|---|---|
| **INT30X** | `HSE30X_Int_SG_*` (`Air_Flaring_Intermittent_SG`, `Flert`), offshore islands A/D (TypeReportID 301/302) | PI flare event | 80 / 41 |
| **INT300** | `HSE300_Int_SG_*` (same report), onshore (TypeReportID 300) | PI flare event | 92 / 54 |
| **CONT30X** | `HSE30X_Cont_SG_*` (`Air_Flaring_Continuous`, `Flert`), D-Island LP sour analyzers | day | 76 / 42 |
| **CONT300** | `HSE300_Cont_SG_*` (same), onshore LP sour analyzers | day | 86 / 52 |
| **TOX** | `HSE_TO_SG_<SOR\|EOR>_*` (`Air_HSE_Thermaloxidizer`, `Thermaloxidizer`) | train × mode × day/month × gas column | 152 / 118 |

Roles (abbreviations): **MW** = molecular weight Σ `MW_GCV_X·x`; **LHV** = `HSE30X_*_FuelLowerHeating` Σ `LHV_GCV_X·x`; **m** = `<X>_mass`
= `MW_GCV_X·x / MW`; **GHG** = `GHG1/GHG2` (carbon content via `CmHnP_GCV`); **S** = `TotalSulphurContent` (INT/CONT) or `Sr` (TOX);
**ΣC** = `TotalSumComposition`; **TOX+** = TOX extras `Density20`, `SumMCmHn/SumNCmHn/SumMNCmHn` (hydrocarbons only), `Vo/Vog/VoH2O`
(combustion air/flue gas: CO, CO2, H2, H2S, N2, O2), and the 481 pre-formula `HSE_TO_SG_481_<type>_H2S`.

| Component (ECR code) | INT30X | INT300 | CONT30X | CONT300 | TOX | Note |
|---|---|---|---|---|---|---|
| Methane, Ethane, Propane, IC4_1, NC4_1, IC5_1, NC5_1, C6_1, C7_1 … C14_1, Benzene, Toluene, pXylene, EBenzene | MW LHV m GHG ΣC | same | same | same | MW m GHG ΣC TOX+ (SumCmHn) | 20 hydrocarbons |
| CN1_2, CN2_2, CN3_1 | MW LHV m GHG ΣC | same | same | same | MW m GHG ΣC Density20 | naphthenes |
| Nitrogen, CO2 | MW LHV m GHG ΣC | same | same | same | MW m GHG ΣC Vog | |
| **H2S** | MW LHV m GHG **S** ΣC | same | same | same | MW m GHG **Sr** ΣC Vo Vog VoH2O; **overwritten** for gas `481A/B` by pre-formula | `R_Air_HSE_Thermaloxidizer_SG_Calculate.cs:53-68` |
| MMercaptan, EMercaptan, nPMercaptan, nBMercaptan, CS2, COS | MW LHV m GHG **S** ΣC | same | same | same | MW m GHG **Sr** ΣC | CS2 × `k24`/`k32` (2 S atoms) |
| H2O | MW LHV m GHG ΣC | same | MW LHV m (no GHG) | MW LHV m GHG ΣC | MW m GHG ΣC | |
| TEGlycol | MW LHV m GHG ΣC | same | MW LHV m | same as INT300 | MW only | |
| **CO, H2, O2** (short codes) | — | MW LHV m GHG ΣC | — | MW LHV m GHG ΣC | MW m GHG ΣC Vo/Vog | long aliases `Carbon_Monoxide/Hydrogen/Oxygen` are **referenced by no formula** |
| **NH3** | — | MW LHV m GHG ΣC | — | same | MW m GHG ΣC | |
| **SO2, Sulfur** | — | MW LHV m GHG ΣC (not in S) | — | same | MW m GHG **Sr** ΣC | in TOX SO2_mass / `Sr` |
| **C2H6S2, C3H8S2, C4H10S2** | — | MW LHV m GHG ΣC | — | same | MW m GHG **Sr** ΣC | disulphides |
| **C2H7NO, C4H11NO2** | — | MW LHV m GHG ΣC | — | same | C2H7NO: MW m GHG ΣC; C4H11NO2: MW only | amines |
| **Methanol** | — | — | — | — | MW m GHG ΣC | only stream `14M` (ID 572) has it ≠ 0 among used cases |
| **Helium, C2H6S** | — | — | — | — | m GHG ΣC (C2H6S also Sr) | **not in FLERT** → forced `0.0m` "aren't for SG in Flert" (`…SG_Calculate.cs:40-43`) |
| **Temperature** (case property) | `EC_Wzv` (exit velocity) | `EC_Wzv` | `EC_Wzv` | `EC_Wzv` | — | ECR: `STREAM_CASE.T_C`, not a component |
| Density, Molecular_Weight, Z_CompressibilityFactor (case properties) | — | — | — | — | — | passed as args, **referenced by no formula** (ECR: `DENSITY_OP/MW_HYSYS/Z`, informational) |

Component counts actually referenced: INT30X **34** + T, INT300 **45** + T, CONT30X 34 + T, CONT300 45 + T, TOX **48 names** = all **46**
ECR components (Methanol included) + `Helium` + `C2H6S` (both forced 0), no Temperature (✎ R3 review A3). Union = exactly the **46 ECR
`COMPONENT` codes** (EDB registry 79) + Helium/C2H6S (absent in ECR; the bridge emits a literal `0`, same as legacy).
Query: LDB `AF_Formulas WHERE FInfo_IsAvailable=1 AND FInfo_Text LIKE '%@GCV%'` (307 rows Flert+Thermaloxidizer, 304 distinct names),
regex `@GCV[._](SOR_|EOR_)?(\w+)`; script `scratchpad\matrix.ps1`.

Data relevance (FDB, 118 cases referenced by 2026 events/results, see §2.4): non-zero in used cases — H2S 110, H2O 110, Methane 106, COS 103,
MMercaptan 104, CS2 96, TEGlycol 36, CO 6, H2 6, O2 5, NH3 4, Sulfur 4, SO2 3, C2H6S2 3, C2H7NO 3, Methanol 1, Temperature 117.

### 1.3 Who does NOT read FLERT (for completeness)

| Consumer | Reads | Source |
|---|---|---|
| `HSE30X_Int_FG` (40), `HSE30X_Cont_FG` (33), TOX FG columns 1–6, TOX AGR (468 formulas) | HSE400 document (`Get_DataFromHSE400`, year+month) / AGR inputs; their 2 `MW_GCV` refs are constants only | `R_Air_HSE_Thermaloxidizer_General.cs:346-361`; `R_Air_HSE30X_Cont_General.cs:89,250` |
| 357 `GCV_*` formulas of `HSE400`/`Common` (224 HSE400 with `@GCV.HSE400_*`) | HSE400 fuel-gas composition | LDB `AF_Formulas` group `HSE400 :: Air_GasComposition_HSE400` |

---

## 2. Stream / case selection rules per consumer

### 2.1 Rules

| Consumer | Event → stream/case | Validity window | Hard-coded parts | Missing composition | Source |
|---|---|---|---|---|---|
| INT30X / INT300 | event `GasCompositionID` = FLERT `FlaringStream.ID`; `StreamTypeID` = `OUTER APPLY TOP 1 Flert_FlaringStreamType WHERE StreamID = GasCompositionID AND StreamType = event.StreamType` (exact text); **and stream `isOnshore` = event `IsOnshore`** (✎ R3 A8) | `ValidFrom ≤ event.StartDate AND ValidTo > event.EndDate` (whole event inside one version) | split by `TypeReportID`: onshore → 300 (INT300), A-Island 301 / D-Island 302 → INT30X | no `GCV_*` args → formula errors; Output `FirstOrDefault(StreamTypeID)` → NRE in INT30X Output (no validity filter there) | proc `PrepareData_GetFiltered_Flaring_Intermittent_SG` lines 196-207, 280-284 (LDB `sys.sql_modules`); `R_Air_HSE30X_Int_SG_General.cs:98-110`; `R_Air_HSE30X_Int_SG_Calculate.cs:66`; `R_Air_HSE300_Int_SG_Calculate.cs:64-68`; `R_Air_HSE30X_Int_SG_Output.cs:17`, `R_Air_HSE300_Int_SG_Output.cs:18` |
| CONT300 (onshore LP sour analyzers) | **stream number "416"** + `StreamType.Contains("SOR")` (FDB: stream 317 "Технолог.газ к Контактору Амина", case 1538, GC 1581) | valid at `StartTime` (day) | number + "SOR" literal; isOnshore not checked | error "Error gas composition Stream Onshore_Sour_Analyzers" + skip day | `R_Air_HSE30X_Cont_General.cs:152-179` |
| CONT30X (D-Island LP sour analyzers) | AF attribute `LP_Sour_Analyzers_StreamIDs` = `id:coef;…` (volume × coef per stream), looked up by **`Stream_ID`** (not number, ✎ R3 A9) + `StreamType.Contains(season)`; one result row per stream | valid at `StartTime`; season = Apr–Sep `Summer`, else `Winter` | AF value v1 `19:0.4285714;21:0.4285714;146:0.1428571` (end 2024-12-31T18:59:59Z), v2 `19:0.4;21:0.4;146:0.2` (end 9999); legacy cache `Settings_SourAnalyzers_Cache` 2026 = 19/21/146 × 0.4/0.4/0.2 | NRE (no null check) | `R_Air_HSE30X_Cont_General.cs:112, 183-239`; `PI AF\ECR_01_Air.xml:89874-89875, 3950973, 3951042, 3951058`; proc `AF_Air_Flaring_LP_Sour_Analyzers_UsingGas_Get` (OPENQUERY to AF) |
| TOX SG columns | gas column → stream **number** map `14→14M, 412, 415, 416, 417, 453, 481A, 481B`; `StreamType.Contains("SOR")` and `.Contains("EOR")` | valid at `flare.Date` (day or month) | columns per mode: Normal = 417, 453, 481A/B (train 1/2), 14; other modes = 453, 481A/B, 14, 412, 415, 416 (+ `AVG`) | NRE (no null check) | `R_Air_HSE_Thermaloxidizer_General.cs:72-76, 274-301, 364-365` |

### 2.2 Legacy defects that matter for parity

| # | Defect | Effect | Source |
|---|---|---|---|
| L1 | TOX: `GCV_EOR_*` are filled from **`gsSor`** (the SOR composition) for both types; `gsEor` is only used for `FCID_SG` in output | EOR rows compute with SOR composition but record the EOR composition id | `R_Air_HSE_Thermaloxidizer_SG_Calculate.cs:21,32,35`; `…_SG_Output.cs:26,29` |
| L2 | TOX: stream number **417 is duplicated** (FDB: ID 318 = SOR only, ID 319 = EOR only, ID 575 = SOR+EOR); `FirstOrDefault` order-dependent | 2026 results used GC 2050 (stream 575) | FDB query §2.3; LDB `CalcResult_FlaringThermaloxidizer` |
| L3 | CONT300 (`"416"`) and TOX lookups go by **number** and ignore `isOnshore`; CONT30X goes by `Stream_ID` (not affected) but matches season by English text only — Cyrillic `зима/лето` never match | only English "370 Summer/Winter" cases usable for CONT30X | `R_Air_HSE30X_Cont_General.cs:166, 233` |
| L4 | INT: event crossing a version boundary has no composition (window must contain the whole event) | error/NRE | `R_Air_HSE30X_Int_SG_Calculate.cs:66` |
| L5 | NULL component → "0"; Σ=0 "No composition" case → silent zero emissions | — | §1.1 |
| **L6** | TOX: `flareMain.Train == "1" ? "481A" : "481B"`, but data holds `"Train 1"` / `"Train 2"` → **Train 1 always uses 481B** | LDB `CalcResult_FlaringThermaloxidizer`: `Train 1 / 481B / FCID_SG 2046` 10 rows, `Train 2 / 481B / 2046` 10 rows; `AF_Air_HSE_Thermaloxidizer` trains = `Train 1`, `Train 2` (486 rows per mode) + 40 junk `Train='0'` | `R_Air_HSE_Thermaloxidizer_General.cs:288, 295` (Category uses `Substring(len-1)`, l.339) |
| **L7** | TOX: missing HSE400 month → whole day/month skipped, SG included | SG rows lost although SG does not need HSE400 | `R_Air_HSE_Thermaloxidizer_General.cs:137-142` (reviewer) |
| **L8** | INT: open events dropped (`EndDate != null`; AF query `EndTime IS NOT NULL`) | event counted only once closed | `R_Air_HSE30X_Int_SG_General.cs:80`; proc `AF_Air_Flaring_Intermittent_SG_Data_Get` |
| **X1** | INT `CalculationDate = EndDate`; 47 of 2 899 events (1.6 %, UTC) start and end in different months | legacy attributes them to the end month in results tables | `R_Air_HSE30X_Int_SG_Calculate.cs:61`; LDB count query |

Note for the lane: `docs/build/archive/bridge-thermaloxidizer.md` calls the `HSE_TO_SG_481_*` formulas "unreachable"; this is wrong for
`HSE_TO_SG_481_<type>_H2S` (`…SG_Calculate.cs:53-68`, literal `481` in the name; `= CST.k24 − Σ others`). Only `481_*_Check1` is unreachable.
The pre-formula must be kept.

### 2.3 Hard-coded streams resolved in FLERT (FDB, `FlaringStream ⋈ FlaringStreamType`)

| Use | Number | Stream ID | Cases (type ID: name) |
|---|---|---|---|
| CONT300 | 416 | 317 | 1538 SOR, 1539 EOR (`Units 331-332 - … Rev D04`) |
| CONT30X | 2D-70 + 1D-57 / CD-100 / CD-72 | 19 / 21 / 146 (offshore) | 982/983, 986/987, 1207/1208 (`370 Summer/Winter`) |
| TOX | 14M (Methanol) | 572 | 2000 EOR, 2001 SOR |
| TOX | 412 / 415 / 453 | 313 / 316 / 361 | 1530/1531, 1536/1537, 1618/1619 |
| TOX | 417 | **318, 319, 575** | 1540 SOR, 1541 EOR, 2006 EOR/2007 SOR |
| TOX | 481A / 481B | 574 / 573 | 2004/2005, 2002/2003 |

### 2.4 Which streams are actually used (LDB, 2026-01-01 … 2026-10-07; history tables empty)

| Consumer | Rows | Distinct streams / cases | Resolution check |
|---|---|---|---|
| INT (events `AF_FlaringsIntermittent_SG`) | 2 899 events (onshore 1 657 → INT300; offshore 1 242 → INT30X) | 63 streams / 109 cases | 0 events without stream, 0 without case, 0 onshore mismatch, 0 "No composition" (join to FDB) |
| CONT (`CalcResult_FlaringsContinuous_SG`) | 1 096 day-rows | GC 1581 (416 SOR) 274 d; offshore 996/1000/1228 (Summer) 183 d each, 997/1001/1229 (Winter) 91 d each | — |
| TOX (`CalcResult_FlaringThermaloxidizer`, monthly) | 228 rows (IsSour 0/1/2), 5 475 daily rows | SOR only in 2026: GC 2044 (14M), 1573 (412), 1579 (415), 1581 (416), 2050 (417→575), 1661 (453), 2046 (481B); **no EOR rows** | — |
| **Union** | | **71 streams / 118 cases of 523 / 982** | FDB query (used set) |

Top INT cases by events: stream 100 "Average-weighted stream" `ПК-3 (370-220) лето` 324 / `зима` 189; 26 (14) `Process Datasheet KE01-A1-560-KD` 277;
19 `370 Summer` 221 / `Winter` 125; 121 "Vent Gas from Tank" лето 183 / зима 96; 14 (1D-2) Winter 175 / Summer 168; 17 (1D-11) Winter 159;
107 (378) `Units 331-332 - SOR Rev D04` 120. Case texts by events: `ПК-3 … лето` 745, `370 Winter` 609, `370 Summer` 512, `ПК-3 … зима` 480,
HMB_U380_* 121, others 312. ⇒ For INT the season is chosen **upstream in PI** (event carries the case text); legacy calc only matches text.

---

## 3. Legacy formulas that use the composition, and output columns

| Calc type | Formulas (all / with `@GCV`) | Calculation list in code | Output table (LDB) | Key output columns |
|---|---|---|---|---|
| INT30X | 80 / 41 | `R_Air_HSE30X_Int_SG_Calculate.cs:28-33` (29 `Common_WtCi_*` + 41 mass/props + LHV/TJ/GHG EF + 28 `EC_*` + 3 GHG + 4 analyzer) | `CalcResult_FlaringsIntermittent_SG` (2 899 rows 2026, `FCID` = `GasComposition_ID`, 109 distinct) | `Density, MolecularWeight, TotalSulphurContent, TotalSumComposition, <45>_mass, TotalSumComposition_mass, FuelLowerHeating, TJThousand, GHGEmissionFactor, VolumeFlaredSourGas, EC_{NO2,NO,Soot,SO2,H2S,CO,CH4,C4H10S,CH4S,C5H8S,C2H6S}_{tonn,gsec}, EC_Wist/Wzv/Wist_Wzv, GHG_{CO2,CH4,N2O}_tonn, An_*` |
| INT300 | 92 / 54 | `R_Air_HSE300_Int_SG_Calculate.cs:25` | same table | same |
| CONT30X | 76 / 42 | `R_Air_HSE30X_Cont_SG_Calculate.cs:46` | `CalcResult_FlaringsContinuous_SG` (1 096 rows 2026) | same family |
| CONT300 | 86 / 52 | `R_Air_HSE300_Cont_SG_Calculate.cs:44` | same | same |
| TOX SG | 152 / 118 | `R_Air_HSE_Thermaloxidizer_SG_Calculate.cs:74-78, 86` (`TotalSumComposition, SumMCmHn, SumNCmHn, SumMNCmHn, MolecularWeight, Density0, Density20, Vwdg, Vog, VoH2O, Vo, Sr, ConcNitrogenOxides, ConcCarbonMonoxide, 47 _mass, GHG1, GHG2, GHGEmissionFactor, CalculateMethodResult, CO2_tonne, <gas>_Check1`) | `CalcResult_FlaringThermaloxidizer` (monthly, 228) + `…Daily` (5 475), `FCID_SG` | composition columns + results per gas column |
| Common shared | 49 (`Common_WtCi_<X> = k1·CmHnP_GCV_X/MW_GCV_X`, constants only) | used by INT/CONT/HSE400 | — | — |

Downstream consumers of these outputs in legacy (LDB `sys.sql_modules`): `Report_Air_Flarings_Intermittent_SG`, `Report_Air_B1/B4_230_*`,
`Report_Air_A1_230_*`, `Report_Air_IEC_Get30XRawMetrics`, `Report_Air_IEC`, `fn_Air_300/301/302_Emissions_{tonn,gsec}`,
`Report_Air_Flarings_Thermaloxidizer`, `Utility_RecalculateReportPeriod`; triggers `FlareDataContinuousTrigger`, `AF_HSE_Thermaloxidizer_Trigger`.
Jobs: `DllProject_Air_Flaring_Intermittent_SG` / `_Continuous` scheduled (`SQL\Jobs.txt:779-1134, 1636-1996`).

---

## 4. ECR mapping and gaps

### 4.1 What exists in ECR (EDB, 25.11)

| Object | State | Query |
|---|---|---|
| `STREAM` (def 80) | 523 entries; fields `LEGACY_ID` (= FLERT ID = event `GasCompositionID`), `NUMBER, NAME, IS_ONSHORE, IS_SOUR, IS_HMB, HMB_DOC, GROUP, EXCLUDE_IN_LIST`; alt key `BY_LEGACY_ID` | `cfg.RegistryDef/FieldDef/KeyDef` |
| `STREAM_CASE` (81, temporal) | 982 versions, all `ValidFrom=2022-01-01`, `ValidTo` NULL; fields `STREAM, CASE_NAME, T_C, DENSITY_OP, MW_HYSYS, Z, RULE_PARSE, LEGACY_TYPE_ID, LEGACY_COMPOSITION_ID, NO_COMPOSITION, FLERT_ALIASES`; PK stream+case | `dic.RegistryEntry` |
| `GAS_COMPOSITION` (82) | 22 725 rows (`CASE` → version entry, `COMPONENT`, `MOL_PCT`); zero = no row | |
| `COMPONENT` (79) | 46 codes = the union in §1.2; `MW/LHV/N_*` **empty** | |
| `cfg.RegistryUse` for 79–82 | **0** (544 uses total, 13 registries; `LAND_HSE400_STREAM` 129) | `cfg.RegistryUse` |
| Methodologies | `Common` (V1, Engine 1, status 0, contains all `*_GCV_*` constants), `HSE400` (1.0.1.0 published), `ECW_C05…C12`, `TEST`. **No `Flert`, no `Thermaloxidizer`** (legacy: 407 / 640 formulas, own constants 129 / 47 names) | `calc.Methodology(Version)`, `calc.MethodologyConstant` |
| Templates | `HSE400, HSE401, LAND_UI, T401, TEST_COORD` — **no flaring template** | `cfg.Template` |
| Flare events | ✎ R3 B: the **engine exists** on `origin/main`: `SourceEventMap` (event → row of a Dynamic table, `$start/$end` mandatory, `LookupByCode/ByName/ValueMap`, single-attribute **equality** `SetFilter(attribute, scope, value)`, volume modes `None/EventAttribute/RowWindow`), `SourceEventSyncJob`, `PiSqlClientDataSource` + `SourceEventFolder`, `SourceEventPeriods.Locate` (event → period of its **start** in project TZ, D-179), `FlareEventArguments` (`CalculationInputBuilder.cs:142`). **Configuration is missing:** live `ext.DataSource` = `FLERT` only, `ext.SourceEntity`/`SourceEventMap` = 0 rows; `GasCompositionID` has 0 hits in code (it is just a mapped attribute) | `Domain/Entities/External/SourceEventMap.cs`, `Enums.cs:836-892`; EDB `ext.*` |
| Projects | `doc.Project.PeriodKind`: HSE400 / HSE401 = 2 (Yearly), LAND_UI = 0 (**Monthly**), TZ `Asia/Atyrau` → monthly projects are supported | EDB `doc.Project` |
| Script engine facts | registry snapshot = one per run at **`period.End`** (`Ecr.Calculations/ScriptCalculationModule.cs:551-555`); `cfg.RegistryUse` written only for `script.RegistryCodes` of the **script itself**, not its imports (`ScriptMaterializer.cs:297-313`); `reject when` rejects on true / empty / error (`docs/user/methodology-script.md` §9); `rows from … where` = equality / `in` only (§3); `period.Month/Days/EndKey` (§16) | `origin/main` |
| Registry functions | `REGFIND/REGONE/REGFIELD/REGSUM/REGCOUNT…` allowed also in `NumericMode=Legacy` (D-426); literal registry code makes the dependency visible (`docs/admin/admin-guide.md` ~l.348); script registry scopes `ScriptRegistryScopeReader` | `origin/main` |

### 4.2 Required consumers in ECR

| Legacy consumer | ECR methodology (new, Script) | Template / sheet (new) | Reads from ECR composition | Status |
|---|---|---|---|---|
| INT30X + INT300 | `FLERT_INT_SG` (two branches by `TypeReportID` 300 vs 301/302, formulas imported 1:1 from `Flert` + `Common`) | HSE30X flaring document: table "Intermittent SG events" (1 row = 1 PI event: EFID, start/end, duration, flare unit, location/category, `GasCompositionID`, `StreamType`, total volume) | case by (LEGACY_ID, CASE_NAME) in the snapshot of the event start date (`registry as of`, §5.2a) → 34/45 components + `T_C` | **gap: methodology, template, event-map configuration** (engine exists, §4.1) |
| CONT30X + CONT300 | `FLERT_CONT_SG` | same document: table "Continuous sour analyzers" (1 row = day × location, volume) | onshore: stream 317 case `…SOR…`; offshore: split registry (§5.4) × season case | **gap** |
| TOX SG | `TOX_SG` (SG part of `Thermaloxidizer`; FG part = HSE400, AGR part separate) | TOX document: train × mode × day/month × gas column | per gas column fixed stream + SOR/EOR case → 48 names (46 components + 2 literal 0) | **gap** |
| Constants | `Common` already in ECR (MW/LHV/CmHnP …) + import `Flert`/`Thermaloxidizer` own k-constants (129/47 names) | — | — | partial |
| Outputs to reports | HSE401 / Land / IEC consumers [A: which ECR document aggregates flaring totals is not decided here] | — | — | open |

---

## 5. Proposed binding design

✎ 25.11 rev 2: §5 rewritten after the R3 review (`.sync-local\design\REVIEW-sg-flaring.md`, verdict FAIL). The superseded rev 1 text is struck through at the end of §5.

### 5.0 Binding human decisions (25.11)

| # | Decision | Where applied |
|---|---|---|
| H1 | All 3 consumers (INT, CONT, TOX SG) in one release | §5.4 |
| H2 | Events from PI AF event frames | §5.4 |
| H3 | TOX EOR uses the **EOR** composition (L1 fixed) | §5.6 |
| H4 | Event at a version boundary → version valid at **event start** | §5.3 |
| H5 | TOX columns → stream id (417 → 575) | §5.6 |
| H6 | Apr–Sep = Summer | §5.6 |
| H7 | 19/21/146 split = admin-edited registry | §5.7 |
| H8 | Legacy formulas through the GCV bridge | §5.1–5.2 |
| H9 | Missing composition = missing input, **never 0** | §5.5 |
| H10 | **L6 fixed:** the 481 column is chosen by the real train value (`Train 1` → 481A / stream 574, `Train 2` → 481B / stream 573) | §5.6, §6 |
| H11 | ✎ rev 3. Human 25.11, verbatim: «для факелів ми беремо дані з аф і для кожного рядку рахуємо». One row = one AF event, computed with the composition valid at **that row's event start date**, whatever the template's period (monthly or yearly; live HSE400/HSE401 projects are yearly with month columns). The rev 2 "monthly template + `period.Days > 31`" design is **rejected** | §5.2a, §5.3 |
| H12 | Q-A: HSE372 Excel is the TOX fallback when AF has no TOX event frames. Q-C: "rejected + reason text" is the accepted form of "missing input" | §5.4, §5.5 |

### 5.1 Principle and the inline-vs-library choice (review R1)
Keep the imported legacy formula texts unchanged (parity, D-NEW 24.11 "Script only, lossless"). Only the **argument source** changes: the
C# loop "property → `GCV_<X>`" is replaced by script `var`s read from the ECR registry. No new tables. No migration.

**Choice: registry reads are generated inline in every consumer script (`FLERT_INT_SG`, `FLERT_CONT_SG`, `TOX_SG`, `TOX_SG_MONTHLY`). There is no
`GASCOMP` library.**
- `ScriptMaterializer.ReplaceRegistryUsesAsync` (`ScriptMaterializer.cs:297-313`) writes `cfg.RegistryUse` only for `script.RegistryCodes` of the
  version being published. Imports are not followed (`ScriptRegistryScopes.cs`). Recalc dependents (`GasCompositionStore.ListDependentYearsAsync`,
  `RegistryImpactStore.QueryAsync`) and the lock (`ListLockedPeriodsAsync`) join `RegistryUse.SourceId = CalculationResult.MethodologyVersionId`.
  With inline reads, that join already returns the consumer version, so AC-0 and the lock work **without a code change**.
- The rejected alternative is a contract change: transitive `RegistryUse` for imports. It would change `RegistryUse`, recalc fan-out and lock scope for
  **every** existing methodology that imports a library. That is R3 risk outside this card, for no functional gain here.
- Cost of the choice: the same ~50-line block appears in 4 scripts. It is generated from one source (the bridge generator, §5.2), so the
  generator is the single point of truth, and a generator test compares the 4 blocks.
- `import Common` remains allowed: `Common` V1 has no registry reads (EDB `ScriptText`: `CHARINDEX('REG')=0`), so nothing is lost from
  `RegistryUse`.

### 5.2 Generated inline block (bridge, H8)

```ecr
// INT example; CONT/TOX differ only in how `stream`/`caseName` and the as-of date are chosen (§5.6)
rows from INT_SG_EVENTS;
registry as of row.EVENT_START;                                                        // ✎ rev 3, NEW clause (§5.2a): all registry reads of this row use the version valid at this date
var stream   = REGONE("STREAM", ROW.LEGACY_ID == row.GAS_COMPOSITION_ID);            // 523-row scan; not found → #N/A → row rejected
var gcase    = REGONE("STREAM_CASE", ROW.STREAM == stream && ROW.CASE_NAME == row.STREAM_TYPE);  // the case version valid at EVENT_START
var GCV_H2S  = REGSUM("GAS_COMPOSITION", ROW.CASE == gcase && ROW.COMPONENT == entry("COMPONENT","H2S"), ROW.MOL_PCT);  // ROW.CASE first → indexed
… one var per referenced component (34 / 45 / 46) …
var GCV_Helium = 0;  var GCV_C2H6S = 0;                                                // TOX only; literal 0 (entry() would fail at save)
var t_c = REGFIELD(gcase, "T_C");
var GCV_Temperature = t_c == null ? 0 : t_c;                                           // ✎ rev 3 (C3): documented ?: form, not `??`; legacy NULL → "0"
```
Generator tests (C3): the emitted text contains no `??`; with a case where `T_C` is null, `GCV_Temperature = 0` and `EC_Wzv` is computed (not rejected).
- The generator rewrites `@GCV.<X>` → `GCV_<X>` and `@GCV_SOR_<X>` / `@GCV_EOR_<X>` → `GCV_SOR_<X>` / `GCV_EOR_<X>` inside the imported formula texts.
  Aliases: formulas use only `CO/H2/O2`, which are the ECR folded codes.
- A component with no row gives `REGSUM = 0`. That is the legacy NULL→"0" rule, and it applies **only inside an existing, valid case**. Missing or empty
  cases are rejected first (§5.5).
- `REGONE` is used instead of `REGFIND` because `REGFIND` takes the primary key only (`S<id>` for STREAM) and has no alternate-key argument. If the
  script language turns out to have string concatenation, `REGFIND("STREAM","S"&id)` is an optional optimisation. The `REGONE` form is the baseline.

### 5.2a ✎ rev 3: per-row dated registry read (engine change, H11)

**Today:** one snapshot per run at `period.End` (`Ecr.Calculations/ScriptCalculationModule.cs:551-555`, `RegistriesAsync` l.508), handed to
every row's `ScriptEvaluationContext` (`_registries`, l.39/84). The building block for dated snapshots **already exists**:
- `IRegistrySnapshotLoader.LoadSourceAsync(ids, asOfUtc)` reads the DB once.
- `IRegistrySnapshotSource.Build(DateOnly)` builds a snapshot for any business date, in memory only (L5-12; `Ports/IRegistrySnapshotSource.cs`).
- Already used per date by `Recalculation/TemplateRegistrySnapshots.cs:28,85` and `Registries/Rules/RegistryRuleEngine.cs:63-64,156`.

**Language (contract = script language only):** a new top-level clause `registry as of <expr>;`
- At most one per script, after `rows from`. The expression is evaluated **per row** and must be a date/time (typically `row.EVENT_START`, which is `$start`).
- The UTC value is converted to a calendar date in the **project TZ** (`SourceEventPeriods.ToProjectTime`, `Asia/Atyrau`).
- Every registry read of that row (`REGFIND/REGONE/REGFIELD/REGSUM/REGCOUNT/REGAVG/REGMIN/REGMAX`, `ROW.<lookup>.<field>` paths) then sees the
  snapshot built for that date.
- The row also gets a read-only `asof.Date` / `asof.Month` / `asof.Year`, used for the season (§5.6). In a yearly document `period.Month` is meaningless.
- Without the clause nothing changes (snapshot at `period.End`), so it is a zero-impact default for every existing methodology.
- Null or error date → the row is **rejected** ("registry as of date is empty"), same semantics as `reject when` (§9 doc).
- Date outside `[period.Start, period.End]` (project TZ) → rejected ("registry date outside the document period"). This prevents reading future versions.
- `entry("R","code")` still resolves ids at save (unchanged).
- New compile diagnostics: `duplicateRegistryAsOf`, `registryAsOfNotDate`, `registryAsOfWithoutRegistries`. Each needs a catalog key in its own seed section.

**Engine change scope (files, `origin/main` ecdd84621):**

| # | File / area | Change |
|---|---|---|
| E1 | `src/Ecr.Scripting` parser + AST + `Binding/ScriptBinder.cs` (`NeedsRegistries`, l.295/585/1227) | parse and bind the clause; `CompiledScript.RegistryAsOf` (expression); `RegistryCodes` collection **unchanged** |
| E2 | `Ecr.Scripting/…/ScriptRegistryScopes.cs` (+ `Application/Calculations/Scripting/ScriptRegistryScopeReader.cs`) | registries read under `as of` get the scope "whole period" instead of "period end" (dependency graph B179) |
| E3 | `src/Ecr.Application/Ports/ICalculationModule.cs:263-311` `RegistrySnapshotCache` | add `GetOrLoadSourceAsync(ids, asOfUtc)` and a per-date memo keyed `(idsKey, validityInterval)` (E6), shared by all documents of one run |
| E4 | `src/Ecr.Calculations/ScriptCalculationModule.cs:508-555` `RegistriesAsync` | if any script of the program has `RegistryAsOf`: load the **source** once, keep the default snapshot = `Build(period.End)`, and put the source + memo into the binding |
| E5 | `src/Ecr.Calculations/ScriptEvaluationContext.cs:39,84` | per row: evaluate the `as of` expression, convert to project-TZ date, pick `memo.GetOrBuild(date)`, expose `asof.*`; trace one step per row "registry as of 2026-03-14" |
| E6 | new `Application/Registries/RegistryValidityIntervals.cs` | from the loaded source, the sorted set of all `ValidFrom/ValidTo` dates of the loaded registries → a date maps to an interval index; **one `Build` per interval, not per day** |
| E7 | `MonthColumnArgumentsModule.cs:144-179` (yearly documents with month columns, HSE400/HSE401 shape) | no change: it goes through `PrepareScriptAsync`, so rows of a yearly document get their own date. Covered by a test |
| E8 | `docs/user/methodology-script.md` (new section after §9), `docs/build/02b` engine notes | documentation |

**Not changed:** no API, no `openapi.snapshot.json`/`schema.d.ts`, no EF migration, no `ErrorCodes.cs`. `GenericCalculationModule` (Formula
engine) is untouched. Script only, per D-NEW 24.11.

**`cfg.RegistryUse` keeps working:** the registry codes are still literals in the consumer script (inline, §5.1). `ReplaceRegistryUsesAsync`
(`ScriptMaterializer.cs:297-313`) writes the same rows. The clause adds no registry and removes none.

**Recalc keeps working:** `ListDependentYearsAsync` / `RegistryImpactStore.QueryAsync` work per registry × **year**. A version that starts in March of a year
recalculates every open document of that year that reads the registry, monthly or yearly.

**System time:** `RegistryAsOfUtc` is unchanged. The source is loaded `FOR SYSTEM_TIME AS OF` the run moment, so a re-run reproduces the result.

**409 lock keeps working, with one required check:** the lock (`GasCompositionStore.ListLockedPeriodsAsync`, `:187`) must compare the edited version
window with the **whole window `[PeriodStart, PeriodEnd]`** of each locked period, not with its end date. In a yearly approved or closed document, an edit
from March must therefore be refused. Lane test: yearly approved document + new version from 2026-03-01 → 409; monthly closed January +
version from February → allowed.

**Tests (engine, R3 within the card):**
- Scripting: parse/bind and diagnostics.
- Calculations:
  - two rows in one document with dates on both sides of a version boundary read different `MOL_PCT`;
  - a row without the clause reads `period.End` (regression);
  - null date → rejected;
  - date outside the period → rejected;
  - yearly document with month columns.
- Infrastructure real-SQL: temporal versions + `RegistryAsOfUtc` re-run.
- Mutation: replace the per-row snapshot by the default → the boundary test goes red.

### 5.3 Version selection (H4, H11; independent of template/period)
- **Version = the one valid at the row's event start date** (project TZ), via `registry as of` (§5.2a). This is H4 and H11.
- **The template/period choice is independent:** the same scripts run in a monthly or a yearly project, and in a yearly project with month columns.
  The rev 2 guard `reject when period.Days > 31` is **removed**.
- Row dates per consumer:

| Consumer | As-of date |
|---|---|
| INT | `row.EVENT_START` (`$start`) |
| CONT | the day of the daily EF (`$start`) |
| TOX daily | the row date |
| TOX monthly aggregate rows | the row's month start (static rows carry `MONTH`) |

- Cross-month events (X1): the event is attributed to the period of its start (`SourceEventPeriods.Locate`, D-179), and it reads the version of its start date.
- Season (CONT, H6): `asof.Month` in 4…9 → Summer, else Winter. Project local time.

### 5.4 Event ingestion: configuration and contract (review R5, H1/H2)
**Prerequisite (human/DBA):** an `ext.DataSource` for PI AF (`PiSqlClientDataSource`, read-only account, AF database the legacy queried as
catalog `Master`, proc `AF_Air_Flaring_LP_Sour_Analyzers_UsingGas_Get`). Live has only `FLERT` (EDB `ext.DataSource`). This blocks AC-0 on live.

| Consumer | AF source (legacy evidence) | ECR table (Dynamic) and `SourceEventMap`s | Mapping | Filter / grain |
|---|---|---|---|---|
| INT | 4 AF views `FlareEventFull_{Auto,Auto_Day,Manual,Manual_Day}_V` (proc `AF_Air_Flaring_Intermittent_SG_Data_Get`) | `INT_SG_EVENTS`; one map per AF source entity (4) | `$start/$end`; `TOTAL` = `AdjustedV` (VolumeMode `EventAttribute`); `GAS_COMPOSITION_ID` Direct (number); `STREAM_TYPE` Direct (text); `LOCATION` **ValueMap** → new registry `FLARE_LOCATION` (5 values in LDB: `Onshore - HP Flare` 474, `Onshore - LP Flare` 1 183, `A Island - HP Flare` 59, `D Island - HP Flare` 497, `D Island - LP Flare` 686) with fields `IS_ONSHORE`, `TYPE_REPORT` (300/301/302), `FLARE_UNIT` (HP/LP); `IS_AUTO`, `EQUIPMENT/TAG/UNIT` Direct | **`SetFilter("FluidDepressurized", Event, "Sour gas")`** — equality is enough: the attribute is a closed set (LDB: SG `Sour gas` 2 899; FG `Fuel gas` 4 819, `Nitrogen` 1 500), so it is equivalent to legacy `INSTR(…,'sour')>0` on all observed values. **No contract change, no migration.** Risk: a new spelling would be dropped silently by the filter, and ECR keeps only the events that pass, so a check inside ECR cannot see it. ✎ rev 3 (C1): the check therefore reads **AF itself, before the filter**. A read-only PI SQL query on the same `ext.DataSource` (tool `tools/flaring/Check-AfFluidValues.ps1`, run by the coordinator's hourly check) runs `SELECT DISTINCT FluidDepressurized` over the 4 EF sources for the last 35 days and alerts on any value ∉ {`Sour gas`, `Fuel gas`, `Nitrogen`}. TypeReportID and IsOnshore come from `FLARE_LOCATION` (replaces the legacy substring rule, `PrepareData_…_Intermittent_SG` l.99, 196-207). **L8:** an event frame without `$end` is not ingested (`$end` mandatory); the sync re-reads it after it closes [A: verify that `SourceEventSyncJob` picks up a later-closed EF, test in lane] |
| CONT | daily wide EF (`AF_FlaringsContinuous`: 1 EF/day, 16 location attributes) | two tables `CONT_SG_ONSHORE`, `CONT_SG_DISLAND`; one map each on the daily EF template | VolumeMode `EventAttribute`: `Onshore_LP_Sour_Analyzers` / `DIsland_LP_Sour_Analyzers`. Location is implied by the table (FieldMap has no constant source, `Enums.cs:868`) | 1 row/day per table. **3-stream split:** the D-Island row computes the 3 streams inline from `LP_SOUR_SPLIT` slots 1–3 (§5.7) and outputs per-slot results (`…_S1/_S2/_S3`, parity key (day, stream)) plus the day total |
| TOX | legacy `AF_Air_HSE_Thermaloxidizer` ← Excel `HSE372_Thermaloxidizers_v1.0.2.0.xlsm` (`Con_File_Name`); `Date` nvarchar `M/d/yyyy`; 40 junk rows `Train='0'` | `TOX_DAILY` (train × mode × day, volumes/oper-times per gas column); `TOX_MONTHLY` static rows carry `MONTH` (1st of month), so they work in a yearly document too | H2: from the AF event frames that hold these rows. If AF has no TOX event frames, the fallback is Excel import of HSE372 into the same table (H12, decided). Excel path: date parsed invariant `M/d/yyyy`; junk excluded by `rows from TOX_DAILY where row.TRAIN in ("Train 1","Train 2")` | **monthly grain = legacy "SUM of daily volumes, then recalc"** (`PrepareData_GetFiltered_HSE_ThermaloxidizerByTrain`): methodology `TOX_SG_MONTHLY` over a static table `TOX_MONTHLY` (one row per train × mode × type × column), with inputs = `table` SUM of `TOX_DAILY` (script §13). It is **not** a Σ of daily results |

### 5.5 Reject guards: never 0 (review R4, H9)
The engine raises `MissingArguments` only for empty argument cells (`ScriptCalculationModule.cs:289,429-451`). A registry miss becomes a **rejected row**: no output,
status `rejected`, reason = condition text (`methodology-script.md` §9; true / empty / error all reject). **This is accepted as H9's "missing input":** no
value is written and the issue is visible. A parameterised reason ("stream 26 case …") would be a contract change and is not needed. The condition variables are
named so that the condition text reads as the reason.

| Path | Silent result without a guard | Guard (generated, first statements of each consumer) | Test |
|---|---|---|---|
| ✎ rev 3: empty / out-of-period as-of date | wrong version | built into `registry as of` (§5.2a): null/error or outside `[period.Start, period.End]` → rejected | null `$start`; date in the next year |
| ✎ rev 3 (C5): AF location not in `FLARE_LOCATION` ValueMap | `LOCATION` empty → onshore check / TypeReportID undefined | `reject when row.LOCATION == null;` (and the SourceEventLink shows the unmapped value) | event with location `E Island - HP Flare` → visible rejected row |
| null `GAS_COMPOSITION_ID` / `STREAM_TYPE` | `REGSUM(ROW.CASE == null)` = 0 | `reject when row.GAS_COMPOSITION_ID == null \|\| row.STREAM_TYPE == null;` | null key → rejected |
| stream / case not found or ambiguous | `#N/A` | `var no_stream_for_event = REGCOUNT("STREAM", ROW.LEGACY_ID == row.GAS_COMPOSITION_ID) != 1; reject when no_stream_for_event;` and the same for the case | unknown id, unknown case text |
| onshore mismatch (A8) | wrong case silently | `reject when REGFIELD(stream,"IS_ONSHORE") != REGFIELD(row.LOCATION,"IS_ONSHORE");` | mismatch → rejected |
| `NO_COMPOSITION` / Σ = 0 (16 FLERT cases) | all emissions 0 | `var case_without_composition = REGFIELD(gcase,"NO_COMPOSITION") == true \|\| REGCOUNT("GAS_COMPOSITION", ROW.CASE == gcase) == 0; reject when case_without_composition;` | NO_COMPOSITION case → rejected |
| CONT split missing / bad | no output / wrong volume | `reject when REGCOUNT("LP_SOUR_SPLIT", true) != 3 \|\| ABS(Σ COEF − 1) > 1e-9;` | 2-slot / Σ 0.9 → rejected |
| TOX column without pin or without SOR/EOR case | Null | `reject when` pin count ≠ 1 or case count ≠ 1 | removed pin → rejected |
| Helium/C2H6S | save error | literal 0 (§5.2) | compile |

### 5.6 Stream/case choice per consumer (H3–H6, H10, review A8/A9)

| Consumer | Stream | Case | Notes |
|---|---|---|---|
| INT (300 / 301 / 302 by `FLARE_LOCATION.TYPE_REPORT`) | `LEGACY_ID == row.GAS_COMPOSITION_ID` and `IS_ONSHORE` = location's | `CASE_NAME == row.STREAM_TYPE` (exact text; the season is chosen in PI) | branch formulas: HSE300 (45 comp) vs HSE30X (34) |
| CONT300 | pin in `TOX_GAS_STREAM`-style registry row `CONT_ONSHORE` → stream 317 (`416`) | `CASE_NAME` contains `SOR` → count must be 1 (exact match on `Units 331-332 - SOR Rev D04` in the pin row) | |
| CONT30X | `LP_SOUR_SPLIT` slot → stream (19 / 21 / 146 by **Stream_ID**) | `asof.Month` in 4..9 → Summer, else Winter (✎ rev 3: row date, not `period.Month`), case name stored as pin field `CASE_SUMMER` / `CASE_WINTER` per slot (no `contains`) | H6; Cyrillic cases unused as in legacy |
| TOX | `TOX_GAS_STREAM` (key COLUMN + TRAIN): 14→572, 412→313, 415→316, 416→317, **417→575**, 453→361, **481 / Train 1 → 574 (481A)**, **481 / Train 2 → 573 (481B)** | pin fields `CASE_SOR`, `CASE_EOR` (exact names, e.g. `Units 331-332 - SOR Rev D04`) | H5, H10 (L6 fixed); **H3: `GCV_EOR_*` read from the EOR case** (L1 fixed); 481 H2S pre-formula kept (`HSE_TO_SG_481_<type>_H2S` overrides `GCV_<type>_H2S`); SG never reads HSE400 → **L7 not inherited** (a missing HSE400 month only rejects the TOX FG rows) |

Exact case names in pin rows replace the legacy `Contains("SOR"/"EOR"/season)` with a deterministic choice. The `where`/registry conditions are equality-only, and
`FirstOrDefault` is gone.

### 5.7 New registries: seed, rights, lock, rollback (review R6/F)

| Registry | Kind | Key | Fields | Seed rows |
|---|---|---|---|---|
| `FLARE_LOCATION` | Local, non-temporal | CODE (AF text) | `IS_ONSHORE`, `TYPE_REPORT`, `FLARE_UNIT` | 5 (§5.4) |
| `LP_SOUR_SPLIT` | Local, **temporal** | SLOT (1–3) | `STREAM` (lookup STREAM), `COEF`, `CASE_SUMMER`, `CASE_WINTER` | v1 2024-01-01…2024-12-31: S19 3/7, S21 3/7, S146 1/7; v2 from 2025-01-01: 0.4/0.4/0.2 (AF xml l.3950973, 3951042) |
| `TOX_GAS_STREAM` (+ row `CONT_ONSHORE`) | Local, **temporal** | COLUMN + TRAIN (`*` = both) | `STREAM`, `CASE_SOR`, `CASE_EOR` | 8 TOX rows + 1 CONT row, from 2022-01-01 |

- **Seed (C5):** section **`-- SGF: sour-gas flaring registries (FLARE_LOCATION, LP_SOUR_SPLIT, TOX_GAS_STREAM)`**, appended to `09-seed.sql`
  (hot-file rule: own section, ids from the next free range). Definitions + rows. Idempotent MERGE. No EF migration. The ready letter names it in its line
  "Seed: `09-seed.sql` section `-- SGF:`" (rule: seed listed in the ready letter). Catalog keys for the new script diagnostics (§5.2a) and for the guard
  messages go in their own `-- SGF:` block of the catalog seed.
- **Rights:** view = `Registry.View`; edit = `GasComposition.Edit` (SystemAdministrator only, the same rule as the composition, DESIGN-gas-composition §2).
- ✎ rev 3 (C4). **Lock: closed months must not change.**
  1. **Store:** overload `IGasCompositionStore.ListLockedPeriodsAsync(IReadOnlyCollection<int> registryDefIds, CancellationToken)` (today it takes
     `GasRegistryIds`, `Ports/IGasCompositionStore.cs:118`, `GasCompositionStore.cs:187`). Same `RegistryUse.SourceId = CalculationResult.MethodologyVersionId`
     join, filtered by the given registry ids. It returns period windows `[PeriodStart, PeriodEnd]` (whole window, §5.2a).
  2. **Guard:** `ExternalRegistryGuard` (`Application/Registries/ExternalRegistryGuard.cs`, today `IsGasCompositionRegistry` l.43,
     `EnsureManualEditAllowed` l.51) gets `EnsureFlaringEditAllowedAsync(definition, oldWindow, newWindow, user, ct)`. It applies to the codes
     `LP_SOUR_SPLIT`, `TOX_GAS_STREAM`, `FLARE_LOCATION` (non-temporal = window (−∞, +∞)). It:
     - requires `GasComposition.Edit` (else 403 `err.ECR-AUTH-0403.permission`);
     - refuses `409 err.ECR-REG-0409.gasCompositionLocked` (lists the periods) if **either** the old or the new validity window of the entry overlaps a locked
       period window.
  3. **Callers:** every generic writer path that references the guard must call it. That is 21 files on `origin/lane/integ-b12-fix` (`git grep -l "ExternalRegistryGuard."`):
     upsert, batch, CSV/Excel import, delete/restore, validity edit, entry move, import apply, etc. **One test per path:** an edit overlapping a closed month →
     409; an edit in an open month → allowed + recalc queued; a user without `GasComposition.Edit` → 403. An architecture test asserts that every writer
     entry point calls the guard (list of entry points vs calls).
  4. **Stated limitation:** a closed month whose rows were **all rejected** has no `CalculationResult`. It is therefore **not locked** and may be edited. A
     closed period is not recalculated either, so nothing silently changes in it. The data still has no result for that month, which is visible as rejected rows.
  - Audit = the generic `RegistryValueChanged`. Corrections = a new version from the first open month.
- **Prerequisites:** (1) **publish `Common` as Script** first (live: Common V1 Engine 1 **Status 0 = Draft**, `calc.MethodologyImport` empty; consumers that
  `import Common` otherwise fail `importNoVersion`). (2) Import the `Flert` (129 names) and `Thermaloxidizer` (47) own k-constants into the consumer versions.
  (3) Correct `docs/build/archive/bridge-thermaloxidizer.md` (481 H2S is reachable).
- **Rollback (no data loss):** deactivate the `SourceEventMap`s; switch the template binding back and deprecate the 4 methodology versions; revert the flaring
  template to its previous version (or archive the project if first release); **keep** all registry rows and results (history). Composition registries are untouched.

### 5.8 Recalculation (review R3)
- **Dropped:** the per-run read set (it would need a migration, and grep `ReadSet|EntriesRead` = 0). AC-3 is re-scoped to the **existing** path: gas edit →
  coalesced `IRegistryImpactRecalculationJob` per registry × year → documents with a current result whose methodology version uses the registry
  (inline reads, §5.1), open/grace periods only, approved sheets marked stale (`DataChangedAt`), not recalculated.
- **Double enqueue:** `GasCompositionService.cs:366/441/477` (`origin/lane/integ-b12-fix` 184bc11f6) enqueue targets for **both** `STREAM_CASE` and
  `GAS_COMPOSITION`, and the consumers read both, so there are 2 jobs per year for the same documents. Lane test: one version edit → each dependent document
  is recalculated **once**. If the test fails, the lane enqueues a single target per year for case+composition operations (dedupe by year). This is in scope,
  not debt.
- Split/pin registry edits use the same machinery, via the generic writer's existing impact enqueue.

### 5.9 Performance (✎ rev 3: with per-row dated snapshots)
- **Snapshot cost:** one `LoadSourceAsync` per run (registry set of the program, shared by all documents through `RegistrySnapshotCache`).
  Then **one `Build` per validity interval** (E6), not per row and not per day.
  - Live today: every composition version is from 2022-01-01, so the composition has 1 interval. The split has 2 (2024 / 2025+), the pins 1. A year has ≤ ~3
    intervals, plus 1 per future monthly composition version (≤ 12/year).
  - A `Build` filters ~25 k entries/values in memory, ≈ milliseconds.
  - Memory: ≤ ~15 snapshots × a few MB per run.
- **200 documents, a few thousand events/year** (legacy 2026: 2 899 INT + 1 096 CONT + 228/5 475 TOX in ~9 months): per row ≈ 2.5 k row touches (below),
  i.e. ≈ 10–25 M row touches per full-year run over all documents. That is seconds to tens of seconds, dominated by formula evaluation, not by snapshots.
- Document fan-out: whatever the template defines (monthly 12/year or yearly 1/year per area). The cost is per row, not per document.
- Per INT row: `REGONE` STREAM (523) + `REGONE` STREAM_CASE (982 in snapshot) + ≤ 46 **indexed** `REGSUM` (`ROW.CASE == gcase` first conjunct →
  `RegistryForms.Rows()` index, ≈ 23 rows each) ≈ 2.5 k row touches. At ~320 events/month that is ≈ 0.8 M per document, which takes seconds. A non-indexed form
  (22 725 × 46 per row) would hit `#BUDGET`, so a generator test asserts the conjunct order.
- Measure on the large backup (human 10.11 rule). Target < 10 s per monthly document, < 2 min per full-year project recalc.

### ~~5.x rev 1 (superseded)~~ — kept struck for traceability

⛔ **SUPERSEDED — do not implement anything below until §6.** The R3 review found: the library breaks RegistryUse/lock, there is no per-date
lookup, the read set needs a migration, and no "MissingInputs with reason" exists. Kept only so that reviewers can see what changed.

~~### 5.2 Resolution function (one place, library methodology `GASCOMP`, Script)~~

```
case  = GASCASE(streamLegacyId, caseName, onDate)         // INT: event; TOX/CONT: fixed stream + rule
  → REGFIND('STREAM', streamLegacyId, key 'BY_LEGACY_ID')
  → STREAM_CASE entry with STREAM = stream, CASE_NAME = caseName (exact, case-insensitive as registry key), ValidFrom ≤ onDate < ValidTo
GCV(case, 'H2S')  = REGSUM('GAS_COMPOSITION', ROW.CASE = case AND ROW.COMPONENT = 'H2S', ROW.MOL_PCT)   // no row → 0  (= legacy NULL→"0")
GCVT(case)        = REGFIELD(case, 'T_C')                                                               // NULL → 0 (legacy)
```
Bridge: generator (same pattern as `hse400_bridges.py`) emits for each imported formula the argument block
`@GCV.<X> := GCV(case,'<X>')` (INT/CONT) and `@GCV_SOR_<X>`/`@GCV_EOR_<X>` (TOX). Helium, C2H6S: no component in ECR → 0 automatically
(legacy forced 0). Aliases: formulas use only `CO/H2/O2` = ECR folded codes → identical. Registry codes are literals → the publish step
writes `cfg.RegistryUse` for `STREAM`, `STREAM_CASE`, `GAS_COMPOSITION` (today 0) — prerequisite of AC-0.

### 5.3 Event → case

| Consumer | Stream | Case | Date used |
|---|---|---|---|
| INT | event `GasCompositionID` → `STREAM.LEGACY_ID` | event `StreamType` text = `CASE_NAME` | event start (see Q1); legacy rule "whole event inside one version" kept as a check → warning, not silent |
| CONT300 | stream with `NUMBER='416'` AND `IS_ONSHORE=1` → pinned `LEGACY_ID 317` | name contains `SOR` (exactly one, else issue) | day |
| CONT30X | split registry rows (19/21/146 × coef) | name contains season (`Summer` Apr–Sep / `Winter`), English only (legacy L3) | day |
| TOX | gas column → pinned `LEGACY_ID` (14M→572, 412→313, 415→316, 416→317, 417→**575**, 453→361, 481A→574, 481B→573) in a small mapping registry `TOX_GAS_STREAM` (temporal) — removes L2 ambiguity | contains `SOR` / `EOR` | `flare.Date` |

Not found / ambiguous / `NO_COMPOSITION` → `MissingInputs` issue with reason ("composition case not found: stream 26, case …, 2026-03"),
never zero emissions and never `FirstOrDefault` (AN-164 pattern; DESIGN-gas-composition §2 Σ rule).

### 5.4 Configuration that is not composition
`LP_Sour_Analyzers_StreamIDs` moves from AF to an ECR temporal registry `LP_SOUR_SPLIT` (stream, coef; Σ coef = 1 rule):
rows v1 2024 = 3/7, 3/7, 1/7; v2 from 2025 = 0.4/0.4/0.2 (AF xml §2.1).

### 5.5 Versioning
Composition versions are month-granular (human rule 24.11: ValidFrom = 1st of month, DESIGN-gas-composition §2 "version"). Lookup key
= (stream, case, yyyymm of the date in §5.3) → one version per month, so an event inside a month always resolves to one version;
an event crossing a month boundary where a new version starts is the only L4 case (Q1). The calc run records `RegistryAsOfUtc`, re-runs
read as-of (temporal tables).

### 5.6 Recalculation on composition change (AC-0)
- Existing path: a gas-composition operation enqueues one coalesced `IRegistryImpactRecalculationJob` per changed registry × year with
  open/grace dependents found through `cfg.RegistryUse`; approved sheets are skipped and only marked stale via `DataChangedAt`
  (DESIGN-gas-composition §2 "recalculation"; HSE400 3b pattern, human 07.11/16.11).
- Addition (B80-style precision, to avoid recalculating all ~200 documents for one case edit): each run stores its **read set** of
  composition versions (STREAM_CASE entry ids actually resolved). On change of version V, the job filters dependents to documents whose
  last run read V (or whose events reference the same stream+case in the changed months). Fallback when the read set is unknown: all
  dependents of that year.
- Locked months (closed period / approved sheet) are already refused by the gas-composition page (409 `gasCompositionLocked`).

### 5.7 Performance (~200 documents per project)
- Data: 523 + 982 + 22 725 rows — load once per run into an in-memory index `(legacyId, caseName, yyyymm) → componentVector[46] + T`
  (registry snapshot is cached per run, RT-23a). Cost ≈ one indexed read of ~25k values (< 1 MB).
- Volume: 2026 legacy = 2 899 INT events + 1 096 CONT day-rows + 228/5 475 TOX rows in ~9 months ⇒ ≈ 4–8 k calc rows/year; 200 documents
  × few hundred rows each is the same order. Per row: ≤ 47 dictionary hits + ~100–150 formula evaluations (NCalc-equivalent).
- `REGSUM` per component must be served from the snapshot index, not one SQL per call: measure on the large backup (human 10.11 rule)
  — target < 10 s per full-year project recalculation.

---

## 6. Parity plan vs legacy

| Item | Legacy reference (LDB) | Compare | Tolerance |
|---|---|---|---|
| INT (both branches) | `CalcResult_FlaringsIntermittent_SG` 2026-01…2026-09 (2 899 rows), join by `EFID`; inputs `AF_FlaringsIntermittent_SG` | `FCID` = ECR `LEGACY_COMPOSITION_ID` of resolved case; `MolecularWeight, TotalSumComposition, TotalSulphurContent, Density, <X>_mass, FuelLowerHeating, GHGEmissionFactor, EC_*_tonn, EC_*_gsec, GHG_*_tonn` | rel 1e-9 on MW/S/ΣC/mass; rel 1e-6 on tonnes/gsec [A: NCalc double vs ECR decimal]; exact after output rounding |
| CONT | `CalcResult_FlaringsContinuous_SG` 1 096 rows (FCID 1581, 996/997, 1000/1001, 1228/1229) | same columns per day | same |
| TOX SG | `CalcResult_FlaringThermaloxidizer` (monthly 228) **and** `…Daily` (5 475), `IsSour=1`, `Type='SOR'`, `FCID_SG` | gas-column results 14M/412/415/416/417/453/481A/481B; key (date, train, mode, type, column) | same |
| Composition input | FDB vs EDB | already proven: 523/982/982/22 725, Σ and `CHECKSUM_AGG` equal (DESIGN-gas-composition §4.2) | exact |

✎ rev 2 (review R6/E):
- **NumericMode:** all 4 consumer versions are published with `NumericMode = Legacy` (enum `Legacy = 0`, `Enums.cs:591-595`). The tolerances above are measured against
  Legacy mode. Strict mode is out of scope.
- **Keys:** INT per `EFID`; CONT per (day, stream), 1 row onshore + 3 rows offshore per day (1 096 = 274 + 3 × 274); TOX monthly and daily.
- **Known differences (expected, listed in the parity report, not failures):**

| # | Difference | Rows affected in the 2026 copy |
|---|---|---|
| L1 (H3) | TOX EOR uses the EOR composition | 0 (no EOR rows in 2026); unit test only |
| **L6 (H10)** | TOX `Train 1` uses 481A / stream 574 / case 2005 instead of 481B / 2046 | monthly: 10 rows `Train 1 / 481B` (LDB); daily: all Train 1 481 rows |
| X1 | INT event attributed to its **start** period (ECR, D-179) vs `CalculationDate = EndDate` (legacy) | 47 cross-month events (UTC; recount in project TZ +05): per-EFID values equal, monthly totals move |
| L7 | TOX days/months skipped in legacy for a missing HSE400 month are calculated in ECR (SG) | count = TOX days without HSE400 in LDB (measure in lane) |
| L8 | none: open events are excluded on both sides | 0 |
| L4 | event spanning a version change: legacy no result, ECR start-month version | 0 (no version changes exist) |

- **Data gaps (cannot be tested on local data):**
  - no EOR rows;
  - no 2024 data, so split v1 (3/7, 3/7, 1/7) is untested against legacy;
  - **no composition version was ever created** (all `ValidFrom 2022-01-01`, FLERT history empty), so the version-boundary rule is unit-tested only;
  - **`EcrLegacyProd` is absent** (`sys.databases`).
  Request to the human/DBA: a production copy of the legacy `ECR` DB with full 2025 (incl. EOR and By-pass months).
- **TZ test:** an INT event at 19:30Z on the last day of a month (+05 → next month) goes to the next period and reads the next month's version (`registry as of`);
  the season for 31 Mar 19:30Z = April = Summer. The same test runs in a **yearly** document with month columns (live HSE400/HSE401 shape).

Test data: golden fixtures = 3 months (Jan winter, May summer, Sep) × a sample of the 118 used cases. Include stream 100 (Cyrillic case),
26, 19, 107, 317 (416 SOR), 572 (Methanol), 575 (417), 574/573 (481A/B), and one case with CO/H2/NH3/SO2 ≠ 0 (INT300 extras).
Additional cases (review F):
- 481 H2S balance;
- Train 1 → 481A (L6);
- CONT 3-stream split (v1 and v2);
- a cross-month event;
- null `GasCompositionID` / `StreamType`;
- a `NO_COMPOSITION` case;
- an onshore mismatch;
- a missing HSE400 month (L7: SG rows still computed).

Mutations:
- drop one component row → that mass changes;
- change `T_C` → only `EC_Wzv`/`gsec` change;
- delete a case → row **rejected**, not zero;
- remove `reject when` for NO_COMPOSITION → the test goes red (all-zero output);
- edit a version from month M → only months ≥ M change, and each dependent document is recalculated once (AC-0, AC-3 re-scoped);
- an edit inside a closed month → 409.

---

## 7. Questions

✎ rev 2: questions 1–8 of rev 1 were answered by the human on 25.11 (§5.0 H1–H10, incl. L6 = fix). Still open (one word each; default applies until answered):

- ~~Q-A~~ answered **yes** (H12). ~~Q-B~~ answered by H11: per row, composition at the event start, any period → engine change §5.2a. ~~Q-C~~ answered **yes** (H12).
- **Q-E (rev 3).** May the `registry as of` clause also be used by future non-flaring methodologies (it is generic)? Default: **yes**.
- **Q-D (human/DBA, not a quiz):** PI AF data source + read-only account on live, and a production copy of legacy `ECR` with 2025.

~~Rev 1 questions (answered 25.11):~~

1. **Event crossing a composition version change — take the version valid at event start?** Default: **yes** (legacy errored, L4).
2. **TOX EOR — fix the legacy defect (EOR uses EOR composition) instead of copying it?** Default: **fix** (legacy used SOR values, L1).
3. **TOX gas columns pinned to stream Ids (417 → stream 575) instead of lookup by number?** Default: **yes** (number 417 is ambiguous, L2).
4. **Continuous offshore season rule Apr–Sep = Summer, English cases only — keep?** Default: **yes**.
5. **`LP_Sour_Analyzers_StreamIDs` split (19/21/146 = 0.4/0.4/0.2) — keep as an ECR registry edited by admins?** Default: **yes**.
6. **First release scope — Intermittent + Continuous SG + Thermal-oxidizer SG together?** Default: **together** (one lookup, one lane).
7. **Flare events into ECR — from PI AF event frames via SourceEvent sync (not from legacy tables)?** Default: **yes**.
8. **Formulas — import legacy `Flert`/`Thermaloxidizer` texts unchanged with the `GCV` bridge (rewrite to REGSUM later)?** Default: **bridge**.

## ✎ 25.11 ~15:20 — дані 2025 від людини (база паритету)
Людина оновила БД ECR (стара система) даними 2025 і дала довідник потоку: `.sync-local\design\FLARES_TOX_HSE400_DATAFLOW.md` (1151 рядок, з прогоном на UAT).
- SG (Int SG, Cont SG) 2025 — реальні за весь рік (SG від HSE400 не залежить) → паритет по всіх 12 місяцях.
- FG 2025: січень–червень пораховано на складі газу HSE400 **2026** (у PI склад лише за липень–грудень 2025) → паритет FG лише липень–грудень; січень–червень — відома різниця, не дефект.
- TOx 2025 — не пораховано: 0 event frames у PI, CalcResult_FlaringThermaloxidizer/Daily за 2025 порожні → паритету TOx на 2025 немає; TOx перевіряти на 2026.
- Передумова «прод-копія старої БД з 2025» — виконана.

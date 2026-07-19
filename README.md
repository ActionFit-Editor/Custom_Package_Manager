# Custom Package Manager (com.actionfit.custompackagemanager)

Unity에서 ActionFit UPM 패키지 카탈로그를 조회하고 설치하는 도구입니다. 패키지와 모음을 검색하고, 카탈로그 또는 직접 입력한 Git URL에서 설치하며, 선택한 버전을 적용하거나 제거하고 ActionFit Editor 패키지의 수동 publish workflow를 지원합니다.

## 설정 SO와 신규 패키지 생성

catalog 설정 `ActionFitPackageCatalogSettings_SO`는 공통 provider가 `Assets/_Data/_CustomPackageManager/`에서 재사용·생성하고 탐색 결과를 캐시합니다.

`1. Create Package` 화면의 Settings SO Lifecycle은 다음 세 가지입니다.

- `None`: 기존 README-only template을 유지합니다.
- `EditorOnly`: Editor 설정 타입, SO Singleton 의존성/asmdef, `Setting SO` 메뉴, 문서와 EditMode 수명주기 검사를 함께 생성합니다.
- `RuntimeSingleton`: `SO_Singleton<자기타입>` Runtime 타입과 `Assets/_Data/_<Owner>/Resources/SO/<Type>.asset` 계약, Runtime/Editor asmdef, 메뉴·문서·검사를 함께 생성합니다.

패키지 계약 검증기는 명시적으로 등록된 설정 타입에만 dependency, menu, asmdef, source location, runtime base 규칙을 적용합니다.

## 설치

```json
{
  "dependencies": {
    "com.actionfit.custompackagemanager": "https://github.com/ActionFit-Editor/Custom_Package_Manager.git#1.1.110"
  }
}
```

## 저장소 공개 범위 정책

새 패키지와 `Fork as New` 저장소의 기본 공개 범위는 **Public**입니다. 승인된 소유권, 배포 또는 기밀성 제약 때문에 소스 접근을 제한해야 할 때만 명시적 예외로 **Private**을 선택합니다. 기존 PackageInfo 공개 범위는 승인된 마이그레이션으로 변경하기 전까지 보존합니다.

저장소 공개 범위와 관계없이 token, 자격 증명, private key, signing material, vendor 설정과 그 밖의 secret을 패키지 콘텐츠나 메타데이터에 포함하면 안 됩니다. Git에서 제외된 로컬 설정, 환경 변수 또는 승인된 secret store에 보관하세요. Private 저장소도 패키지 secret 보관소가 아닙니다. 공개 배포 권리가 확정되지 않았다면 패키지를 몰래 Private으로 바꾸지 말고 publish를 차단해 검토합니다.

## README 작성 언어 정책

ActionFit 패키지의 `README.md`는 사람이 읽는 사용 문서이므로 한국어를 기본 언어로 작성합니다. 패키지를 publish하기 전에 작성자 또는 검토자는 README의 제목, 설치 방법, 사용법, 설정, 주의사항, 마이그레이션 및 릴리스 관련 설명이 한국어 중심으로 최신 상태인지 확인해야 합니다.

패키지 ID, 타입 및 API 이름, Unity 메뉴 경로, 명령어, 설정 키, 파일 경로, 코드 예제와 외부에서 정의된 제품명·고유명사는 정확성을 위해 원문 표기를 유지합니다. 이 규칙은 기존 README를 자동 번역하지 않으며, 공유 AI 문서인 `AI_GUIDE.md`는 프로젝트 간 재사용을 위해 영어로 작성하는 예외를 유지합니다.

## 메뉴

- `Tools > Package > Custom Package Manager > Package Manager`: 패키지 설치, 버전 적용, 제거, 상세 정보 확인과 업데이트 검사를 수행합니다.
- `Tools > Package > Custom Package Manager > SDK Profiles`: bridge 패키지의 versioned SDK profile을 불러오고 현재 상태와 정확한 plan을 검토한 뒤 명시적으로 적용, 복구, 업데이트, 제거 또는 recovery합니다.
- `Tools > Package > Custom Package Manager > Manager Console`: 패키지 생성, 변경되거나 선택한 패키지 버전 publish, schema v2 Agent Skill 추가, catalog/manifest 열기와 AI guide router refresh를 수행합니다.
- `Tools > Package > Custom Package Manager > Install or Refresh Agent Skills`: 설치된 ActionFit 패키지의 등록 skill을 검색하고 관리되는 프로젝트 로컬 복사본을 안전하게 동기화합니다.
- `Tools > Package > Custom Package Manager > Remove Managed Agent Skills`: 변경되지 않은 관리 복사본만 제거하고 명시적 refresh 전까지 자동 재생성을 비활성화합니다.
- `Tools > Package > Custom Package Manager > Add Agent Skill`: 편집 가능한 embedded 패키지에 schema v2 skill을 추가하고 최초 실행 시 필수 package help skill을 생성합니다.
- `Tools > Package > <Package Name> > README`: 해당 패키지 README를 Editor 창에서 엽니다.
- `Tools > Package > <Package Name> > Setting SO`: 패키지가 설정 ScriptableObject를 보유한 경우 해당 에셋을 선택합니다.

## 패키지 Agent Skill

Custom Package Manager의 `Install or Refresh Agent Skills`는 이 패키지 자체에서도 Codex와 Claude에 다음 skill을 설치합니다.

- `package-manager-help`: 패키지 관리 기능, 설치된 스킬, 릴리스 준비와 안전 경계를 설명합니다.
- `package-manager-audit`: 로컬 manifest/lock/package metadata와 agent-skill 등록 상태를 변경 없이 점검합니다.
- `package-manager-validate`: 기존 `Tools~/package_contract_validator.py`를 한 패키지, 변경 패키지 또는 전체 embedded 패키지 범위로 실행합니다.
- `package-manager-update-dependencies`: physical embedded ActionFit 패키지의 dependency 최신화 범위를 plan하고, 정확한 별도 승인 뒤 release metadata까지 원자적으로 apply합니다.

help/audit/validate는 read-only입니다. dependency updater는 수동 호출 전용 write-capable skill이며 plan은 항상 read-only입니다. apply에는 성공한 Catalog refresh 확인, 현재 `planId`와 정확한 승인 문자열이 모두 필요하고 contract validation 실패 시 전체 파일을 복구합니다. 실제 publish는 apply와 분리되며 사용자가 다시 명시적으로 요청한 경우에만 기존 `ActionFitPackageBulkPublishApi`의 preflight와 승인을 dependency-safe layer 순서로 사용합니다.

### Embedded 의존성 자동화

Unity의 `ActionFitPackageWorkflowApi.Inspect`가 `RefreshCatalog = true`로 성공한 뒤 다음 명령으로 변경 계획을 확인합니다.

```bash
python3 Packages/com.actionfit.custompackagemanager/Tools~/package_dependency_updater.py plan \
  --repo-root . \
  --catalog-refreshed
```

이 도구는 top-level physical `Packages/com.actionfit.*`만 읽고 dependency graph를 fixed point로 계산합니다. catalog/local embedded 중 더 최신인 dependency를 선택하며 downgrade하지 않고, local-ahead package는 publish prerequisite로 표시합니다. major update, cycle, malformed SemVer, project override, missing physical dependency는 안전하게 차단합니다.

Plan 결과의 `requiredApprovalText`를 그대로 승인한 경우에만 동일한 `planId`로 apply할 수 있습니다. Apply는 affected package의 `package.json`, README 설치 tag, AI guide version, PackageInfo release note만 원자적으로 갱신하고 package contract validation 실패 시 모두 rollback합니다. Python 도구는 GitHub push, tag 생성, Catalog append 또는 credential 접근을 수행하지 않습니다.

ActionFit 패키지는 `Skills~/manifest.json`으로 Codex 및 Claude skill을 등록할 수 있습니다.

```json
{
  "schemaVersion": 2,
  "skillPrefix": "sample",
  "helpSkill": "sample-help",
  "skills": [
    {
      "name": "sample-help",
      "agents": ["codex", "claude"],
      "includeShared": false,
      "access": "read-only"
    },
    {
      "name": "sample-run",
      "agents": ["codex", "claude"],
      "includeShared": true,
      "access": "write-capable"
    }
  ]
}
```

Schema v2에는 명시적인 소문자 `skillPrefix`, `<skillPrefix>-help`와 같은 `helpSkill`, 패키지가 사용하는 모든 agent를 포함하는 등록 help skill이 필요합니다. 모든 skill 이름은 `<skillPrefix>-`로 시작하고 `access`는 `read-only` 또는 `write-capable`로 명시합니다. Runtime installer는 호환성을 위해 schema v1 패키지를 임시로 읽지만 패키지 계약 검증기는 변경되거나 새로 publish하는 패키지의 v1을 거부합니다.

소스는 `Skills~/Codex/<skill-name>`과 `Skills~/Claude/<skill-name>`에 둡니다. 각 소스에는 등록과 동일한 frontmatter `name`, skill의 기능과 사용 시점을 설명하는 `description`을 가진 `SKILL.md`가 있어야 합니다. `Skills~/Shared` 아래의 선택형 패키지 공통 파일은 `includeShared`가 true일 때만 overlay하며 shared와 agent 소스가 같은 상대 경로 파일을 포함하면 안 됩니다.

설치할 때 manager는 `package.json`, schema v2 manifest와 대상 agent의 frontmatter description으로 각 설치 help skill 안에 `PACKAGE_SKILLS.md`를 생성합니다. 생성 파일은 관리 hash에 포함되며 package identity, 관련 skill, `$name` 호출과 access의 authoritative inventory입니다. 패키지 소스에 `PACKAGE_SKILLS.md`를 직접 작성하지 않습니다.

Manager는 등록 소스를 `.agents/skills/<skill-name>`과 `.claude/skills/<skill-name>`에 설치합니다. 설치는 Editor load와 패키지 등록 후 실행되며 batch mode에서는 건너뜁니다. 관리 소유권과 hash는 Git에서 제외된 `UserSettings/ActionFitPackageManager/skill-install-state.json`에 저장합니다. 누락된 target은 설치하고 변경되지 않은 관리 target은 업데이트하며, 관리되지 않거나 수정되거나 link이거나 충돌하는 target은 경고와 함께 보존합니다. 패키지가 사라져도 자동 동기화가 target을 삭제하지 않습니다. 삭제는 명시적 제거 메뉴에서 변경되지 않은 관리 복사본에만 가능합니다.

Package Manager 상세 화면은 패키지별 집계 `Agent Skills` 상태인 registered, current, update available, missing, preserved 및 conflict 수를 표시합니다. Embedded 패키지 행에는 `Add Agent Skill`도 표시되며 downloaded 패키지는 소스를 편집하기 전에 embed해야 합니다. 같은 scaffolding 창을 Manager Console에서도 열 수 있습니다.

Skill 이름은 소문자, 숫자와 hyphen만 허용합니다. Manifest에서 source 또는 target 경로를 지정할 수 없으므로 고정 패키지 및 프로젝트 로컬 skill root 밖으로 설치를 redirect할 수 없습니다. Symbolic link와 reparse point를 거부하며 설치 중 복사한 script를 실행하지 않습니다.

이전에 AI Jira가 관리하던 프로젝트는 기존 `UserSettings/AIJira/skill-install-state.json`을 유지합니다. Custom Package Manager는 이 파일을 마이그레이션 입력으로 읽고 target이 기록된 hash와 여전히 일치할 때만 소유권을 인수하며 legacy 파일과 이전에 비활성화한 자동 설치 설정을 보존합니다.

## 외부 SDK 설치 Profile

`SDKInstallProfile.json`은 공식 출처에서 외부 SDK 하나를 설치하는 versioned·vendor-neutral 선언입니다. Schema v1은 기존의 정확한 불변 source 계약과 동기 `Plan` API를 유지합니다. Schema v2는 각 source에서 `ResolutionPolicy: anyInstalledElseLatestStable`을 선택할 수 있습니다. 비동기 resolver는 이미 해석된 필수 패키지 버전을 보존하고 해당 패키지가 없을 때만 선언된 공식 metadata를 조회합니다. Profile은 vendor 및 license metadata, Unity/platform 호환성, 허용 HTTPS domain, artifact/Git/registry source, 선택 module과 dependency closure, scoped registry 및 기존 설치 감지 규칙을 선언합니다. Git source는 안전한 상대 `GitSubpath`를 선언할 수 있고 planner는 이를 UPM `?path=<subpath>#<immutable-revision>` 의존성 값으로 조합합니다. Profile에는 자격 증명이나 vendor 설정 값을 포함하지 않습니다.

Schema v2 latest source는 일치하는 `LatestResolver`(`registryMetadata`, `gitRelease`, `artifactMetadata`)와 `AllowedDomains`의 query 없는 `MetadataUrl`을 선언합니다. Resolver는 canonical `PackageId` 및 `Releases` 문서, registry의 native npm/UPM `versions` metadata와 Git source의 GitHub 형식 release 배열을 받습니다. 모든 canonical release는 안정적인 정확한 `Version`과 선택형 `MinimumUnityVersion`/`MaximumUnityVersion`을 선언합니다. Git release는 `ImmutableRevision`, artifact release는 자격 증명 없는 `Url`, 정확한 `PackageVersion`, `Sha256`도 선언합니다. 명시적 SHA-256이 없는 artifact metadata는 거부합니다. Draft와 prerelease는 제외합니다. 비어 있지 않은 `VersionFamily`를 공유하는 source가 모두 누락된 경우 호환되는 가장 최신 공통 버전을 해석합니다. 일부만 설치된 family는 해당 버전이 공식적으로 제공되고 호환될 때만 누락된 선택 패키지를 설치된 버전에 고정하며 충돌하거나 사용할 수 없는 family 버전은 차단합니다.

`ResolveAsync`와 `PreparePlanAsync`는 상태를 변경하지 않습니다. 설치 상태 감지는 `Packages/manifest.json`, `Packages/packages-lock.json`, 열린 프로젝트의 Unity 등록 패키지를 비교합니다. Manifest에만 있는 선언, 중복, 잘못된 source, 누락된 등록, 일관되지 않은 해석, legacy 충돌 또는 모호한 부분 상태는 plan을 차단합니다. 선택한 latest-policy 의존성이 모두 이미 해석되어 있으면 `NO_CHANGES`를 반환하고 manifest나 ownership 상태를 쓰지 않습니다. 누락된 패키지는 불변 version/commit/checksum snapshot으로 해석하며 정확한 metadata content hash와 프로젝트 package-state hash를 plan ID에 포함하고 실행 전에 다시 검증합니다.

`Tools > Package > Custom Package Manager > SDK Profiles` 또는 `SDK Profiles` toolbar button을 사용합니다.

1. 설치된 bridge 패키지의 `Editor/SDKInstallProfile.json`을 불러옵니다.
2. 선택 module을 지정하고 `Inspect (read-only)`를 실행합니다.
3. `Apply`, `Repair`, `Update`, `Remove` 중 하나를 선택한 뒤 `Resolve + Prepare Plan (read-only)`을 실행합니다.
4. 모든 의존성, scoped registry, artifact cache, ownership 변경과 content-bound plan ID를 검토합니다.
5. 별도 확인 dialog에서 정확히 해당 plan을 실행합니다.

실행은 변경 전에 profile, plan ID, manifest, packages lock, Unity 등록 패키지, ownership과 공식 metadata snapshot을 다시 검증합니다. Artifact download에는 선언 domain의 자격 증명 없는 HTTPS, 검증된 SHA-256과 일치하는 `package/package.json` identity가 필요합니다. Manifest 및 ownership은 원자적으로 쓰고 transaction journal은 `UserSettings/ActionFitPackageManager/SdkTransactions`에 유지합니다. Domain reload는 대기 journal만 보고하며 recovery는 명시적 복구 동작에서만 프로젝트 상태를 변경합니다.

Ownership schema version 1은 각 profile이 생성하거나 의도적으로 인수한 항목만 기록합니다. 제거는 해당 profile이 만들지 않은 호환 의존성, registry scope, cache artifact 또는 공유 항목을 보존하며 충돌 시 사용자 관리 상태를 덮어쓰지 않고 실행을 차단합니다.

Bridge 패키지 작성자는 `ActionFitSdkBridgePackageTemplate.Create`를 호출할 수 있습니다. Template에는 `Public` 저장소 공개 범위와 패키지 ID에 `BridgePackageId`가 일치하는 유효 profile이 필요합니다. 설치된 `com.actionfit.custompackagemanager` 버전을 명시적 패키지 의존성으로 추가하고 `Editor/SDKInstallProfile.json`, `THIRD_PARTY_NOTICES.md`와 Editor 계약 테스트 어셈블리를 생성합니다. Bridge 패키지는 source-only이며 SDK 바이너리, archive, 자격 증명, `google-services.json` 또는 `GoogleService-Info.plist`를 재배포하면 안 됩니다.

## 패키지 계약 검증기

`Tools~/package_contract_validator.py`는 Unity를 시작하거나 외부 서비스에 연결하지 않고 embedded `Packages/com.actionfit.*` 패키지를 검증합니다. 사용하는 Unity 프로젝트 root에서 Python 3로 실행합니다.

```bash
# One package. Add --base-ref to enforce a version bump for changed files.
python Packages/com.actionfit.custompackagemanager/Tools~/package_contract_validator.py \
  --package com.actionfit.custompackagemanager \
  --base-ref origin/dev_jewoo

# Every package changed from the merge base of the supplied Git ref.
python Packages/com.actionfit.custompackagemanager/Tools~/package_contract_validator.py \
  --changed \
  --base-ref origin/dev_jewoo \
  --output Temp/actionfit-package-contract.json

# The current contract state of every embedded ActionFit package.
python Packages/com.actionfit.custompackagemanager/Tools~/package_contract_validator.py --all
```

검증기는 `package.json`, SemVer와 변경 패키지 버전 상승, README 설치 tag, `AI_GUIDE.md` identity/version/router 항목, schema v2 prefix/help/access 규칙, 등록 skill source와 `SKILL.md` frontmatter, `ActionFitPackageInfo_SO` 및 패키지 asmdef를 확인합니다. 패키지에 `Editor/SDKInstallProfile.json`이 있으면 profile schema/source 불변성, public bridge 공개 범위, 서드파티 고지, source-only 크기 경계와 금지된 vendor 파일 또는 자격 증명도 검사합니다. 동일한 JSON schema를 stdout과 선택형 `--output`에 쓰며 모든 진단에는 `code`, `severity`, `path`, `line`, `message`, `suggestedFix`가 포함됩니다.

`--changed`는 base commit에 존재하던 embedded 패키지 폴더가 삭제된 경우 현재 `Packages/manifest.json`의 top-level dependency가 credential-free HTTPS Git URL과 full commit 또는 exact SemVer tag를 사용하고, `packages-lock.json`의 depth-0 Git entry가 동일 version과 40자리 commit hash를 가질 때만 downloaded 전환으로 인정합니다. 이 조건이 하나라도 다르면 삭제를 계약 오류로 보고합니다. 자동 생성된 `PACKAGE_AI_GUIDE_ROUTER.md`만 바뀐 경우에는 Custom Package Manager 릴리스 변경으로 선택하지 않지만, 다른 패키지 소스가 함께 바뀌면 기존 버전 상승 검사를 그대로 적용합니다.

Exit code는 로컬 자동화 및 CI에서 안정적으로 유지됩니다.

- `0`: 선택한 모든 패키지가 통과했습니다.
- `1`: 하나 이상의 패키지 계약 진단에 `severity: error`가 있습니다.
- `2`: argument, Git, 저장소 또는 결과 출력 infrastructure 문제로 검증기를 안정적으로 실행하지 못했습니다.

`--changed`에는 `--base-ref`가 필요합니다. `--package`와 `--all`은 Git 비교 없이 실행할 수 있지만 버전 상승 강제 검사는 `--base-ref`를 제공할 때만 활성화됩니다. 검증기는 catalog, GitHub remote, 자격 증명, Unity compile 또는 패키지 테스트를 검사하지 않습니다.

Unity가 격리된 `file:` 의존성에서 패키지 소유 검증기를 호출하면 Editor adapter는 일회용 프로젝트의 가상 `Packages` 경로가 physical이라고 가정하지 않고 설치된 패키지 경로에서 실제 패키지 저장소 root를 해석합니다. Publish 준비도 physical embedded 폴더를 요구하거나 catalog, 자격 증명 및 remote 상태를 검사하기 전에 계약 진단을 반환합니다.

## 패키지 관리자

`Search`는 package ID, 표시 이름, owner뿐 아니라 package type, 저장소, 버전, 상태, Unity 최소 버전, 설명, 변경 내역과 의존성을 함께 검색합니다. 활성 Content Bundle도 bundle ID, 이름, 상태, 필수 패키지, module ID·이름·패키지로 같은 검색어에 필터링됩니다.

`Install from Git URL`은 카탈로그에 없는 Unity 패키지를 Unity Package Manager의 `Client.Add`로 설치합니다. 자격증명이 URL에 포함되지 않은 HTTPS, `ssh://`, SCP 형식 SSH를 지원하며 UPM의 `?path=<subpath>`와 `#<revision>` 문법을 함께 사용할 수 있습니다. HTTP, embedded HTTPS 자격증명, SSH password, 다른 query와 비어 있는 revision은 설치 요청 전에 거부하고 입력 URL은 로그에 기록하지 않습니다.

`Package Collections`는 여러 패키지를 설치하는 bootstrap/installer 패키지를 일반 package section과 분리합니다. Catalog가 선택형 `package_type` column에서 `collection`을 제공하면 이를 우선 사용하고, 기존 catalog와의 호환을 위해 package ID가 `.installer`로 끝나는 항목도 collection으로 분류합니다. 설치 버튼과 버전 선택은 일반 카탈로그 패키지 흐름을 그대로 사용하며 Lava Rush Installer 같은 self-removing bootstrap은 설치가 완료된 뒤 기존 Content Bundle 상태로 이어집니다.

### Content Bundle

Bootstrap 패키지는 versioned JSON profile을 `ActionFitContentBundleApi`에 전달해 하나의 Git URL로 응집력 있는 기본 패키지 묶음을 설치할 수 있습니다. Legacy schema version 1은 전체 패키지 목록을 설치합니다. Schema version 2는 패키지를 필수 및 선택 module로 그룹화합니다. 필수 module은 선택 해제할 수 없고 기본 선택 module은 한 번의 설치 흐름을 유지하며 직접 사용자는 `PlanSelectedModulesJson` / `InstallSelectedModulesJson`으로 더 작은 closure를 선택할 수 있습니다. 공유 패키지는 한 번만 설치합니다. Manager는 모든 의존성 변경을 먼저 plan하고 호환되는 embedded 패키지와 같거나 최신인 canonical tag를 보존하며, 같은 canonical 저장소의 오래된 tag만 업그레이드합니다. Local, fork, branch 기반, 해석 불가 또는 사용자가 수정한 값은 덮어쓰지 않고 차단합니다.

성공한 설치는 journal에 기록하고 `Packages/manifest.json`을 원자적으로 쓴 뒤 모든 필수 패키지가 등록될 때까지 기다립니다. 이후 `ProjectSettings/ActionFitContentBundles.json`에 ownership을 영속화하고 bootstrap 의존성을 제거합니다. 중단된 install/release transaction은 Editor load 복구를 위해 `UserSettings/ActionFitPackageManager/ContentBundleTransactions`에 남깁니다.

활성 bundle은 일반 패키지 행 위에 표시됩니다. 선택 module은 `PlanModifyModules` / `ModifyModules`에서만 변경할 수 있고 UI가 적용 전 정확한 패키지 diff를 보여줍니다. 선택 패키지는 직접 제거할 수 없고 필수 module은 선택 상태를 유지하며 패키지 등록 event가 누락된 필수 manifest 항목을 복구합니다. Release에는 profile의 정확한 GitHub login allowlist가 필요하고 shared, embedded, 비소유 및 사용자 수정 의존성을 보존합니다. Batchmode 변경은 비활성화되며 조사 및 plan API는 읽기 전용입니다.

공개 Editor API:

- `ActionFitContentBundleApi.InspectJson` / `PlanJson`
- `ActionFitContentBundleApi.PlanSelectedModulesJson` / `InstallSelectedModulesJson` / `RepairSelectedModulesJson`
- 기본 module 선택용 `ActionFitContentBundleApi.InstallJson` / `RepairJson`
- `ActionFitContentBundleApi.PlanModifyModules` / `ModifyModules`
- `ActionFitContentBundleApi.PlanRelease` / `Release` / `Remove`
- `ActionFitContentBundleApi.Recover`, `GetStatuses`, `IsRequiredPackage`, `IsManagedPackage`

새 installer 패키지는 `Editor/Templates~/ContentBundleInstaller/`에서 시작하고 `Editor/Documentation/ContentBundleInstallerContract.md`를 따릅니다. Editor 전용 bootstrap은 reflection으로 Custom Package Manager를 찾고 누락되었거나 오래된 canonical manager tag만 설치합니다. Embedded/local/fork/newer manager source를 보존하고 기본 module 설치를 호출한 뒤 manager가 검증된 bootstrap 의존성을 제거하게 합니다.

- `Reload`: 활성 catalog와 현재 패키지 설치 상태를 다시 불러옵니다.
- `Update Catalog`: 설정된 spreadsheet/web app에서 로컬 catalog CSV를 내려받습니다.
- `Force Update`: `Update Catalog`를 실행하고 downloaded 패키지를 나열한 뒤 확인을 거쳐 catalog 최신 Git UPM URL을 `Packages/manifest.json`에 다시 적용합니다. Embedded 패키지는 건너뜁니다.
- `Check Update`: catalog 최신 버전이 현재 버전보다 높은 설치 패키지를 표시합니다.
- `Console`: Manager Console을 엽니다.
- `SDK Profiles`: 외부 SDK 조사, plan, 실행 및 명시적 recovery 창을 엽니다.

패키지 section은 Package Manager, Package Collections, Embedded Packages, Downloaded Packages, Available Packages로 나뉩니다. Collection은 일반 package section에서 제외됩니다. `Packages/manifest.json`의 Git/registry 의존성은 Downloaded Packages로 표시합니다. Local `file:` 의존성 또는 manifest 의존성 없이 `Packages/` 아래에 있는 패키지 폴더는 Embedded Packages로 표시합니다.

패키지 section은 `likes - dislikes`로 계산한 community score가 높은 순서로 정렬합니다. Catalog spreadsheet Web App이 `package_vote_summary`를 제공하면 `Update Catalog`가 `likes`, `dislikes`, `vote_score`, `comment_count`를 로컬 catalog CSV로 가져옵니다.

패키지 README와 설정 접근 메뉴는 Package Manager 행이 아니라 Unity 상단 메뉴에 있습니다. 각 패키지는 `Tools > Package > <Package Name> > README`를 가지며 공유 설정 ScriptableObject를 소유하거나 bootstrap하는 패키지는 같은 분리된 하단 메뉴 그룹에 `Setting SO`도 가집니다. 각 패키지는 자체 메뉴 파일을 소유하고 `Setting SO`에 패키지별 에셋 경로나 안전한 factory method를 사용합니다.

`Tools > Package`는 메뉴 우선순위에 따라 그룹을 유지해야 합니다. 패키지 전체 Custom Package Manager 항목이 먼저, 실행 가능한 도구 명령이 있는 패키지가 다음, `Setting SO` + `README`만 있는 패키지가 그다음, README 전용 패키지가 마지막입니다. 그룹 사이에는 separator 간격을 유지합니다. 패키지 root 안에서 실제 도구 명령은 분리된 하단 `Setting SO`와 `README`보다 위에 둡니다.

패키지에 문서화된 예외 사유가 없다면 정해진 priority band를 사용합니다. Package Manager는 `0-9`, 실행 도구는 `20-99`, `Setting SO` + `README` 전용 패키지는 `600-699`, README 전용 패키지는 `900-999`입니다. `1. Create Package`로 만든 새 패키지는 기본적으로 README 전용 패키지 메뉴 파일을 포함합니다.

Downloaded 패키지는 `Embed for Edit`, `Project Override`, `Fork as New`를 제공합니다. `Embed for Edit`는 Unity Package Manager의 공식 `Client.Embed` 작업으로 downloaded 패키지를 `Packages/<packageId>/` 아래에 materialize합니다. Unity가 성공을 보고하면 manager가 `package.json`을 검증하고 cache 전용 `_fingerprint` metadata를 제거하며 `Packages/manifest.json`을 `file:<packageId>`로 정규화합니다. 기존 패키지 저장소로 변경을 publish할 수 있도록 catalog 저장소 metadata도 보존합니다. Physical local 패키지 폴더가 이미 있고 `package.json` name이 일치하면 덮어쓰지 않고 기존 폴더를 사용할 수 있습니다. Manager는 `UserSettings/ActionFitPackageManager/EmbeddedBaselines`에 패키지 파일 baseline을 기록해 이후 변환 경고에서 embed 후 파일 변경 여부를 보고합니다. Downloaded 패키지를 수정하려면 이 Embed for Edit 흐름을 사용해야 합니다. Catalog가 새 버전을 기록하지 못하므로 upstream 저장소를 직접 수정하고 version tag를 수동 push하는 방식은 승인된 수정 경로가 아닙니다.

`Project Override`는 같은 보호 embed transaction을 사용하지만 PackageInfo에서 Public으로 선언된 패키지만 허용합니다. Public base 저장소 URL, version, revision 및 content hash를 `ProjectSettings/ActionFitPackageOverrides.json`에 기록합니다. 프로젝트 상대 embedded 경로만 저장하며 절대 경로나 자격 증명을 포함한/private remote는 저장하지 않습니다. Override 상태는 현재 content hash와 catalog 최신 버전을 기록된 base와 비교하고 remote URL을 노출하지 않은 채 생성 AI package state에 포함됩니다. 단일 패키지 및 자동 bulk upstream publish에서는 제외됩니다. Catalog base로 복원하고 기록을 지우려면 `Use Downloaded`, 새 패키지 ID/저장소로 독립 publish하려면 `Fork as New`를 사용합니다.

Downloaded embedding은 Unity Package Manager에 위임하므로 Unity의 가상 `Packages/<packageId>` mapping이 custom 폴더 이동을 `Library/PackageCache` 안으로 되돌릴 수 없습니다. 기존 폴더 변환과 `Fork as New`는 UI와 public AI API가 공유하는 원자적 journal transaction을 계속 사용합니다. Manifest 쓰기는 검증된 임시 파일과 원자적 교체를 사용하고 rollback은 transaction이 생성한 패키지 폴더를 삭제하기 전에 영향받은 의존성 값을 복원합니다. 대기 중인 custom transaction은 `UserSettings/ActionFitPackageManager/Transactions`에 기록하고 Editor restart 또는 domain reload 후 복구합니다. 의존성 복구를 검증할 수 없으면 local 패키지 폴더를 보존하고 API가 journal 경로와 함께 `RECOVERY_REQUIRED`를 반환합니다.

Physical embedded 폴더가 없어도 Unity가 downloaded 패키지 cache 콘텐츠를 논리적 `Packages/<packageId>` 경로로 노출할 수 있습니다. 따라서 변환은 재사용 가능한 local 폴더 존재 여부를 판단할 때 `Directory.Exists`만 사용하지 않고 보호된 physical 디렉터리 열거를 사용합니다. Downloaded cache projection을 embedded 패키지로 오인하거나 패키지를 복사하지 않은 채 `file:` 의존성을 쓰는 문제를 방지합니다.

`Fork as New`는 downloaded 패키지를 새 `Packages/<newPackageId>/` 폴더로 복사하고 새 `com.actionfit.*` 패키지 ID에 맞게 metadata를 다시 씁니다. 새 저장소용 PackageInfo metadata를 만들고 원래 manifest 의존성을 제거한 뒤 새 local `file:` 의존성을 씁니다. 저장소 공개 범위 기본값은 `Public`이며 `Private`은 승인된 예외로 명시적으로 선택해야 합니다. Visibility가 없는 API 요청은 `Public`으로 정규화하고 지정되지 않은 `Private` 요청은 거부합니다. Source 패키지와 fork가 중복 어셈블리를 동시에 compile하지 않게 합니다. 복사 또는 검증이 실패하면 manifest를 그대로 유지해 Unity가 깨진 `file:` 의존성을 해석하지 않게 합니다. Embedded 패키지의 `Use Downloaded`는 embedded 업데이트와 같은 보호 교체 절차로 패키지를 downloaded 흐름으로 되돌립니다.

Embedded 패키지를 교체하기 전에 manager는 `Embed for Edit` 이후 변경 여부와 local 수정이 merge되지 않는다는 경고를 표시하고 `UserSettings/ActionFitPackageManager/EmbeddedBackups/<packageId>/` 아래에 timestamp가 있는 안전 복사본을 만듭니다. 필요한 모든 backup이 성공한 뒤에만 manifest를 변경합니다. Embedded 폴더를 운영체제 휴지통으로 옮길 수 없으면 원래 manifest와 해당 작업 중 이미 옮긴 패키지 폴더를 복원합니다. Backup은 수동으로 제거할 때까지 유지합니다.

Embedded 패키지를 수정한 뒤 `Publish Changed`를 사용하기 전에 `package.json` 버전을 catalog 최신 버전보다 높게 올립니다.

## 커뮤니티 피드백

각 패키지 상세 화면에는 `Community` foldout이 있습니다.

- `Like`와 `Dislike`는 익명 project ID 및 패키지당 하나의 vote를 공유합니다.
- 익명 project ID는 `UserSettings/ActionFitPackageManager/community_id.txt`에 저장하며 사용자 계정이나 기기 identity가 아닙니다.
- 프로젝트가 `Like` 또는 `Dislike` 중 하나를 제출하면 해당 패키지의 두 vote button을 모두 비활성화합니다. 첫 vote가 최종입니다.
- Comment는 `Title`과 `Description`을 사용합니다. 사용자가 제목을 먼저 훑고 원하는 설명만 열 수 있도록 comment title을 foldout으로 표시합니다.
- 각 프로젝트는 패키지당 편집 가능한 comment 하나를 유지할 수 있습니다.

설정된 catalog Web App은 `votePackage`, `upsertPackageComment`와 `Update Catalog` 중 `package_comments` sheet 반환을 지원해야 합니다. 필요한 sheet 및 response 계약은 `Editor/Documentation/PackageCommunityWebAppContract.md`를 확인하세요.

## 업데이트 확인

`Check Update` panel은 catalog 최신 버전이 현재 설치 버전보다 높을 때만 설치 패키지를 표시합니다.

- Downloaded 패키지는 개별, 선택 또는 전체 업데이트할 수 있습니다. `Select Downloaded`와 `Update Downloaded`는 embedded 패키지를 자동 선택하지 않습니다.
- Embedded 패키지도 표시하지만 명시적으로 선택하거나 `Convert & Update`로 업데이트해야 합니다. 다른 버전을 선택하면 안전 backup을 만들고 패키지를 Git UPM 의존성으로 변환하며 local 수정은 merge하지 않습니다.
- `Changes`는 설치 버전과 선택 target 버전 사이의 changelog 행을 표시합니다.
- `History`는 해당 패키지의 모든 catalog changelog 행을 표시합니다.
- 패키지 상세 행도 펼친 패키지 안에서 같은 `Changes`와 `History` 동작을 제공합니다.
- `Latest Git`은 기본 browser에서 패키지의 catalog 최신 GitHub 저장소 tag를 엽니다.
- 설치 패키지가 catalog 최신 버전보다 높으면 우발적 downgrade를 막기 위해 `Check Update` panel에 표시하지 않습니다.

`Force Update`는 `Check Update`와 별개입니다. 먼저 catalog를 refresh한 뒤 downloaded 패키지 확인 목록을 표시하고 나열된 각 패키지에 catalog 최신 URL을 씁니다. Embedded 패키지를 건드리지 않고 같은 버전의 manifest 항목과 의존성 URL을 refresh할 수 있습니다.

예를 들어 `1.0.1`에서 `1.0.4`로 업데이트하면 화면 표시 시점에 `1.0.2`, `1.0.3`, `1.0.4` changelog 행을 보여줍니다. 해당 행을 최신 release note 안에 누적 저장하지 않습니다.

## 변경 이력 규칙

각 패키지의 `ActionFitPackageInfo_SO.ReleaseNote`에는 준비 중인 단일 버전 내용만 포함해야 합니다. 최신 release note에 이전 changelog를 누적하지 않습니다.

Package Manager는 별도 catalog version 행으로 `History`와 `Changes`를 구성합니다. UI가 이미 버전 label을 표시하므로 release note에 `## 1.1.28` 같은 제목을 넣을 필요가 없습니다.

## Unity 메뉴

- 패키지 root: `Tools > Package > Custom Package Manager`
- Agent skill 설치 또는 refresh: `Tools > Package > Custom Package Manager > Install or Refresh Agent Skills`
- 관리 Agent Skill 제거: `Tools > Package > Custom Package Manager > Remove Managed Agent Skills`
- Schema v2 Agent Skill 추가: `Tools > Package > Custom Package Manager > Add Agent Skill`
- README: `Tools > Package > Custom Package Manager > README`.
- Setting SO: `Tools > Package > Custom Package Manager > Setting SO`.
- 패키지 명령은 같은 패키지 root 아래에 유지하며 README/Setting SO 항목이 있으면 분리된 해당 항목보다 위에 표시합니다.
- `Tools > Package` 그룹 순서는 패키지 전체 manager, 실행 도구 패키지, Setting SO + README 전용 패키지, README 전용 패키지를 유지합니다.
- Priority band: Package Manager `0-9`, 실행 도구 `20-99`, Setting SO + README 전용 `600-699`, README 전용 `900-999`

## AI 가이드

모든 ActionFit 패키지는 패키지 root에 `AI_GUIDE.md`를 포함해야 합니다. 사용하는 프로젝트의 AI assistant가 원본 프로젝트 `Docs/AI` 폴더에 접근하지 않아도 패키지별 규칙을 이해할 수 있게 합니다.

- `README.md`: 사람을 위한 설정과 사용법입니다.
- `AI_GUIDE.md`: AI를 위한 package identity, 편집 규칙, release note 규칙, migration 참고 사항과 패키지가 요청한 router 항목입니다.
- `PACKAGE_AI_GUIDE_ROUTER.md`: 작업별로 어떤 설치 패키지 `AI_GUIDE.md`를 읽을지 선택하고 이 router를 프로젝트 기본 AI 읽기 순서에 연결하도록 요청하는 패키지 포함 AI router입니다.
- `package.json`: 패키지 ID, 버전, Unity 버전과 의존성입니다.
- `Editor/PackageInfo/ActionFitPackageInfo_SO.asset`: catalog metadata와 release note 원본입니다.

패키지 동작이 변경되면 publish 전에 해당 패키지의 `AI_GUIDE.md`를 업데이트합니다. Custom Package Manager는 각 패키지의 `Requested router entry`를 읽고 `PACKAGE_AI_GUIDE_ROUTER.md`를 자동 refresh합니다.

### 공개 AI API

아래 API는 Editor 전용 public static API이며 dialog 없이 실행되고 직렬화 가능한 result object를 반환합니다.

```csharp
var inspection = ActionFitPackageWorkflowApi.Inspect(new ActionFitPackageInspectionRequest
{
    PackageId = "com.actionfit.example",
    RefreshCatalog = true,
});

var validation = ActionFitPackageEmbedApi.Validate(new ActionFitPackageEmbedRequest
{
    PackageId = "com.actionfit.example",
    DryRun = true,
});

ActionFitPackageEmbedApi.EmbedForEditAsync(new ActionFitPackageEmbedRequest
{
    PackageId = "com.actionfit.example",
    Resolve = true,
}, embed => Debug.Log($"{embed.Code}: {embed.Message}"));

var skill = ActionFitPackageSkillScaffoldApi.Add(new ActionFitPackageSkillScaffoldRequest
{
    PackageId = "com.actionfit.example",
    SkillPrefix = "example",
    SkillName = "example-run",
    Description = "Run the example package workflow when explicitly requested.",
    Agents = new[] { "codex", "claude" },
    Access = "write-capable",
});

var sdkProfile = ActionFitSdkInstallApi.ReadProfile(
    "Packages/com.actionfit.vendor.sdk/Editor/SDKInstallProfile.json");
var sdkPlan = ActionFitSdkInstallApi.Plan(sdkProfile, new ActionFitSdkPlanRequest
{
    Operation = ActionFitSdkInstallOperation.Apply,
    SelectedModuleIds = new[] { "core" },
});
// Show sdkPlan.Findings, sdkPlan.Changes, and sdkPlan.PlanId to the user first.
ActionFitSdkExecutionResult sdkResult = await ActionFitSdkInstallApi.ApplyAsync(sdkPlan, sdkPlan.PlanId);
```

- `ActionFitPackageWorkflowApi.Inspect`: 확인 UI 없이 선택적으로 공유 spreadsheet를 refresh하고 최신 catalog 행을 읽어 설치 버전과 비교한 뒤 embedded 변경 상태와 안전한 workflow 선택지를 반환합니다.
- `ActionFitPackageWorkflowApi.InspectJson`: Unity connector 및 AI 도구용 JSON wrapper입니다.
- `ActionFitPackageEmbedApi.GetCandidates`: 내려받을 수 있는 source가 있는 설치 ActionFit 패키지를 나열합니다.
- `ActionFitPackageEmbedApi.Validate`: `Embed for Edit`용 dry 읽기 전용 검증입니다.
- `ActionFitPackageEmbedApi.EmbedForEditAsync`: Package Manager UI가 사용하는 변환 API입니다. Callback은 Unity Package Manager가 끝난 후 최종 `EMBEDDED` 또는 실패 결과를 받습니다.
- `ActionFitPackageEmbedApi.EmbedForEdit`: 시작 결과 중심 편의 API입니다. Downloaded 패키지는 `EMBED_STARTED`를 반환하며 최종 성공이 필요한 호출자는 async overload를 사용하거나 이후 패키지 상태를 확인해야 합니다.
- `ActionFitPackageEmbedApi.ExecuteJson`: Unity connector 및 AI 도구용 JSON 시작 결과 wrapper입니다. 반환된 `EMBED_STARTED`는 최종 변환 결과가 아닙니다.
- `ActionFitPackageEmbedApi.RecoverPendingTransactions`: Editor load 자동 recovery 외에 제공하는 명시적 recovery entry point입니다.
- `ActionFitPackageSkillScaffoldApi.Add` / `AddJson`: 기존 source를 덮어쓰지 않고 embedded 패키지에 schema v2 package skill을 추가합니다. 최초 추가 시 Codex 및 Claude용 `<skillPrefix>-help`와 Codex `agents/openai.yaml` metadata를 만듭니다.
- `ActionFitSdkInstallApi.ReadProfile` / `Inspect` / `Plan`: bridge profile을 검증하고 읽기 전용 설치 상태 또는 content-bound plan을 반환합니다.
- `ActionFitSdkInstallApi.ApplyAsync` / `RepairAsync` / `UpdateAsync` / `RemoveAsync`: 검토한 작업과 정확한 plan ID가 일치할 때만 실행합니다. Dialog나 암묵적 승인은 없습니다.
- `ActionFitSdkInstallApi.InspectPendingTransactions` / `RecoverPendingTransactions`: 상태 변경 없이 대기 journal을 보고하고 명시적 recovery 호출에서만 복원합니다.
- `ActionFitPackagePublishApi.Prepare`: 로컬 패키지 계약을 먼저 실행하고 catalog refresh, 재사용 버전 차단, 패키지 metadata/authentication 검증, GitHub 저장소 및 불변 tag 확인 후 외부 상태를 변경하지 않는 content-bound plan ID를 반환합니다. Catalog source URL이 선택한 Public/Private target과 다르면 저장소 migration plan 전에 실제 공개 범위, default branch, 모든 branch/tag ref와 target 문서도 비교합니다.
- `ActionFitPackagePublishApi.Execute`: 모든 preflight를 다시 실행하고 저장소 push, tag push 및 catalog upsert 전에 같은 plan ID와 정확한 `RequiredApprovalText`를 요구합니다. 저장소 이동에는 `ApproveRepositoryMigration = true`와 정확한 별도 `MigrationApprovalText`도 필요합니다.
- `ActionFitPackagePublishApi.PrepareJson` / `ExecuteJson`: AI connector용 JSON wrapper입니다. 실행이 준비 단계에서 승인을 추론하지 않습니다.
- `ActionFitPackagePublishApi.PrepareCatalogRecovery` / `ExecuteCatalogRecovery`: 기존 불변 remote tag를 로컬 패키지와 검증하고 정확한 recovery 승인을 요구한 뒤 `main`이나 tag를 push하지 않고 누락된 catalog 행만 추가합니다. JSON wrapper는 `PrepareCatalogRecoveryJson` / `ExecuteCatalogRecoveryJson`입니다.
- `ActionFitPackageBulkPublishApi.PrepareAllChanged`: 변경된 embedded 패키지를 찾거나 명시한 패키지 ID 목록을 검증하고 catalog/GitHub 상태를 refresh한 뒤 외부 변경 없는 하나의 content-bound bulk plan을 반환합니다. 불변 tag가 이미 있지만 Catalog 버전이 누락된 패키지는 일반 publish 충돌로 실패하지 않고 검증 후 `CatalogRecoveryPackageIds`로 분류됩니다.
- `ActionFitPackageBulkPublishApi.ExecuteAll`: 정확한 bulk plan ID, 정확한 publish 승인, 정확한 저장소 생성 패키지 집합과 필요 시 정확한 migration 및 Catalog recovery 승인을 요구합니다. 승인된 migration을 먼저 끝내고 `PublishPackageIds`만 저장소 worker에 전달하며 검증된 recovery 행은 저장소 작업 없이 Catalog batch에 합칩니다.
- `ActionFitPackageBulkPublishApi.PrepareAllChangedJson` / `ExecuteAllJson`: AI connector용 JSON wrapper입니다. Manager Console의 `Publish All Changed` button도 같은 API를 호출합니다.
- `ActionFitPackageRepositoryRetirementApi.Prepare` / `Execute`: 검증된 Private-to-Public publish와 Catalog refresh 후 정확한 `Keep`, `Archive`, `Delete` source 처분 하나를 준비하고 실행합니다. `Keep`은 변경하지 않으며 Archive/Delete는 변경 전에 Public 불변 tag, 모든 migration ref, 알려진 모든 Catalog version URL, 현재 PackageInfo target, source 공개 범위와 local 의존성 참조를 다시 확인합니다.
- `ActionFitPackageRepositoryRetirementApi.PrepareBatch` / `ExecuteBatch`: 정확히 선택한 패키지/mode 집합을 bind하고 변경 직전에 전체 집합과 각 항목을 다시 검증한 뒤 source를 순차 retire합니다. 첫 실패에서 중단하고 이후 source를 보존합니다. 네 작업 모두 JSON wrapper를 제공합니다.

Batchmode 호출자는 다음 entry point를 사용할 수 있습니다.

- `-executeMethod ActionFitPackageEmbedCli.Run`, `-actionFitEmbedRequest <request.json>`, `-actionFitEmbedResult <result.json>`을 함께 사용합니다. Unity `-quit` option은 추가하지 않습니다. 이 명령이 비동기 embed 결과를 기다리고 결과 파일을 쓴 뒤 Unity를 직접 종료합니다.
- `-executeMethod ActionFitPackageWorkflowCli.Run`, `-actionFitInspectRequest <request.json>`, `-actionFitInspectResult <result.json>`을 함께 사용합니다.
- `-executeMethod ActionFitPackagePublishCli.Prepare` 또는 `ActionFitPackagePublishCli.Execute`를 `-actionFitPublishRequest <request.json>` 및 `-actionFitPublishResult <result.json>`과 함께 사용합니다.
- `-executeMethod ActionFitPackageBulkPublishCli.PrepareAllChanged` 또는 `ActionFitPackageBulkPublishCli.ExecuteAll`을 `-actionFitBulkPublishRequest <request.json>` 및 `-actionFitBulkPublishResult <result.json>`과 함께 사용합니다.

Inspection은 조언용이며 publish하지 않습니다. Workflow 선택지는 저장소 publish에 `RequiresExplicitPublishApproval`을 표시합니다. 사용자가 외부 작업을 명시적으로 요청하지 않으면 AI는 push, tag, 저장소 생성 또는 catalog 행 추가를 실행하면 안 됩니다.

`Prepare`는 항상 읽기 전용입니다. 성공한 plan은 `PUBLISH com.actionfit.example@1.2.3 PLAN <planId>` 같은 정확한 승인 문자열을 반환합니다. `Execute`는 누락되거나 일치하지 않는 승인을 거부하고 refresh된 catalog와 remote tag를 다시 확인하며 변경된 content hash 또는 plan을 거부합니다. 새 저장소 생성에는 `ApproveRepositoryCreation = true`도 필요합니다. 양방향 저장소 이동은 별도 `MIGRATE ... PLAN <planId>` 승인을 가지며 source 공개 범위를 제자리에서 바꾸거나 publish 중 source를 변경하지 않습니다. Private source는 이후 자체 정확한 승인을 가진 retirement plan에서만 archive 또는 delete할 수 있습니다. 저장소 push는 성공했지만 catalog upsert가 실패하면 저장소를 다시 push하지 않고 `RetryCatalogAppendAvailable = true`를 보고합니다.

Custom Package Manager는 embedded `Packages/com.actionfit.*`와 Git UPM `Library/PackageCache/com.actionfit.*@*` 폴더에서 설치된 `AI_GUIDE.md`를 검색한 뒤 각 `Requested router entry` block으로 `PACKAGE_AI_GUIDE_ROUTER.md`를 refresh합니다. Router 항목은 실제 검색한 guide 경로로 다시 쓰므로 Git UPM 패키지는 `Library/PackageCache/...@hash/AI_GUIDE.md`를 가리킵니다. 사용하는 프로젝트에 primary AI markdown entry point가 이미 있으면 그 옆에 `packages/actionfit-packages.md` 호환 pointer를 생성하고 프로젝트 수준 AI router가 `PACKAGE_AI_GUIDE_ROUTER.md`를 찾을 수 있도록 자동 관리 section을 추가합니다.

자동 router가 등록되기 전에 AI assistant가 이 패키지 문서를 읽더라도 package `AI_GUIDE.md`가 요청 router 항목을 노출하고 `PACKAGE_AI_GUIDE_ROUTER.md`가 router를 연결할 위치를 안내합니다.

## 관리자 콘솔

- `1. Create Package`: 저장소 공개 범위 기본값을 `Public`으로 사용하고 명시적 예외 선택에서만 `Private`을 허용한 뒤 `Packages/com.actionfit.*` 패키지 skeleton, README, AI guide, README 전용 패키지 메뉴 파일, asmdef와 PackageInfo SO를 만듭니다. Visibility가 없는 API 요청은 `Public`으로 정규화하고 지정되지 않은 `Private` 요청 또는 잘못된 enum 값은 거부합니다. 완성된 skeleton은 생성 성공을 반환하기 전에 패키지 소유 round-trip 계약 검증을 통과해야 합니다.
- `2. Publish Changed`: 일반 publish 경로이자 Manager Console의 두 번째 동작입니다. Local `package.json` 버전이 catalog 최신 버전보다 높은 top-level `Packages/com.actionfit.*` 패키지와 아직 등록되지 않은 새 패키지를 찾고 단일/bulk publish에 같은 승인 기반 API를 사용합니다. Test fixture 또는 패키지 콘텐츠 아래 중첩 `package.json`은 publish 후보가 아닙니다. `Publish All Changed`는 일반 publish 행과 검증된 Catalog recovery 행을 분리하고 별도 정확한 recovery 승인을 요구합니다. 최대 4개 worker로 일반 저장소 publish만 실행한 뒤 두 그룹을 하나의 Catalog batch 요청으로 추가합니다. 각 패키지 `ActionFitPackageInfo_SO`의 `Repository Visibility`가 public/private GitHub profile을 선택합니다.
- `Add Agent Skill`: Manager Console의 세 번째 동작입니다. Embedded 패키지 상세 행과 같은 no-overwrite schema v2 scaffolding 창을 엽니다. 최초 추가는 Codex 및 Claude 필수 help source를 만들고 이후 추가는 manifest와 새로 요청한 source 경로만 업데이트합니다.
- `Publish Package`: 이미 등록된 패키지의 특정 버전을 직접 입력할 때 쓰는 수동 publish 경로입니다. 버전을 쓴 뒤 `Publish Changed`와 같은 승인 기반 preflight, migration, 저장소 publish 및 catalog sequence로 진입합니다.
- `Open Catalog`: local 또는 fallback catalog CSV를 선택합니다.
- `Open Manifest`: 프로젝트 `Packages/manifest.json`을 엽니다.
- `Refresh AI Guide Router`: `PACKAGE_AI_GUIDE_ROUTER.md`를 refresh하고 검색한 AI entry point 옆에 local `packages/actionfit-packages.md` 호환 pointer를 다시 생성하며 해당 entry point에 자동 관리 package guide section이 있으면 refresh합니다. Router code는 추가 AI 도구에서 package guide 검색을 중복하지 않도록 AI entry point 등록을 adapter helper 뒤에 유지합니다.

## 카탈로그 및 Manifest

- Local catalog 경로: `Assets/_Data/_CustomPackageManager/package_catalog.csv`
- Fallback catalog 경로: `Packages/com.actionfit.custompackagemanager/Editor/Catalog/package_catalog.csv`
- 선택형 community summary sheet: `package_vote_summary`
- 선택형 community comment sheet: `package_comments`이며 `Update Catalog` 실행 시 `UserSettings/ActionFitPackageManager/package_comments.csv`에 cache합니다.
- 선택형 catalog package type column: `package_type`; 값이 `collection`이면 Package Collections로 분류하고 column이 없는 기존 catalog는 그대로 지원합니다.
- `Update Catalog`는 `package_catalog`의 `package_type`을 local catalog CSV에 보존하며, 값이 없는 legacy row에는 빈 값을 기록합니다.
- 버전을 설치하거나 적용하면 `Packages/manifest.json`을 업데이트하고 Unity Package Manager resolve를 실행합니다.
- Catalog `dependencies` 항목은 선택한 패키지보다 먼저 적용합니다. ActionFit 패키지 의존성을 Git UPM URL로 쓰려면 catalog에 존재해야 하며 ActionFit이 아닌 registry 의존성은 raw 버전 문자열을 fallback으로 사용합니다.
- 펼친 패키지 행은 선택 버전의 의존성을 표시합니다. ActionFit 의존성은 가능한 경우 해석된 catalog Git URL을 표시하고 registry/raw 의존성은 raw 버전 값을 표시합니다.
- `Update Catalog`는 release note 같은 quoted multi-line CSV cell을 보존해 이후 `dependencies` 같은 column이 정렬되고 패키지 상세에 정확히 표시되게 합니다.
- Manifest 의존성 형식은 쓸 때 정규화합니다.

## Publish 참고 사항

`Publish Package`와 `Publish Changed`는 upload 전에 catalog를 refresh하고 같은 `package_id@version`이 이미 있으면 publish를 차단합니다. Local publish clone을 생성 또는 refresh하고 복사한 패키지 파일을 commit한 뒤 `main`과 필요 시 version tag를 push하고 catalog 행을 추가합니다. 중복 버전이 발견되면 기존 Git tag/catalog 행을 덮어쓰지 않고 중단하는 것이 기본 정책입니다. `package.json` 또는 `Publish Version`을 바꾸거나 별도 패키지/저장소로 분리해야 하면 `Fork as New`를 사용합니다. Unity Console은 저장소 확인, clone 경로, 파일 복사, commit/tag, branch push, tag push 및 catalog append 단계에 `[ActionFitPackageManager]` log를 출력합니다.

Publish preflight는 저장소는 존재하지만 첫 commit이 아직 없는 경우도 지원합니다. Tag 조회에서 GitHub가 반환하는 empty-repository conflict는 저장소를 existing으로 유지하면서 tag를 사용 가능하다고 처리합니다. Git command output stream은 동시에 drain해 line-ending warning을 포함한 많은 경고 출력이 publish를 막지 않게 합니다.

`Publish All Changed`는 승인 전에 패키지 계약을 한 번 검증하고 실행 중 같은 in-process 승인 plan을 재사용하며, 변경 직전에 변경 가능한 catalog, content hash, version, repository, tag 및 recovery equivalence를 다시 확인합니다. Validation receipt는 직렬화되지 않으므로 deserialize하거나 API로 제공한 plan data가 계약 검증을 우회할 수 없습니다. 일치하는 기존 tag는 plan에서 `Recover Catalog only`로 표시합니다. Content 또는 visibility가 다르면 차단하고 다음 patch 버전을 권장합니다. GitHub remote preflight와 일반 저장소 publish는 최대 4개 worker로 실행합니다. Recovery 후보는 저장소 worker에 들어가지 않으며 publish 승인과 별도로 `CatalogRecoveryApprovalText`가 필요합니다. Progress dialog는 local 검증, GitHub 확인, 저장소 publish, catalog batch/fallback 및 최종 refresh 단계를 표시하고 변경 전 취소하거나 저장소 publish 완료 후 catalog 등록 전에 중단할 수 있습니다.

기존 catalog 저장소 URL이 선택한 Public 또는 Private publish target과 다르면 `Publish Changed`가 명시적 저장소 migration으로 처리합니다. Preflight는 두 저장소의 실제 GitHub visibility, default branch, 모든 branch/tag ref와 현재 버전 tag 충돌을 표시하고 `README.md`와 `AI_GUIDE.md`가 모두 target URL을 참조하도록 요구합니다. 누락된 target은 기존 생성 승인으로만 만들 수 있습니다. 이후 source branch와 tag를 force 또는 prune 없이 target에 mirror하고 target default branch를 맞춘 뒤 모든 ref를 다시 확인합니다. Tag는 정확한 불변 SHA를 유지해야 합니다. Target branch가 source commit을 ancestor로 포함한다고 GitHub가 증명할 때만 ahead를 허용합니다. 이는 새 패키지 commit publish 후 기대하는 상태입니다. Behind, diverged, missing 또는 검증 불가 ancestry는 차단합니다. 잘못된 visibility 또는 안전하지 않은 target ref가 있는 target도 차단하고 같은 저장소를 제자리에서 변경하지 않습니다. Migration, 패키지 publish 및 Catalog 업데이트 중 source는 변경하지 않습니다.

검증된 Private-to-Public migration에서는 각 publish 행에 기본값 `Keep`인 `Previous Repository`가 표시됩니다. `Archive`와 `Delete`는 별도 post-publish 단계입니다. 새 Public tag와 Catalog 행이 존재한 뒤에만 준비하고 정확한 source/target/ref 변경 및 경고를 보여주며 content-bound 승인을 요구하고 변경 직전에 모든 검사를 refresh합니다. 알려진 Catalog 버전이나 현재 프로젝트 manifest, lock 파일 또는 embedded 패키지 metadata가 기존 Private URL을 참조하면 차단합니다. Archive는 복구 가능하며 권장합니다. Delete는 되돌릴 수 없고 GitHub Issue, pull request, 설정, secret, Actions 설정, star, fork 및 기타 저장소 metadata는 migration하지 않습니다. 현재 checkout으로 외부 consumer를 증명할 수 없으므로 해당 의존성은 별도로 확인해야 합니다. Bulk retirement는 명시적으로 선택한 행에만 적용하고 첫 실패에서 중단해 이후 source를 보존합니다.

Catalog와 GitHub HTTP 요청은 30초 connection/read timeout을 사용합니다. Catalog Web App은 `upsertPackageVersions`를 지원하고 일치하는 `count` 또는 항목별 확인을 반환해야 합니다. 지원하지 않는 batch response는 serial `upsertPackageVersion`으로 fallback합니다. Timeout 또는 취소 실패에서는 오래 걸릴 수 있는 serial fallback을 시작하지 않습니다. 저장소 publish는 성공했지만 catalog append가 실패하거나 취소되면 창이 해당 행을 유지하고 `Retry Catalog Append`를 표시해 저장소를 다시 push하지 않고 spreadsheet 업데이트를 재시도할 수 있게 합니다. Unity Console은 catalog refresh, GitHub preflight, 저장소 publish, batch append, serial fallback 및 전체 bulk 실행의 경과 millisecond를 기록합니다.

창을 다시 만들거나 domain reload 또는 Editor restart 후 창이 보관하던 retry 행을 더 이상 사용할 수 없으면 변경 패키지 행의 `Recover Catalog Entry`를 사용합니다. Recovery에는 refresh된 catalog에 해당 버전이 없고 불변 remote tag와 저장소 visibility가 일치하며 checkout한 tag content가 안전한 `_fingerprint`, JSON whitespace 및 Unity PackageInfo YAML serialization 정규화만 제외하고 local 패키지와 일치해야 합니다. 불일치는 차단하고 다음 patch 버전을 권장합니다. 성공한 recovery는 catalog upsert와 refresh만 수행하며 저장소 생성, branch/tag push, move, delete 또는 overwrite를 하지 않습니다.

`Settings`는 `GitHub Publish Default`에 GitHub token 하나와 public/private 저장소별 생성 organization을 저장합니다. `_githubToken`은 한 번 입력하고 저장소 owner가 다르면 `Repo Creation - Public`과 `Repo Creation - Private` org 값을 설정합니다. 새 패키지 기본 target은 `Repo Creation - Public`이며 private profile은 명시적 예외 선택 후에만 사용합니다. Private catalog 항목은 private GitHub 저장소를 가리킬 수 있으므로 사용하는 프로젝트도 설치를 위한 GitHub 접근 권한이 필요합니다. 설정한 token을 패키지 파일이나 저장소 metadata에 복사하면 안 됩니다.

패키지 콘텐츠를 준비하기 전에 publisher가 local publish clone을 `origin/main`에서 refresh해 오래된 cache clone이 준비 상태에 영향을 주지 않게 합니다.

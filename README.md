# Custom Package Manager (com.actionfit.custompackagemanager)

ActionFit 전용 UPM 패키지 카탈로그를 표시하고, 선택한 패키지 버전을 `Packages/manifest.json`에 Git URL dependency로 적용하는 Unity 에디터 툴입니다.

## 설치 (manifest.json, Git URL)

```json
{
  "dependencies": {
    "com.actionfit.custompackagemanager": "https://github.com/ActionFit-Editor/Custom_Package_Manager.git#1.1.15"
  }
}
```

## 사용

- 메뉴: `Tools > ActionFit > Package Manager`
- 설정 SO: 설치 후 `Assets/_Data/_CustomPackageManager/ActionFitPackageCatalogSettings_SO.asset`이 없으면 자동 생성됩니다. 이 SO에 Spreadsheet URL, Web App URL, Token, GitHub Org, GitHub Token, Publish Root를 입력합니다.
- 카탈로그: `Assets/_Data/_CustomPackageManager/actionfit_package_catalog.csv`가 있으면 우선 사용하고, 없으면 패키지 내부 `Editor/Catalog/actionfit_package_catalog.csv`를 fallback으로 사용합니다.
- Update: 설정 SO의 스프레드시트에서 `actionfit_package_catalog` 탭을 받아 로컬 카탈로그 CSV를 덮어쓴 뒤 목록을 새로고침합니다.
- 1. Create Package: 새 `Packages/com.actionfit.*` 패키지 폴더와 `package.json`, README, Editor asmdef, `Editor/Scripts`, PackageInfo SO를 생성합니다.
- 2. Create Repo: 아직 카탈로그에 등록되지 않은 PackageInfo SO를 스캔해 첫 레포 생성/카탈로그 등록 창을 엽니다.
- 3. Publish Package: 이미 카탈로그에 등록된 PackageInfo SO를 스캔해 버전 입력 후 업데이트 배포 창을 엽니다.
- README: 패키지 생성, 배포, 업데이트 플로우를 확인할 수 있도록 전용 README 창을 엽니다.
- 설치/버전 적용: 선택한 패키지와 카탈로그에 등록된 dependency를 `Packages/manifest.json`에 반영한 뒤 Package Manager resolve를 실행합니다.
- 삭제: manifest dependency로 설치된 패키지를 `Packages/manifest.json`에서 제거하고 Package Manager resolve를 실행합니다. `Packages/` 아래 embedded 패키지는 자동 삭제하지 않습니다.
- 카탈로그 목록: 단일 카탈로그 안에서 다운로드 받은 패키지와 다운로드 가능한 패키지를 섹션으로 나눠 함께 볼 수 있습니다.

## 새 패키지 생성

1. 새 패키지는 `1. Create Package`로 기본 폴더와 파일을 생성합니다.
2. 생성 창에서 `Package Id`, `Display Name`, `Repo Name`, `Version`, `Unity`, 설명, 릴리즈 노트를 입력합니다.
3. 생성 후 아래 기본 구조가 만들어졌는지 확인합니다.

```text
Packages/com.actionfit.yourpackage/
├── package.json
├── README.md
└── Editor/
    ├── com.actionfit.yourpackage.Editor.asmdef
    ├── Scripts/
    └── PackageInfo/
        └── ActionFitPackageInfo_SO.asset
```

4. 배포할 에디터 스크립트와 파일을 패키지 폴더 안에 추가합니다. 에디터 전용 코드는 기본적으로 `Editor/Scripts`에 둡니다.
5. 런타임 코드가 필요한 패키지는 `Runtime` 폴더와 Runtime asmdef를 별도로 추가합니다.

## 첫 등록

1. `2. Create Repo`를 누르면 아직 카탈로그에 없는 PackageInfo SO 목록이 표시됩니다.
2. 설명, 릴리즈 노트, 레포명을 확인합니다.
3. `2. Create Repo`로 GitHub 레포 생성/확인, push/tag, 카탈로그 append를 실행합니다.

## 배포

1. 설정 SO에 Spreadsheet URL, Web App URL, Token, GitHub Org, GitHub Token, Publish Root를 입력합니다.
2. Package Manager 창에서 `2. Create Repo` 또는 `3. Publish Package`를 누릅니다.
3. 창에서 대상 패키지를 확인하고 실행합니다.
4. 툴이 GitHub 레포를 생성/확인하고, 패키지 내용을 `Publish Root`에 복사한 뒤 commit, push, tag를 수행합니다.
5. Apps Script에 카탈로그 append/upsert를 요청합니다.
6. 카탈로그 CSV를 다시 동기화하고 Package Manager 목록을 갱신합니다.

## 업데이트

1. 기존 패키지의 파일을 수정합니다.
2. Package Manager 창에서 `3. Publish Package`를 누릅니다.
3. `ActionFitPackageInfo_SO`의 릴리즈 노트를 새 버전에 맞게 수정합니다.
4. Publish Version에 배포할 버전을 직접 입력하고 `3. Publish Package`를 누릅니다.

## Dependencies 설정

Dependencies는 카탈로그의 `dependencies` 열에 저장되고, 해당 패키지를 설치/버전 적용할 때 먼저 `Packages/manifest.json`에 반영됩니다.

- 특정 버전 고정: `com.actionfit.csvimporter@1.2.3`
- 카탈로그 latest 사용: `com.actionfit.csvimporter`
- 여러 개 입력: `com.actionfit.csvimporter@1.2.3;com.actionfit.sosingleton@1.0.0`

여러 dependency는 `;`, `,`, `|`로 구분할 수 있습니다. `package.json`의 `"dependencies"` 값은 배포 시 자동으로 `패키지ID@버전` 형태로 추출되며, `ActionFitPackageInfo_SO`의 `Dependencies Override`에 값이 있으면 override 값이 우선 사용됩니다.

Apps Script는 JSON POST의 `action: "upsertPackageVersion"`을 받아 `package_catalog`/`package_versions`와 기존 `actionfit_package_catalog` 탭에 append/upsert하도록 구성되어 있어야 합니다. 응답에는 `success`, `package_id`, `version`, `catalog_id`, `legacy_catalog_updated`가 포함되어야 하며, `legacy_catalog_updated`가 `true`가 아니면 Unity는 카탈로그 append 실패로 처리합니다.

GitHub Token은 레포 생성과 push 권한을 가지므로 실제 값은 커밋하지 말고 각 개발자 로컬 설정 SO에서만 입력합니다.

## 구성

- **Editor** (`com.actionfit.custompackagemanager.Editor`):
  - `ActionFitPackageManagerWindow` — 패키지 목록, 설치 상태, 버전 선택 UI
  - `ActionFitPackagePublishWindow` — 첫 등록과 업데이트 배포 대상 스캔/편집/실행 UI
  - `ActionFitPackageReadmeWindow` — 패키지 생성/배포 플로우 README 표시 창
  - `ActionFitPackageCatalogSettings_SO` — 스프레드시트 동기화 설정
  - `ActionFitPackageInfo_SO` — 패키지별 배포/카탈로그 등록 메타데이터
  - `actionfit_package_catalog.csv` — ActionFit 패키지 카탈로그

# Custom Package Manager (com.actionfit.custompackagemanager)

ActionFit 내부 UPM 패키지 카탈로그를 조회하고, 선택한 패키지를 `Packages/manifest.json`의 Git URL dependency로 설치하거나 버전을 변경하는 Unity 에디터 툴입니다.

## Install

```json
{
  "dependencies": {
    "com.actionfit.custompackagemanager": "https://github.com/ActionFit-Editor/Custom_Package_Manager.git#1.1.28"
  }
}
```

## Menu

- `Tools > ActionFit > Package Manager > Package Manager`: 패키지 설치, 버전 적용, 삭제, 업데이트 확인을 관리합니다.
- `Tools > ActionFit > Package Manager > Manager Console`: 패키지 생성, 저장소 생성, 배포, README/카탈로그/manifest 열기 같은 운영 기능을 모아둔 별도 창입니다.

## Package Manager

- `Reload`: 현재 카탈로그와 설치 상태를 다시 읽습니다.
- `Update Catalog`: 설정 SO에 저장된 Spreadsheet/Web App 설정을 사용해 로컬 카탈로그 CSV를 갱신합니다.
- `Settings`: `Assets/_Data/_CustomPackageManager/ActionFitPackageCatalogSettings_SO.asset`을 선택합니다.
- `Updates`: 설치된 패키지 중 카탈로그 latest와 현재 버전이 다른 항목을 모아서 보여줍니다.
- `Console`: 운영 기능을 모아둔 Manager Console 창을 엽니다.

패키지 목록은 Package Manager, Embedded Packages, Downloaded Packages, Available Packages로 구분됩니다. Embedded package는 `Packages/` 아래에 직접 존재하는 패키지이며, 다른 버전을 적용하면 Git UPM dependency로 전환하고 embedded 폴더를 제거한 뒤 Package Manager resolve를 실행합니다.

## Updates

`Updates` 패널은 현재 프로젝트에 적용된 패키지를 기준으로 업데이트 가능 여부를 보여줍니다.

- Downloaded package는 개별 업데이트, 선택 업데이트, 전체 업데이트를 사용할 수 있습니다.
- Embedded package는 목록에 표시되며, 현재 버전과 다른 버전을 선택하면 Git UPM dependency로 전환하는 업데이트를 실행할 수 있습니다.
- `Changes`는 현재 설치 버전 다음 버전부터 선택한 버전까지의 업데이트 내역을 보여줍니다.
- `History`는 해당 패키지의 전체 버전 내역을 최신부터 초기 버전까지 보여줍니다.

예를 들어 현재 버전이 `1.0.1`이고 선택한 버전이 `1.0.4`이면 `Changes`는 `1.0.2`, `1.0.3`, `1.0.4`의 changelog를 함께 보여줍니다.

## Manager Console

운영성 버튼은 메인 Package Manager 창에서 분리되어 Manager Console에 있습니다.

- `1. Create Package`: `Packages/com.actionfit.*` 패키지 뼈대와 PackageInfo SO를 생성합니다.
- `2. Create Repo`: 카탈로그에 아직 등록되지 않은 PackageInfo SO를 기준으로 GitHub 저장소와 카탈로그 행을 생성합니다.
- `3. Publish Package`: 이미 등록된 PackageInfo SO를 기준으로 버전 배포를 진행합니다.
- `Publish Changed`: 현재 `package.json` 버전과 카탈로그 최신 버전을 비교해 변경된 패키지를 찾아 배포합니다.
- `README`: 패키지 사용법과 배포 설명을 볼 수 있는 README 창을 엽니다.
- `Open Catalog`: 로컬 카탈로그 CSV를 선택합니다.
- `Open Manifest`: 프로젝트 `Packages/manifest.json`을 엽니다.
- `Settings`: 카탈로그 설정 SO를 선택합니다.

## Catalog And Manifest

- 로컬 카탈로그 기본 경로는 `Assets/_Data/_CustomPackageManager/package_catalog.csv`입니다.
- 로컬 카탈로그가 없으면 패키지 내부 fallback 카탈로그 `Packages/com.actionfit.custompackagemanager/Editor/Catalog/package_catalog.csv`를 사용합니다.
- 설치와 버전 적용은 `Packages/manifest.json` dependencies 블록을 갱신한 뒤 Unity Package Manager resolve를 실행합니다.
- manifest dependency 블록은 쓰기 시 4칸 들여쓰기, 빈 줄 제거, 마지막 항목 trailing comma 제거 형식으로 정리됩니다.

## Publish Notes

이 패키지는 실제 GitHub push/tag 배포를 자동으로 실행하지 않습니다. 배포가 필요하면 Unity에서 `Tools > ActionFit > Package Manager > Manager Console`을 열고 `3. Publish Package` 또는 `Publish Changed`를 수동으로 실행하세요.

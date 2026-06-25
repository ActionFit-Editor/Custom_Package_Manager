# Custom Package Manager (com.actionfit.custompackagemanager)

ActionFit 전용 UPM 패키지 카탈로그를 표시하고, 선택한 패키지 버전을 `Packages/manifest.json`에 Git URL dependency로 적용하는 Unity 에디터 툴입니다.

## 설치 (manifest.json, Git URL)

```json
{
  "dependencies": {
    "com.actionfit.custompackagemanager": "https://github.com/ActionFit-Editor/Custom_Package_Manager.git#1.0.0"
  }
}
```

## 사용

- 메뉴: `Tools > ActionFit > Package Manager`
- 카탈로그: 패키지 내부 `Editor/Catalog/actionfit_package_catalog.csv`
- 설치/버전 적용: 선택한 패키지와 카탈로그에 등록된 dependency를 `Packages/manifest.json`에 반영한 뒤 Package Manager resolve를 실행합니다.

## 구성

- **Editor** (`com.actionfit.custompackagemanager.Editor`):
  - `ActionFitPackageManagerWindow` — 패키지 목록, 설치 상태, 버전 선택 UI
  - `actionfit_package_catalog.csv` — ActionFit 패키지 카탈로그

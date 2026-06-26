# AI_READ_THIS.md

이 파일은 WebApp Launcher 프로젝트에 기여하는 AI 코딩 에이전트를 위한 가이드입니다. 변경을 시작하기 전에 이 문서를 먼저 읽으세요.

## 프로젝트 개요

WebApp Launcher는 Windows용 경량 웹앱 런처입니다.

- GitHub 저장소의 `.wapk` manifest를 해석해 웹앱을 설치/실행합니다.
- WPF + WebView2 기반 GUI를 제공합니다.
- Python/Node.js 런타임과 Git/uv/pnpm 도구를 `~/.webapp` 아래에 중앙 관리합니다.
- CLI, Bootstrapper(런타임 설치), PyInstaller GUI 설치 프로그램으로 구성됩니다.

## 핵심 규격

- **앱 manifest**: `APP_CONTRACT.md`의 `.wapk` format 2를 따릅니다.
- **Window API**: `window.webapp.window.*` 형태로 최상위 문서에 주입됩니다.
- **데이터 경로**: 앱의 영구 데이터는 `WEBAPP_DATA_DIR` 아래에 저장해야 합니다.

## 프로젝트 구조

```text
WebAppLauncher/              WPF 데스크톱 앱, MainWindow.xaml.cs가 host-webapp 브리지 역할
  Ui/                        HTML/CSS/JS UI (WebView2에서 로드)
WebAppLauncher.Core/         공유 비즈니스 로직
  AppInstaller.cs            앱 설치
  AppLauncher.cs             앱 실행/세션/프로세스 관리
  AppRepository.cs           설치된 앱 메타데이터
  AppUpdateManager.cs        앱 업데이트
  RuntimeInspector.cs        런타임 버전/상태 진단
  RuntimeUpdateManager.cs    런타임 업데이트
  ToolResolver.cs            git/uv/pnpm/python/node 경로 해결
  WebAppPaths.cs             ~/.webapp 레이아웃
WebAppLauncher.Cli/          명령줄 진입점
WebAppLauncher.Bootstrapper/ 런타임/도구 설치 진입점
WebAppLauncher.Tests/        xUnit 테스트
Installer/                   PyInstaller 기반 GUI 설치 프로그램
  installer.py               tkinter 설치 마법사
  build-installer.ps1        인스톨러 빌드 스크립트
```

## 환경 규칙

- **Python**: 표준 `venv` + `pip`를 사용합니다. `uv` 사용은 금지됩니다.
- **Node**: `npm`를 사용합니다. `pnpm` 사용은 금지됩니다.
- **패키지 매니저**: `scoop` 사용을 권장합니다.
- **인스톨러 가상환경**: 루트 공유 `.venv` 대신 `Installer/.venv`를 사용합니다.

## 빌드/검증

```powershell
# 전체 솔루션 빌드
dotnet build WebAppLauncher.slnx -c Release

# 테스트
dotnet test WebAppLauncher.slnx -c Release

# 인스톨러 빌드
Installer/build-installer.ps1 -Configuration Release -Runtime win-x64 -ProductVersion 0.1.0
```

모든 C# 변경 후에는 `dotnet build`와 `dotnet test`를 실행하세요.

## 코딩 규칙

- **최소 변경**: 목표 달성에 필요한 최소한의 수정만 하세요.
- **기존 스타일 유지**: 들여쓰기, 명명법, 네임스페이스 스타일을 기존 코드와 맞춥니다.
- **한국어 UI**: 사용자에게 보이는 메시지는 한국어로 작성합니다.
- **코멘트**: 복잡한 로직에는 간단한 한국어 또는 영어 설명을 추가합니다.
- **타입 안전**: C#에서는 nullable 참조 타입을 존중하고, 경고를 추가하지 마세요.

## 자주 참조하는 파일

- `APP_CONTRACT.md`: 앱 개발자가 따라야 할 규격
- `WebAppLauncher.Core/WebAppPaths.cs`: 경로 규칙
- `WebAppLauncher.Core/ToolResolver.cs`: 외부 도구 해결
- `WebAppLauncher.Core/Models.cs`: 공유 데이터 모델
- `WebAppLauncher/MainWindow.xaml.cs`: WebView2 ↔ C# 브리지
- `WebAppLauncher/Ui/app.js`: 프론트엔드 상태/렌더링
- `Installer/installer.py`: 설치 프로그램 흐름

## 변경 시 체크리스트

- [ ] `dotnet build`가 성공합니다.
- [ ] `dotnet test`가 통과합니다.
- [ ] UI 메시지가 한국어로 자연스럽습니다.
- [ ] 인스톨러/런타임 관련 변경 시 `Installer/build-installer.ps1`로 빌드해 봅니다.
- [ ] `AGENTS.md`나 `AI_READ_THIS.md`에 언급된 규칙을 위반하지 않습니다.

## 제약 사항

- Windows 전용 프로젝트입니다.
- PyInstaller/Electron 같은 무거운 단일 실행 파일화는 지양합니다.
- 런타임은 되도록 중앙 `.webapp` 디렉터리를 사용하고, 시스템 전역 설치를 피합니다.

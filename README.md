# WebApp Launcher

Windows용 경량 웹앱 런처입니다. GitHub 저장소의 `.wapk` manifest를 읽어 WebView2 창에서 웹앱을 실행하고, Python/Node.js 런타임과 도구를 중앙에서 관리합니다.

## 주요 기능

- **GitHub 기반 앱 설치**: `owner@repo` 형식의 package id로 저장소를 클론해 웹앱을 설치합니다.
- **통합 런타임 관리**: Python, Node.js, Git, uv, pnpm, WebView2를 별도 설치 없이 `.webapp` 아래에서 관리합니다.
- **WebView2 기반 실행**: 앱은 HTML/JS 프론트엔드와 선택적 Python/Node 백엔드로 구성됩니다.
- **창/프로세스 제어**: 커스텀 타이틀 바, 최소화/최대화/전체화면, 프로세스 관리자, 런타임 업데이트를 지원합니다.

## 프로젝트 구조

```text
WebAppLauncher/          WPF 데스크톱 앱 (WebView2 + HTML UI)
WebAppLauncher.Core/     공유 로직 (설치, 실행, 런타임, 설정)
WebAppLauncher.Cli/      명령줄 인터페이스
WebAppLauncher.Bootstrapper/  런타임/도구 설치기
WebAppLauncher.Tests/    xUnit 테스트
Installer/               PyInstaller 기반 GUI 설치 프로그램
APP_CONTRACT.md          `.wapk` manifest 및 Window API 규격
```

## 빌드 방법

### 요구 사항

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Python 3 (인스톨러 빌드용)

### 솔루션 빌드

```powershell
dotnet build WebAppLauncher.slnx -c Release
dotnet test WebAppLauncher.slnx -c Release
```

### 인스톨러 빌드

```powershell
Installer/build-installer.ps1 -Configuration Release -Runtime win-x64 -ProductVersion 0.1.0
```

빌드 결과는 `artifacts/` 폴터에 생성됩니다.

## 사용 방법

### GUI 런처

```powershell
artifacts/publish/WebAppLauncher/WebAppLauncher.exe
```

### CLI

```powershell
WebAppLauncher.Cli/bin/Release/net10.0/WebAppLauncher.Cli.exe --help
WebAppLauncher.Cli/bin/Release/net10.0/WebAppLauncher.Cli.exe install owner@repo
WebAppLauncher.Cli/bin/Release/net10.0/WebAppLauncher.Cli.exe run owner@repo
WebAppLauncher.Cli/bin/Release/net10.0/WebAppLauncher.Cli.exe doctor
```

## `.wapk` manifest

앱은 저장소 루트의 `webapp.wapk` 파일로 규격을 정의합니다. 자세한 내용은 [`APP_CONTRACT.md`](APP_CONTRACT.md)를 참조하세요.

```toml
[wapk]
format = 2

[package]
id = "owner@repository"
name = "Example App"

[source]
provider = "github"
owner = "owner"
repo = "repository"
branch = "main"
commit = "*"
app_dir = "."

[runtime]
python = "python313"
node = "none"

[entry]
html = "app.html"
python = "app.py"
icon = "icon.png"

[window]
width = 1200
height = 800
resizable = true
```

## 라이선스

WebApp Launcher는 [Apache License 2.0](LICENSE) 하에 배포됩니다.
사용된 서드파티 라이브러리 및 런타임 정보는 [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md)를 참조하세요.

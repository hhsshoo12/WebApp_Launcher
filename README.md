# WAPL (WebApp Launcher)

> GitHub 기반 로컬 웹앱 런처

WAPL(WebApp Launcher)은 **로컬 웹앱을 쉽고 일관되게 배포하고 실행하기 위한 플랫폼**입니다.

Python, Node.js 등의 런타임을 내장하고 있으며, 개발자는 GitHub 저장소만 공개하면 사용자는 복잡한 개발 환경 설치 없이 `.wapk` 설치 레시피를 통해 앱을 설치하고 실행할 수 있습니다.

---

# 주요 기능

* GitHub 기반 앱 설치
* 내장 Python 및 Node.js 런타임
* Git, uv, pnpm 기본 제공
* `.wapk` 패키지 규격
* 런타임 자동 관리
* Local-first 설계

---

# 왜 WAPL을 만들었나요?

물론 Python이나 Node.js로 만든 애플리케이션은 PyInstaller, Electron, Tauri 등의 도구를 이용하여 실행 파일(EXE)로 배포할 수 있습니다.

하지만 작은 로컬 웹앱을 개발하고 공유하는 과정에서는 다음과 같은 불편함이 있습니다.

* 수정할 때마다 다시 빌드해야 합니다.
* 빌드 과정이 오래 걸릴 수 있습니다.
* 테스트 중에도 매번 실행 파일을 다시 생성해야 합니다.
* 앱마다 Python이나 Node.js 런타임이 중복 포함되어 용량이 커질 수 있습니다.
* 간단한 도구를 공유하기에도 배포 과정이 번거롭습니다.

WAPL은 이러한 불편함을 줄이기 위해 만들어졌습니다.

런타임은 한 번만 설치하고, 앱은 `.wapk` 설치 레시피를 통해 GitHub 저장소에서 받아 실행합니다.

개발자는 빌드보다 개발에 집중할 수 있고, 사용자는 `.wapk` 파일 하나로 앱을 손쉽게 설치할 수 있습니다.

> WAPL은 EXE를 대체하기 위한 프로젝트가 아닙니다.
> AI와 사람이 만든 작은 로컬 웹앱을 더 쉽게 배포하고 실행하기 위한 플랫폼입니다.

---

# 기본 런타임

### Python

* Python 3.14
* Python 3.13

### Node.js

* Node.js 24 (LTS)
* Node.js 22 (LTS)

### 도구

* Git
* uv
* pnpm

---

# 보안

WAPL은 다음과 같은 최소한의 검증을 수행합니다.

* GitHub Public Repository만 허용
* Commit Hash 형식 검증 및 Git `rev-parse` 검증
* Runtime 업데이트 패키지 SHA256 검증

커뮤니티 앱의 안전성을 보증하지는 않습니다.
설치 전 GitHub 저장소를 직접 확인하는 것을 권장합니다.

---

# AI 개발 지원

AI를 이용하여 WAPL 앱을 개발하는 경우 프로젝트 루트의 `AI_READ_THIS.md`를 먼저 읽어 주세요.

해당 문서에는 WAPL 애플리케이션 구조와 개발 규칙이 정의되어 있습니다.

---

# 라이선스

자세한 내용은 `LICENSE`를 참고하세요.

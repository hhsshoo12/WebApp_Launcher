# AI_READ_THIS.md

> 이 문서는 **WebApp Launcher(WAPL)로 웹앱을 제작하는 바이브 코더의 AI 에이전트**를 위한 규격서입니다.
> WAPL은 Electron/Tauri 같은 무거운 프레임워크 없이, HTML + Python/Node.js 조합으로 가벼운 데스크톱 웹앱을 만들고 배포할 수 있도록 설계되었습니다.
> AI는 반드시 아래 규격을 준수하여 웹앱을 작성하세요.

---

## WAPL이란?

**WAPL(WebApp Launcher)**는 Windows용 경량 웹앱 런처입니다.

- GitHub 저장소의 `app.wapk` manifest를 읽어 웹앱을 설치/실행합니다.
- 별도의 Electron/Tauri 빌드 없이 **HTML + (선택) Python 또는 Node.js 백엔드**로 데스크톱 앱처럼 동작합니다.
- 런타임(Python, Node.js, Git 등)은 WAPL이 중앙 관리하므로 앱은 소스 코드만 제공하면 됩니다.
- AI는 WAPL 규격만 맞추면 복잡한 패키징/배포 없이 즉시 실행 가능한 앱을 만들 수 있습니다.

---

## 폴터 구조

WAPL은 저장소를 그대로 클론하여 실행합니다. 최상위 폴터 구조는 다음과 같이 구성하세요.

```text
my-webapp/
├── app.wapk          # WAPL manifest (필수)
├── app.html          # 앱 진입 HTML (필수)
├── app.py            # Python 백엔드 진입점 (선택)
├── app.js            # Node.js 백엔드 진입점 (선택)
├── backend/          # 백엔드 추가 모듈/소스 (선택)
├── assets/           # 이미지, 폰트, 정적 리소스 (선택)
├── data/             # 런타임 영구 데이터 (선택, 권장)
└── webapp.wapk       # app.wapk의 별칭 (레거시, 사용하지 마세요)
```

### 필수 파일

- `app.wapk`: 앱 메타데이터와 실행 규격
- `app.html`: WebView2에 로드되는 메인 UI

### 선택 파일

- `app.py`: Python 기반 백엔드 서버
- `app.js`: Node.js 기반 백엔드 서버
- `backend/`: 백엔드 코드가 커질 때 분리 보관용

---

## app.wapk 설명

`app.wapk`는 TOML 형식의 manifest 파일입니다. 최소 구성은 다음과 같습니다.

```toml
[wapk]
format = 2

[package]
id = "owner@repository"
name = "My App"

[source]
provider = "github"
owner = "owner"
repo = "repository"
branch = "main"
commit = "*"
app_dir = "."

[runtime]
python = "python313"   # "none", "python313", "python314"
node = "none"          # "none", "nodejs-lts-22", "nodejs-lts-24"

[entry]
html = "app.html"
python = "app.py"      # python 런타임 사용 시
# node = "app.js"      # node 런타임 사용 시
icon = "assets/icon.png"

[window]
width = 1200
height = 800
resizable = true
devtools = false
transparent = false
borderless = false
fullscreen = false
always_on_top = false
start_maximized = false
instance_mode = "new_backend"  # "focus_existing", "share_backend", "new_backend"
```

### 주요 필드

- `[package].id`: `owner@repo` 형식의 고유 ID
- `[runtime].python` / `[runtime].node`: 사용할 런타임. 둘 중 하나만 사용하거나 둘 다 사용할 수 있습니다.
- `[entry].html`: 반드시 존재하는 메인 HTML 파일
- `[entry].python` / `[entry].node`: 백엔드 진입점
- `[window].instance_mode`: 앱 재실행 시 창/백엔드 공유 정책
  - `focus_existing`: 이미 실행 중이면 기존 창을 포커스
  - `share_backend`: 같은 백엔드를 여러 창에서 공유
  - `new_backend`: 실행할 때마다 새 백엔드 생성

자세한 규격은 [`APP_CONTRACT.md`](APP_CONTRACT.md)를 참조하세요.

---

## app.py / app.js 역할

`app.py` 또는 `app.js`는 선택적 백엔드 서버 진입점입니다.

- **HTTP 서버로 동작**해야 합니다.
- **반드시 `--host`와 `--port` 인자를 지원**해야 합니다.
- WAPL이 지정한 호스트/포트에서 listen해야 앱과 통신할 수 있습니다.
- 백엔드는 앱의 데이터 처리, 파일 I/O, 외부 API 호출 등을 담당합니다.

### Python 예시 (`app.py`)

```python
import argparse
from http.server import HTTPServer, BaseHTTPRequestHandler

parser = argparse.ArgumentParser()
parser.add_argument("--host", default="127.0.0.1")
parser.add_argument("--port", type=int, default=8080)
args = parser.parse_args()

class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(b'{"status":"ok"}')

HTTPServer((args.host, args.port), Handler).serve_forever()
```

### Node.js 예시 (`app.js`)

```javascript
const http = require("http");
const args = require("minimist")(process.argv.slice(2), {
  default: { host: "127.0.0.1", port: 8080 }
});

http.createServer((req, res) => {
  res.writeHead(200, { "Content-Type": "application/json" });
  res.end(JSON.stringify({ status: "ok" }));
}).listen(args.port, args.host);
```

---

## app.html 역할

`app.html`은 WAPL 창에 로드되는 메인 UI입니다.

- 일반적인 HTML/CSS/JS로 작성합니다.
- WAPL은 `window.webapp.window.*` API를 최상위 문서에 주입합니다.
  - 최소화, 최대화, 전체화면, 창 이동, 닫기 등
- `app.html`은 백엔드 HTTP API를 호출하여 동적인 기능을 구현합니다.
- 커스텀 타이틀 바를 만들 때 `window.webapp.window.startDrag()`를 pointer-down 핸들러에서 호출하세요.

---

## backend/ 역할

`backend/` 폴터는 백엔드 코드가 커질 때 사용하는 선택적 폴터입니다.

- `app.py` 또는 `app.js`에서 `backend/` 모듈을 import할 수 있습니다.
- 백엔드 라우터, 모델, 유틸리티 등을 분리 보관할 때 사용하세요.
- WAPL은 `backend/` 폴터를 특별히 해석하지 않으므로, 백엔드 진입점이 직접 필요한 파일을 로드해야 합니다.

---

## data, logs, temp 사용 규칙

### data/

- **영구 데이터**를 저장하는 공간입니다.
- WAPL은 `WEBAPP_DATA_DIR` 환경 변수를 제공합니다.
- AI는 반드시 `WEBAPP_DATA_DIR`를 사용하여 데이터를 저장하고, **앱 소스 폴터 내에 데이터를 쓰지 마세요**.

```python
import os
data_dir = os.environ["WEBAPP_DATA_DIR"]
os.makedirs(data_dir, exist_ok=True)
```

### logs/

- WAPL이 자동으로 생성하는 로그 폴터입니다.
- AI는 `WEBAPP_LOG_DIR` 환경 변수를 사용하여 로그를 작성할 수 있습니다.
- 콘솔에만 출력하지 말고, 파일 로그도 남기는 것을 권장합니다.

### temp/

- 임시 파일은 `tempfile` (Python) 또는 `os.tmpdir()` (Node.js)을 사용하세요.
- 종료 시 불필요한 임시 파일을 정리하세요.

---

## localStorage / IndexedDB 사용 금지

**절대 `localStorage`나 `IndexedDB`를 사용하지 마세요.**

- WebView2 세션은 종료 시 브라우저 프로필 데이터를 삭제합니다.
- 따라서 `localStorage`와 `IndexedDB`에 저장된 데이터는 **영구 보존이 불가능**합니다.
- 모든 영구 데이터는 **백엔드 API를 통해 `WEBAPP_DATA_DIR`에 파일로 저장**해야 합니다.

```javascript
// ❌ 금지
localStorage.setItem("key", value);

// ✅ 권장
await fetch("/api/save", {
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify({ key: "value" })
});
```

---

## `--host`, `--port` 지원 (필수)

WAPL은 백엔드 실행 시 `--host`와 `--port` 인자를 전달합니다.

- 백엔드는 이 인자를 **반드시 처리**해야 합니다.
- 기본값이 있더라도 WAPL이 전달한 값을 우선적으로 사용해야 합니다.
- WAPL이 할당한 포트에서 listen하지 않으면 앱이 실행되지 않습니다.

---

## WAPL 폴터 구조를 절대 변경하지 마세요

- `app.wapk`, `app.html`, `app.py`, `app.js`의 위치와 이름은 WAPL이 직접 참조합니다.
- 파일 이름을 임의로 바꾸거나 폴터를 이동하면 앱이 실행되지 않습니다.
- `app.wapk`에 명시된 경로만 사용하고, 추가 파일은 `assets/`, `backend/`, `data/` 등의 보조 폴터에 배치하세요.

---

## 요약 체크리스트

- [ ] `app.wapk`가 루트에 있고 형식이 올바른가?
- [ ] `app.html`이 메인 UI로 동작하는가?
- [ ] 백엔드가 있다면 `--host`와 `--port`를 지원하는가?
- [ ] 영구 데이터는 `WEBAPP_DATA_DIR`에 파일로 저장하는가?
- [ ] `localStorage` / `IndexedDB`를 사용하지 않는가?
- [ ] WAPL 표준 폴터 구조를 변경하지 않았는가?

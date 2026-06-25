const host = window.chrome?.webview;
const state = {
  apps: [],
  root: "",
  query: "",
  portRange: { first: 52000, last: 52999 },
  selected: null
};

const $ = (selector) => document.querySelector(selector);
const appList = $("#app-list");
const emptyState = $("#empty-state");
const busyLayer = $("#busy-layer");
const modalBackdrop = $("#modal-backdrop");

function icon(name) {
  return `<svg class="icon" aria-hidden="true"><use href="#i-${name}"></use></svg>`;
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

function post(message) {
  if (host) host.postMessage(message);
}

function appKey(app) {
  return `${encodeURIComponent(app.packageId)}::${encodeURIComponent(app.version)}`;
}

function findApp(key) {
  return state.apps.find((app) => appKey(app) === key);
}

function appInitial(name) {
  return [...String(name || "W").trim()][0]?.toUpperCase() || "W";
}

function render() {
  const query = state.query.trim().toLocaleLowerCase();
  const apps = state.apps.filter((app) =>
    [app.name, app.packageId, app.version, app.port, app.runtime]
      .some((value) => String(value).toLocaleLowerCase().includes(query))
  );

  $("#nav-count").textContent = state.apps.length;
  $("#port-usage").textContent = `${state.apps.length} / 1000`;
  $("#port-fill").style.width = `${Math.min(100, state.apps.length / 10)}%`;
  $("#port-first").textContent = state.portRange.first;
  $("#port-last").textContent = state.portRange.last;
  $("#root-path").textContent = state.root;

  appList.innerHTML = apps.map((app) => {
    const key = escapeHtml(appKey(app));
    const iconMarkup = app.icon?.startsWith("data:image/")
      ? `<img src="${escapeHtml(app.icon)}" alt="">`
      : escapeHtml(appInitial(app.name));
    return `
      <article class="app-row" data-key="${key}">
        <div class="app-identity">
          <div class="app-icon">${iconMarkup}</div>
          <div class="app-copy">
            <div class="app-name">${escapeHtml(app.name)} <span class="app-version">${escapeHtml(app.version)}</span></div>
            <div class="app-package">${escapeHtml(app.packageId)}</div>
          </div>
        </div>
        <div class="runtime"><b>${escapeHtml(app.mode === "server" ? "Backend" : "Static")}</b>${escapeHtml(app.runtime || "런타임 없음")}</div>
        <div class="port"><span>${escapeHtml(app.port)}</span></div>
        <div class="row-actions">
          <button class="secondary-button run-button" type="button" data-row-action="run">${icon("play")}실행</button>
          <button class="icon-button" type="button" title="데이터 폴더 열기" aria-label="데이터 폴더 열기" data-row-action="open-data">${icon("folder")}</button>
          <button class="icon-button" type="button" title="포트 변경" aria-label="포트 변경" data-row-action="port">${icon("port")}</button>
          <button class="icon-button delete" type="button" title="앱 삭제" aria-label="앱 삭제" data-row-action="remove">${icon("trash")}</button>
        </div>
      </article>`;
  }).join("");

  const noApps = state.apps.length === 0;
  emptyState.hidden = !noApps;
  appList.hidden = noApps || apps.length === 0;
  if (!noApps && apps.length === 0) {
    emptyState.hidden = false;
    emptyState.querySelector("h2").textContent = "검색 결과가 없습니다";
    emptyState.querySelector("p").textContent = "다른 이름, 패키지 또는 포트로 검색하십시오.";
    emptyState.querySelector("button").hidden = true;
  } else {
    emptyState.querySelector("h2").textContent = "설치된 앱이 없습니다";
    emptyState.querySelector("p").textContent = ".wapk 설치 레시피를 선택하면 전용 환경에 앱을 준비합니다.";
    emptyState.querySelector("button").hidden = false;
  }
}

function setBusy(active, message = "처리하는 중입니다.") {
  busyLayer.hidden = !active;
  $("#busy-text").textContent = message;
}

function setStatus(message) {
  $("#status-text").textContent = message;
}

function toast(message, tone = "success") {
  const element = document.createElement("div");
  element.className = `toast ${tone}`;
  element.textContent = message;
  $("#toast-region").append(element);
  setTimeout(() => element.remove(), 4200);
}

function openModal({ eyebrow = "CONFIRM", title, content, actions }) {
  $("#modal-eyebrow").textContent = eyebrow;
  $("#modal-title").textContent = title;
  $("#modal-content").innerHTML = content;
  $("#modal-actions").innerHTML = actions;
  modalBackdrop.hidden = false;
  modalBackdrop.querySelector("button, input")?.focus();
}

function closeModal() {
  modalBackdrop.hidden = true;
  state.selected = null;
}

function commandFor(app, type, extra = {}) {
  return { type, packageId: app.packageId, version: app.version, ...extra };
}

function showRemove(app) {
  state.selected = app;
  openModal({
    eyebrow: "REMOVE APP",
    title: `${app.name} 삭제`,
    content: `<p><strong>${escapeHtml(app.packageId)}/${escapeHtml(app.version)}</strong>의 소스, 데이터, 로그와 의존성을 모두 삭제합니다.</p><p class="warning">이 작업은 앱의 <code>data/</code> 폴더도 삭제하며 되돌릴 수 없습니다.</p>`,
    actions: `<button class="secondary-button" type="button" data-action="close-modal">취소</button><button class="primary-button danger-button" type="button" data-action="confirm-remove">${icon("trash")}삭제</button>`
  });
}

function showPort(app) {
  state.selected = app;
  openModal({
    eyebrow: "NETWORK",
    title: "영구 포트 변경",
    content: `<label>새 포트<input id="port-input" type="number" min="${state.portRange.first}" max="${state.portRange.last}" value="${app.port}"></label><p class="warning">포트를 바꾸면 origin이 달라져 기존 브라우저 저장소를 이어서 사용할 수 없습니다.</p>`,
    actions: `<button class="secondary-button" type="button" data-action="close-modal">취소</button><button class="primary-button" type="button" data-action="confirm-port">${icon("port")}변경</button>`
  });
  $("#port-input").select();
}

function showDoctor(items) {
  openModal({
    eyebrow: "RUNTIME STATUS",
    title: "환경 진단",
    content: `<div class="doctor-list">${items.map((item) => `<div class="doctor-item">${escapeHtml(item)}</div>`).join("")}</div>`,
    actions: `<button class="primary-button" type="button" data-action="close-modal">확인</button>`
  });
}

document.addEventListener("click", (event) => {
  const actionButton = event.target.closest("[data-action]");
  if (actionButton) {
    const action = actionButton.dataset.action;
    if (action === "install") post({ type: "install" });
    if (action === "refresh") post({ type: "refresh" });
    if (action === "doctor") post({ type: "doctor" });
    if (action === "open-root") post({ type: "openRoot" });
    if (action === "close-modal") closeModal();
    if (action === "confirm-remove" && state.selected) {
      post(commandFor(state.selected, "remove"));
      closeModal();
      setBusy(true, "앱을 삭제하는 중입니다.");
    }
    if (action === "confirm-port" && state.selected) {
      const port = Number($("#port-input").value);
      post(commandFor(state.selected, "reassignPort", { port }));
      closeModal();
      setBusy(true, "포트 설정을 변경하는 중입니다.");
    }
    return;
  }

  const rowButton = event.target.closest("[data-row-action]");
  if (!rowButton) return;
  const app = findApp(rowButton.closest(".app-row").dataset.key);
  if (!app) return;
  const action = rowButton.dataset.rowAction;
  if (action === "run") post(commandFor(app, "run"));
  if (action === "open-data") post(commandFor(app, "openData"));
  if (action === "port") showPort(app);
  if (action === "remove") showRemove(app);
});

$("#search-input").addEventListener("input", (event) => {
  state.query = event.target.value;
  render();
});

document.addEventListener("keydown", (event) => {
  if (event.key === "Escape" && !modalBackdrop.hidden) closeModal();
});

if (host) {
  host.addEventListener("message", ({ data }) => {
    if (data.type === "state") {
      state.apps = data.apps ?? [];
      state.root = data.root ?? "";
      state.portRange = data.portRange ?? state.portRange;
      setBusy(false);
      setStatus(`${state.apps.length}개의 앱이 준비되어 있습니다.`);
      render();
      if (data.notification) toast(data.notification);
    }
    if (data.type === "busy") setBusy(true, data.message);
    if (data.type === "idle") setBusy(false);
    if (data.type === "doctor") showDoctor(data.items ?? []);
    if (data.type === "toast") toast(data.message, data.tone);
    if (data.type === "error") {
      setBusy(false);
      setStatus(data.message);
      toast(data.message, "error");
    }
  });
  post({ type: "ready" });
} else {
  state.root = "C:\\Users\\user\\.webapp";
  state.apps = [
    { packageId: "hhsshoo12@webapp-test", name: "WebApp Test", version: "1.0", runtime: "python313", mode: "server", port: 52017 },
    { packageId: "studio@note-grid", name: "Note Grid", version: "2.4", runtime: "nodejs-lts-24", mode: "server", port: 52031 },
    { packageId: "local@status-board", name: "Status Board", version: "1.3", runtime: "", mode: "static", port: 52044 }
  ];
  setStatus("미리보기 모드");
  render();
}

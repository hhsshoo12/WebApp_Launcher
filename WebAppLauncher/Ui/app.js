const host = window.chrome?.webview;
const state = {
  apps: [],
  root: "",
  query: "",
  selected: null,
  settings: { developerMode: false, ports: null },
  licenses: { project: "", thirdParty: "" },
  activeLicense: "project"
};

const $ = (selector) => document.querySelector(selector);
const appList = $("#app-list");
const emptyState = $("#empty-state");
const busyLayer = $("#busy-layer");
const modalBackdrop = $("#modal-backdrop");
const settingsBackdrop = $("#settings-backdrop");

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
    emptyState.querySelector("p").textContent = "다른 이름, 패키지 또는 런타임으로 검색하십시오.";
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

function showDoctor(items) {
  openModal({
    eyebrow: "RUNTIME STATUS",
    title: "환경 진단",
    content: `<div class="doctor-list">${items.map((item) => `<div class="doctor-item">${escapeHtml(item)}</div>`).join("")}</div>`,
    actions: `<button class="primary-button" type="button" data-action="close-modal">확인</button>`
  });
}

function openSettings() {
  settingsBackdrop.hidden = false;
  settingsBackdrop.querySelector("button")?.focus();
  post({ type: "settings" });
  post({ type: "licenses" });
}

function closeSettings() {
  settingsBackdrop.hidden = true;
}

function selectSettingsTab(name) {
  document.querySelectorAll("[data-settings-tab]").forEach((button) => {
    button.classList.toggle("active", button.dataset.settingsTab === name);
  });
  document.querySelectorAll("[data-settings-panel]").forEach((panel) => {
    panel.classList.toggle("active", panel.dataset.settingsPanel === name);
  });
}

function renderSettings() {
  const ports = state.settings.ports ?? { occupied: 0, total: 1000, percent: 0, values: [] };
  $("#settings-port-count").textContent = ports.occupied;
  $("#settings-port-fill").style.width = `${Math.min(100, ports.percent)}%`;
  $("#settings-port-summary").textContent = ports.occupied === 0
    ? "현재 점유된 런처 포트가 없습니다."
    : `${ports.occupied}개 사용 중 · ${ports.total - ports.occupied}개 사용 가능`;
  $("#settings-port-list").innerHTML = ports.values.length
    ? ports.values.map((port) => `<code>${escapeHtml(port)}</code>`).join("")
    : '<span class="settings-empty-inline">점유 포트 없음</span>';
  $("#developer-mode-toggle").checked = Boolean(state.settings.developerMode);
}

function renderRuntimeResults(items) {
  const labels = {
    current: ["최신", "good"],
    newer: ["기준보다 새 버전", "good"],
    update: ["업데이트 필요", "warning"],
    missing: ["설치 안 됨", "muted"],
    error: ["확인 실패", "error"]
  };
  $("#runtime-results").innerHTML = items.map((item) => {
    const [label, tone] = labels[item.status] ?? labels.error;
    return `<div class="runtime-result">
      <div><strong>${escapeHtml(item.name)}</strong><small>${escapeHtml(item.id)}</small></div>
      <div class="runtime-version"><span>${escapeHtml(item.installedVersion || "—")}</span><small>기준 ${escapeHtml(item.expectedVersion)}</small></div>
      <b class="status-chip ${tone}">${label}</b>
    </div>`;
  }).join("");
}

function renderLicense() {
  $("#license-text").textContent =
    state.licenses[state.activeLicense] || "라이선스 문서를 찾을 수 없습니다.";
  document.querySelectorAll("[data-license-tab]").forEach((button) => {
    button.classList.toggle("active", button.dataset.licenseTab === state.activeLicense);
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
    if (action === "settings") openSettings();
    if (action === "close-modal") closeModal();
    if (action === "confirm-remove" && state.selected) {
      post(commandFor(state.selected, "remove"));
      closeModal();
      setBusy(true, "앱을 삭제하는 중입니다.");
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
  if (action === "remove") showRemove(app);
});

settingsBackdrop.addEventListener("click", (event) => {
  const closeButton = event.target.closest("[data-settings-close]");
  if (closeButton || event.target === settingsBackdrop) {
    closeSettings();
    return;
  }

  const tab = event.target.closest("[data-settings-tab]");
  if (tab) {
    selectSettingsTab(tab.dataset.settingsTab);
    return;
  }

  if (event.target.closest("[data-runtime-check]")) {
    $("#runtime-results").innerHTML = '<div class="settings-empty">버전을 확인하는 중입니다.</div>';
    post({ type: "checkRuntimeUpdates" });
    return;
  }

  const licenseTab = event.target.closest("[data-license-tab]");
  if (licenseTab) {
    state.activeLicense = licenseTab.dataset.licenseTab;
    renderLicense();
  }
});

$("#developer-mode-toggle").addEventListener("change", (event) => {
  post({ type: "setDeveloperMode", enabled: event.target.checked });
});

$("#search-input").addEventListener("input", (event) => {
  state.query = event.target.value;
  render();
});

document.addEventListener("keydown", (event) => {
  if (event.key === "Escape" && !settingsBackdrop.hidden) closeSettings();
  else if (event.key === "Escape" && !modalBackdrop.hidden) closeModal();
});

if (host) {
  host.addEventListener("message", ({ data }) => {
    if (data.type === "state") {
      state.apps = data.apps ?? [];
      state.root = data.root ?? "";
      setBusy(false);
      setStatus(`${state.apps.length}개의 앱이 준비되어 있습니다.`);
      render();
      if (data.notification) toast(data.notification);
    }
    if (data.type === "busy") setBusy(true, data.message);
    if (data.type === "idle") setBusy(false);
    if (data.type === "doctor") showDoctor(data.items ?? []);
    if (data.type === "settings") {
      state.settings = data;
      renderSettings();
    }
    if (data.type === "runtimeCheck" && data.status === "complete") {
      renderRuntimeResults(data.items ?? []);
    }
    if (data.type === "licenses") {
      state.licenses = {
        project: data.project ?? "",
        thirdParty: data.thirdParty ?? ""
      };
      renderLicense();
    }
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
    { packageId: "hhsshoo12@webapp-test", name: "WebApp Test", version: "1.0", runtime: "python313", mode: "server", port: "자동" },
    { packageId: "studio@note-grid", name: "Note Grid", version: "2.4", runtime: "nodejs-lts-24", mode: "server", port: "자동" },
    { packageId: "local@status-board", name: "Status Board", version: "1.3", runtime: "", mode: "static", port: "없음" }
  ];
  setStatus("미리보기 모드");
  render();
}

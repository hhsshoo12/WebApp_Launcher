const host = window.chrome?.webview;
const state = {
  view: "apps",
  apps: [],
  root: "",
  query: "",
  selected: null,
  settings: { developerMode: false },
  doctorItems: null,
  licenses: { project: "", thirdParty: "" },
  activeLicense: "project",
  processes: [],
  processPorts: { occupied: 0, total: 1000, percent: 0, values: [] }
};

const $ = (selector) => document.querySelector(selector);
const appList = $("#app-list");
const emptyState = $("#empty-state");
const busyLayer = $("#busy-layer");
const modalBackdrop = $("#modal-backdrop");
const settingsBackdrop = $("#settings-backdrop");
const appLibraryView = $("#app-library-view");
const processManagerView = $("#process-manager-view");

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

function setView(name) {
  state.view = name;
  appLibraryView.hidden = name !== "apps";
  processManagerView.hidden = name !== "processes";
  $(".nav-item")?.classList.toggle("active", name === "apps");
  document.querySelectorAll("[data-action='process-manager']").forEach((button) => {
    button.classList.toggle("active", name === "processes");
  });
  if (name === "processes") {
    post({ type: "processManager" });
  }
  setStatus(name === "apps"
    ? `${state.apps.length}개의 앱이 준비되어 있습니다.`
    : "프로세스 관리자");
}

function render() {
  if (state.view === "apps") {
    renderAppLibrary();
  } else {
    renderProcessManager();
  }
}

function renderAppLibrary() {
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
          <button class="icon-button" type="button" title="데이터 폴 열기" aria-label="데이터 폴 열기" data-row-action="open-data">${icon("folder")}</button>
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

function renderProcessManager() {
  const ports = state.processPorts ?? { occupied: 0, total: 1000, percent: 0, values: [] };
  $("#pm-port-count").textContent = ports.occupied;
  $("#pm-port-fill").style.width = `${Math.min(100, ports.percent)}%`;
  $("#pm-port-summary").textContent = ports.occupied === 0
    ? "현재 점유된 런처 포트가 없습니다."
    : `${ports.occupied}개 사용 중 · ${ports.total - ports.occupied}개 사용 가능`;
  $("#pm-port-list").innerHTML = ports.values.length
    ? ports.values.map((port) => `<code>${escapeHtml(port)}</code>`).join("")
    : '<span class="settings-empty-inline">점유 포트 없음</span>';

  const processList = $("#process-list");
  const processEmpty = $("#process-empty");
  const processes = state.processes ?? [];

  processList.innerHTML = processes.map((proc) => {
    const key = escapeHtml(appKey(proc));
    return `
      <article class="app-row process-row" data-key="${key}">
        <div class="app-identity">
          <div class="app-icon">${escapeHtml(appInitial(proc.name))}</div>
          <div class="app-copy">
            <div class="app-name">${escapeHtml(proc.name)} <span class="app-version">${escapeHtml(proc.version)}</span></div>
            <div class="app-package">${escapeHtml(proc.packageId)}</div>
          </div>
        </div>
        <div class="runtime"><b>${escapeHtml(proc.mode === "server" ? "Backend" : "Static")}</b>${escapeHtml(proc.runtime || "런타임 없음")}</div>
        <div class="port"><span>${escapeHtml(proc.port ?? "—")}</span></div>
        <div class="pid"><code>${escapeHtml(proc.processId ?? "—")}</code><small>${escapeHtml(proc.processName ?? "")}</small></div>
        <div class="row-actions">
          <button class="icon-button" type="button" title="로그 폴 열기" aria-label="로그 폴 열기" data-process-action="open-log" data-key="${key}">${icon("folder")}</button>
          <button class="icon-button delete" type="button" title="프로세스 종료" aria-label="프로세스 종료" data-process-action="kill" data-key="${key}">${icon("x")}</button>
        </div>
      </article>`;
  }).join("");

  processEmpty.hidden = processes.length > 0;
  processList.hidden = processes.length === 0;
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
    content: `<p><strong>${escapeHtml(app.packageId)}/${escapeHtml(app.version)}</strong>의 소스, 데이터, 로그와 의존성을 모두 삭제합니다.</p><p class="warning">이 작업은 앱의 <code>data/</code> 폴어도 삭제하며 되돌릴 수 없습니다.</p>`,
    actions: `<button class="secondary-button" type="button" data-action="close-modal">취소</button><button class="primary-button danger-button" type="button" data-action="confirm-remove">${icon("trash")}삭제</button>`
  });
}

function showKillProcess(proc) {
  state.selected = proc;
  openModal({
    eyebrow: "KILL PROCESS",
    title: `${proc.name} 프로세스 종료`,
    content: `<p><strong>${escapeHtml(proc.packageId)}/${escapeHtml(proc.version)}</strong>의 백엔드 프로세스(PID ${escapeHtml(proc.processId)})를 종료합니다.</p><p class="warning">프로세스를 종료하면 실행 중인 앱 창도 함께 닫힙니다.</p>`,
    actions: `<button class="secondary-button" type="button" data-action="close-modal">취소</button><button class="primary-button danger-button" type="button" data-action="confirm-kill">${icon("trash")}종료</button>`
  });
}

function openSettings() {
  settingsBackdrop.hidden = false;
  settingsBackdrop.querySelector("button")?.focus();
  post({ type: "settings" });
  post({ type: "licenses" });
  post({ type: "doctor" });
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
  $("#developer-mode-toggle").checked = Boolean(state.settings.developerMode);
}

function renderDoctor() {
  const container = $("#doctor-results");
  const items = state.doctorItems;
  if (!items) {
    container.innerHTML = '<div class="settings-empty">아직 진단을 실행하지 않았습니다.</div>';
    return;
  }
  container.innerHTML = `<div class="doctor-list">${items.map((item) => `<div class="doctor-item">${escapeHtml(item)}</div>`).join("")}</div>`;
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
    if (action === "refresh") {
      post({ type: "refresh" });
      setBusy(true, "앱 목록을 새로 고치는 중입니다.");
    }
    if (action === "process-manager") {
      setView(state.view === "processes" ? "apps" : "processes");
      return;
    }
    if (action === "refresh-processes") post({ type: "processManager" });
    if (action === "open-root") post({ type: "openRoot" });
    if (action === "settings") openSettings();
    if (action === "close-modal") closeModal();
    if (action === "confirm-remove" && state.selected) {
      post(commandFor(state.selected, "remove"));
      closeModal();
      setBusy(true, "앱을 삭제하는 중입니다.");
    }
    if (action === "confirm-kill" && state.selected) {
      post(commandFor(state.selected, "killProcess"));
      closeModal();
    }
    return;
  }

  const navButton = event.target.closest("[data-view]");
  if (navButton) {
    setView(navButton.dataset.view);
    return;
  }

  const rowButton = event.target.closest("[data-row-action]");
  if (rowButton) {
    const app = findApp(rowButton.closest(".app-row").dataset.key);
    if (!app) return;
    const action = rowButton.dataset.rowAction;
    if (action === "run") post(commandFor(app, "run"));
    if (action === "open-data") post(commandFor(app, "openData"));
    if (action === "remove") showRemove(app);
    return;
  }

  const processButton = event.target.closest("[data-process-action]");
  if (processButton) {
    const key = processButton.dataset.key;
    const proc = state.processes.find((p) => appKey(p) === key);
    if (!proc) return;
    const action = processButton.dataset.processAction;
    if (action === "open-log") {
      post({ type: "openLog", packageId: proc.packageId, version: proc.version });
    }
    if (action === "kill") showKillProcess(proc);
  }
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

  if (event.target.closest("[data-doctor-check]")) {
    $("#doctor-results").innerHTML = '<div class="settings-empty">진단을 실행하는 중입니다.</div>';
    post({ type: "doctor" });
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
    if (data.type === "processManager") {
      state.processPorts = data.ports ?? state.processPorts;
      state.processes = data.processes ?? [];
      if (state.view === "processes") render();
    }
    if (data.type === "doctor") {
      state.doctorItems = data.items ?? [];
      renderDoctor();
    }
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
  state.processes = [
    { packageId: "hhsshoo12@webapp-test", name: "WebApp Test", version: "1.0", runtime: "python313", mode: "server", port: 52001, processId: 1234, processName: "python.exe", logPath: "C:\\Users\\user\\.webapp\\app\\...\\logs\\...log" },
    { packageId: "studio@note-grid", name: "Note Grid", version: "2.4", runtime: "nodejs-lts-24", mode: "server", port: 52002, processId: 5678, processName: "node.exe", logPath: "C:\\Users\\user\\.webapp\\app\\...\\logs\\...log" }
  ];
  state.processPorts = { occupied: 2, total: 1000, percent: 0.2, values: [52001, 52002] };
  setStatus("미리보기 모드");
  render();
}

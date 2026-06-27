from __future__ import annotations

import locale

try:
    system_lang = locale.getdefaultlocale()[0]
except Exception:
    system_lang = "en_US"

is_korean = system_lang and system_lang.lower().startswith("ko")

class LocaleStrings:
    @staticmethod
    def get(key: str, *args) -> str:
        ko_dict = {
            "invalid_install_dir": "error: {} 은(는) 설치된 WebApp Launcher 폴더가 아닙니다.",
            "invalid_state_format": "error: 설치 상태 파일이 현재 형식이 아닙니다.",
            "installed_version": "설치된 런처 버전: v{}",
            "checking_new_version": "GitHub Release에서 새 버전을 확인하는 중...",
            "latest_version": "최신 런처 버전: v{}",
            "already_latest": "v{}은(는) 이미 최신 버전입니다.",
            "cleaning_runtime": "런처 실행을 정리하는 중...",
            "process_not_terminated": "실행 중인 WebAppLauncher.exe가 종료되지 않았습니다.",
            "downloading_payload": "런처 페이로드 다운로드 중 {}%",
            "replacing_files": "런처 파일을 교체하는 중...",
            "archiving_setup": "Setup.exe를 보관하는 중...",
            "setup_path_log": "업데이트용 Setup.exe: {}",
            "update_complete_msg": "업데이트가 끝났습니다. 런처를 다시 시작합니다.",
            "update_complete_detail": "v{} 설치 완료",
            "update_failed": "업데이트에 실패했습니다.",
        }
        en_dict = {
            "invalid_install_dir": "error: {} is not a valid installed WebApp Launcher folder.",
            "invalid_state_format": "error: The installation state file format is invalid.",
            "installed_version": "Installed launcher version: v{}",
            "checking_new_version": "Checking for new version on GitHub Release...",
            "latest_version": "Latest launcher version: v{}",
            "already_latest": "v{} is already the latest version.",
            "cleaning_runtime": "Cleaning up launcher runtime...",
            "process_not_terminated": "The running WebAppLauncher.exe did not terminate.",
            "downloading_payload": "Downloading launcher payload {}%",
            "replacing_files": "Replacing launcher files...",
            "archiving_setup": "Archiving Setup.exe...",
            "setup_path_log": "Setup.exe path for updates: {}",
            "update_complete_msg": "Update completed. Restarting the launcher.",
            "update_complete_detail": "v{} installation complete",
            "update_failed": "Update failed.",
        }
        fallback = ko_dict if is_korean else en_dict
        template = fallback.get(key, key)
        return template.format(*args)

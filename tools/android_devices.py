"""ADB の場所と、接続中 Android の物理端末一覧を共有する。"""
from __future__ import annotations

import glob
import os
import shutil


class AdbNotFoundError(RuntimeError):
    pass


def _candidate_paths() -> list[str]:
    candidates = []
    configured = os.environ.get("ADB", "").strip().strip('"')
    if configured:
        candidates.append(configured)
    found = shutil.which("adb")
    if found:
        candidates.append(found)
    for name in ("ANDROID_HOME", "ANDROID_SDK_ROOT"):
        root = os.environ.get(name, "").strip().strip('"')
        if root:
            candidates.append(os.path.join(root, "platform-tools", "adb.exe"))

    program_files = [os.environ.get("ProgramFiles", ""),
                     os.environ.get("ProgramFiles(x86)", "")]
    local = os.environ.get("LOCALAPPDATA", "")
    patterns = []
    for base in filter(None, program_files):
        patterns.append(os.path.join(base, "Unity", "Hub", "Editor", "2022.3.*",
                                     "Editor", "Data", "PlaybackEngines", "AndroidPlayer",
                                     "SDK", "platform-tools", "adb.exe"))
        patterns.append(os.path.join(base, "SideQuest", "resources", "app.asar.unpacked",
                                     "build", "platform-tools", "adb.exe"))
    if local:
        patterns.extend([
            os.path.join(local, "Android", "Sdk", "platform-tools", "adb.exe"),
            os.path.join(local, "Programs", "SideQuest", "resources", "app.asar.unpacked",
                         "build", "platform-tools", "adb.exe"),
        ])
    for pattern in patterns:
        candidates.extend(sorted(glob.glob(pattern), reverse=True))
    return candidates


def resolve_adb() -> str:
    """優先順に adb を探す。見つからない場合は端末障害ではなく前提不足として返す。"""
    for path in _candidate_paths():
        if path and os.path.isfile(path):
            return os.path.abspath(path)
    raise AdbNotFoundError(
        "adb が見つかりません。環境変数 ADB に adb.exe のパスを設定するか、"
        "Android SDK platform-tools を導入してください")


def adb_command(adb_path: str, serial: str | None, *args) -> list[str]:
    cmd = [adb_path]
    if serial:
        cmd += ["-s", serial]
    cmd += [str(a) for a in args]
    return cmd


def list_android_devices(run, adb_path: str, known_quest_serials=()) -> list[dict]:
    """物理 ro.serialno で畳んだ一覧。USB を同一端末の代表接続に選ぶ。"""
    rc, out, err = run(adb_command(adb_path, None, "devices", "-l"), timeout=25)
    if rc != 0:
        raise RuntimeError((err or out or "adb devices に失敗しました").strip())
    raw = []
    for line in out.splitlines()[1:]:
        parts = line.strip().split()
        if len(parts) < 2 or parts[0].startswith("*"):
            continue
        transport, state = parts[0], parts[1]
        fields = {}
        for token in parts[2:]:
            if ":" in token:
                key, value = token.split(":", 1)
                fields[key] = value
        physical = ""
        if state == "device":
            _, serial_out, _ = run(
                adb_command(adb_path, transport, "shell", "getprop", "ro.serialno"),
                timeout=20)
            physical = (serial_out.strip().splitlines() or [""])[0].strip()
        physical = physical or (transport if ":" not in transport else "")
        model = fields.get("model", "")
        is_quest = (physical in set(known_quest_serials) or
                    model.lower().startswith("quest"))
        raw.append({"serial": transport, "physicalSerial": physical,
                    "state": state, "model": model, "isQuest": is_quest,
                    "usb": ":" not in transport})

    result = []
    by_physical = {}
    for item in raw:
        key = item["physicalSerial"] or ("transport:" + item["serial"])
        previous = by_physical.get(key)
        rank = (item["state"] == "device", item["usb"])
        previous_rank = ((previous["state"] == "device", previous["usb"])
                         if previous is not None else (False, False))
        if previous is None or rank > previous_rank:
            by_physical[key] = item
    result.extend(by_physical.values())
    return sorted(result, key=lambda d: (not d["usb"], d["serial"]))

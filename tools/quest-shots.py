"""体験中の撮影（左グリップ）を、Quest から取り出して管理する。

    py -3.11 tools/quest-shots.py status [--serial S]        # 端末の状態（ON/OFF と撮影セットの数）
    py -3.11 tools/quest-shots.py pull   [--serial S]        # 撮った画像を logs/shots/<serial>/ へ
    py -3.11 tools/quest-shots.py pull --delete              # 取り出して端末から消す
    py -3.11 tools/quest-shots.py off    [--serial S]        # 撮影を止める（展示本番の前に必ず）
    py -3.11 tools/quest-shots.py on     [--serial S]        # 撮影を許す（Release ビルドで使うとき）
    py -3.11 tools/quest-shots.py clear  [--serial S]        # 端末の撮影を全部消す

1 回の撮影 = 1 フォルダ（`20260930_142315_001/`）:

    1_screen.png     表示されているスクリーン映像（最終合成）
    2_raw.png        加工前の生映像
    3_layers.png     合成しているものだけ（合成が無い瞬間は無い）
    3_cg_alpha.png   その CG 層そのもの（透過。CG が出ている瞬間だけ）
    4_hmd.png        体験者の視界（アプリの描画のみ。パススルーは含まない）
    info.json        どの瞬間か（相・周回・カメラ・演出）と、撮れなかったものの理由

⚠ **`adb pull` は使えない。** /sdcard/Android/data/<pkg>/ は scoped storage で、pull は黙って
  0 バイトを作る（`quest-fleet.py` の `cat_file` と同じ理由）。`exec-out cat` で 1 ファイルずつ取る。
  PNG はバイナリなので、テキスト前提の `cat_file` は使い回せない。

⚠ **左グリップは体験者が握り込む。展示本番の機には `off` を打つ。** ON / OFF の印は端末の
  `shots/ON` `shots/OFF`。アプリが起動時に 1 度だけ読むので、**反映はアプリの再起動後**。
"""

import argparse
import os
import re
import subprocess
import sys

PKG = "com.roiril.mawarimi"
SHOTS_DIR = f"/sdcard/Android/data/{PKG}/files/shots"
DEFAULT_OUT = os.path.join("logs", "shots")

# 撮影セットのフォルダ名（ExperienceShotLogic.SetFolderName と対）。
SET_RE = re.compile(r"^\d{8}_\d{6}_\d{3}$")


# ---------------------------------------------------------------- adb

def _run(serial, *args, timeout=60, binary=False):
    cmd = ["adb"]
    if serial:
        cmd += ["-s", serial]
    cmd += [str(a) for a in args]
    try:
        if binary:
            p = subprocess.run(cmd, capture_output=True, timeout=timeout)
            return p.returncode, p.stdout, p.stderr.decode("utf-8", "replace")
        p = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8",
                           errors="replace", timeout=timeout)
        return p.returncode, p.stdout.replace("\r", ""), p.stderr.replace("\r", "")
    except subprocess.TimeoutExpired:
        return 124, b"" if binary else "", "timeout"
    except FileNotFoundError:
        print("adb が PATH に無い", file=sys.stderr)
        sys.exit(2)


def parse_devices(text):
    """`adb devices -l` の出力を [{serial,state,model}] にする。Quest 以外の機は外す。"""
    res = []
    for line in text.splitlines()[1:]:
        parts = line.strip().split()
        if len(parts) < 2 or line.startswith("*"):
            continue
        model = ""
        for tok in parts[2:]:
            if tok.startswith("model:"):
                model = tok[6:]
        # model が読めて Quest でないものだけ外す（unauthorized の Quest は model を出さない）。
        if model and not model.lower().startswith("quest"):
            continue
        res.append({"serial": parts[0], "state": parts[1], "model": model})
    return res


def pick_serial(serial, devices):
    """対象機を決める。複数のとき黙って選ばない（別の機の写真を「これだ」と読ませない）。"""
    if serial:
        return serial, None
    ready = [d for d in devices if d["state"] == "device"]
    if len(ready) == 1:
        return ready[0]["serial"], None
    if not ready:
        return None, "Quest が繋がっていない（adb devices を確認）"
    names = ", ".join(f'{d["serial"]}({d["model"] or "?"})' for d in ready)
    return None, f"Quest が {len(ready)} 台ある。--serial で選ぶ: {names}"


def resolve_serial(args):
    _, out, _ = _run(None, "devices", "-l")
    serial, err = pick_serial(args.serial, parse_devices(out))
    if err:
        print(err, file=sys.stderr)
        sys.exit(1)
    return serial


def parse_sets(ls_output):
    """`ls -1` の出力から撮影セットのフォルダ名だけを、古い順に返す。"""
    return sorted(n.strip() for n in ls_output.splitlines() if SET_RE.match(n.strip()))


def list_sets(serial):
    code, out, _ = _run(serial, "shell", f"ls -1 {SHOTS_DIR} 2>/dev/null")
    return parse_sets(out) if code == 0 else []


def list_files(serial, set_name):
    _, out, _ = _run(serial, "shell", f"ls -1 {SHOTS_DIR}/{set_name} 2>/dev/null")
    return [n.strip() for n in out.splitlines() if n.strip()]


def marker_state(serial):
    _, out, _ = _run(serial, "shell", f"ls -1 {SHOTS_DIR} 2>/dev/null")
    names = {n.strip() for n in out.splitlines()}
    return "ON" in names, "OFF" in names


def cat_bytes(serial, remote):
    """端末のファイルをバイト列で取る。失敗は None（0 バイトは失敗として扱う）。"""
    code, out, _ = _run(serial, "exec-out", f"cat {remote}", timeout=120, binary=True)
    if code != 0 or not out:
        return None
    return out


# ---------------------------------------------------------------- コマンド

def cmd_status(args):
    serial = resolve_serial(args)
    on, off = marker_state(serial)
    sets = list_sets(serial)
    if off:
        state = "OFF（印あり。撮影しない）"
    elif on:
        state = "ON（印あり。Release でも撮る）"
    else:
        state = "既定（Development ビルドは撮る / Release は撮らない）"
    print(f"{serial}: 撮影 {state}")
    print(f"  端末内の撮影セット {len(sets)} 件" + (f"（最新 {sets[-1]}）" if sets else ""))
    print("  ⚠ 印はアプリの起動時に 1 度だけ読まれる。変えたらアプリを再起動する")
    return 0


def _set_marker(serial, name, present):
    if present:
        _run(serial, "shell", f"mkdir -p {SHOTS_DIR} && touch {SHOTS_DIR}/{name}")
    else:
        _run(serial, "shell", f"rm -f {SHOTS_DIR}/{name}")


def cmd_off(args):
    serial = resolve_serial(args)
    _set_marker(serial, "OFF", True)
    _set_marker(serial, "ON", False)
    on, off = marker_state(serial)
    print(f"{serial}: 撮影を OFF にした（確認: OFF={off}）。アプリを再起動すると反映される")
    return 0 if off else 1


def cmd_on(args):
    serial = resolve_serial(args)
    _set_marker(serial, "ON", True)
    _set_marker(serial, "OFF", False)
    on, off = marker_state(serial)
    print(f"{serial}: 撮影を ON にした（確認: ON={on}）。アプリを再起動すると反映される")
    return 0 if on else 1


def cmd_pull(args):
    serial = resolve_serial(args)
    outdir = args.out or os.path.join(DEFAULT_OUT, serial)
    sets = list_sets(serial)
    if not sets:
        print(f"{serial}: 端末に撮影が無い（{SHOTS_DIR}）")
        return 0

    complete = []
    for name in sets:
        files = list_files(serial, name)
        if not files:
            print(f"  {name}: 空のフォルダ（書き出し中かもしれない）— 飛ばす")
            continue
        local_dir = os.path.join(outdir, name)
        got, failed = [], []
        for f in files:
            data = cat_bytes(serial, f"{SHOTS_DIR}/{name}/{f}")
            if data is None:
                failed.append(f)
                continue
            os.makedirs(local_dir, exist_ok=True)
            with open(os.path.join(local_dir, f), "wb") as fh:
                fh.write(data)
            got.append(f)
        mark = "OK" if not failed else "一部失敗: " + ", ".join(failed)
        print(f"  {name}: {len(got)}/{len(files)} 枚  {mark}")
        if not failed:
            complete.append(name)

    print(f"取り出し先: {os.path.abspath(outdir)}")
    if args.delete:
        for name in complete:
            _run(serial, "shell", f"rm -rf {SHOTS_DIR}/{name}")
        print(f"端末から {len(complete)} 件を消した（失敗があったセットは残してある）")
    return 0


def cmd_clear(args):
    serial = resolve_serial(args)
    # 印（ON/OFF）は残す。消すのは撮影セットだけ。
    for name in list_sets(serial):
        _run(serial, "shell", f"rm -rf {SHOTS_DIR}/{name}")
    print(f"{serial}: 撮影セットを全部消した（残り {len(list_sets(serial))} 件）")
    return 0


def main(argv=None):
    # cp932 の端末で「⚠」等を print すると UnicodeEncodeError で落ちる（windows-env.md §2）。
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    for name, fn, helptext in (
        ("status", cmd_status, "端末の撮影の状態"),
        ("pull", cmd_pull, "撮った画像を PC へ取り出す"),
        ("off", cmd_off, "撮影を止める（展示本番の前に）"),
        ("on", cmd_on, "撮影を許す（Release ビルド用）"),
        ("clear", cmd_clear, "端末の撮影セットを全部消す"),
    ):
        p = sub.add_parser(name, help=helptext)
        p.add_argument("--serial")
        if name == "pull":
            p.add_argument("--out", help=f"取り出し先（既定 {DEFAULT_OUT}/<serial>）")
            p.add_argument("--delete", action="store_true",
                           help="取り出せたセットを端末から消す")
        p.set_defaults(fn=fn)
    args = ap.parse_args(argv)
    return args.fn(args)


if __name__ == "__main__":
    sys.exit(main())

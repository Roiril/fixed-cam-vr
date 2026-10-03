#!/usr/bin/env python3
import argparse
import copy
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import struct
import sys
import zipfile


TABLET_DIR = Path(__file__).resolve().parent
REPO_DIR = TABLET_DIR.parent
MAIN_DIR = TABLET_DIR / "app" / "src" / "main"
JAVA_DIR = MAIN_DIR / "java"
RES_DIR = MAIN_DIR / "res"
ASSET_DIR = MAIN_DIR / "assets"
BUILD_DIR = TABLET_DIR / ".build"
OUTPUT_DIR = REPO_DIR / "Builds"
UNITY_ANDROID = Path(r"C:\Program Files\Unity\Hub\Editor\2022.3.62f2\Editor\Data\PlaybackEngines\AndroidPlayer")
JDK_BIN = UNITY_ANDROID / "OpenJDK" / "bin"
SDK_DIR = UNITY_ANDROID / "SDK"
BUILD_TOOLS = SDK_DIR / "build-tools" / "34.0.0"
ANDROID_JAR = SDK_DIR / "platforms" / "android-34" / "android.jar"


def executable(directory: Path, name: str) -> Path:
    for suffix in (".exe", ".bat", ".cmd", ""):
        candidate = directory / f"{name}{suffix}"
        if candidate.is_file():
            return candidate
    raise FileNotFoundError(f"{name} was not found under {directory}")


def run(command, *, capture=False, cwd=None):
    printable = subprocess.list2cmdline([str(part) for part in command])
    print(f"> {printable}")
    environment = os.environ.copy()
    environment["JAVA_HOME"] = str(UNITY_ANDROID / "OpenJDK")
    environment["PATH"] = str(JDK_BIN) + os.pathsep + environment.get("PATH", "")
    return subprocess.run(
        [str(part) for part in command], check=True, text=True,
        stdout=subprocess.PIPE if capture else None,
        stderr=subprocess.STDOUT if capture else None,
        encoding="utf-8", errors="replace",
        env=environment,
        cwd=str(cwd) if cwd else None,
    )


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def repack_apk(source: Path, destination: Path, classes_dex: Path):
    seen = set()
    with zipfile.ZipFile(source, "r") as input_apk, zipfile.ZipFile(destination, "w", allowZip64=True) as output_apk:
        output_apk.comment = input_apk.comment
        for info in input_apk.infolist():
            normalized = info.orig_filename.replace("\\", "/")
            if normalized in seen:
                raise RuntimeError(f"Duplicate APK entry after path normalization: {normalized}")
            seen.add(normalized)
            cloned = copy.copy(info)
            cloned.filename = normalized
            cloned.orig_filename = normalized
            data = input_apk.read(info)
            output_apk.writestr(cloned, data, compress_type=info.compress_type)
        if "classes.dex" in seen:
            raise RuntimeError("aapt2 output unexpectedly contains classes.dex")
        dex_info = zipfile.ZipInfo("classes.dex")
        dex_info.compress_type = zipfile.ZIP_DEFLATED
        dex_info.external_attr = 0o100644 << 16
        output_apk.writestr(dex_info, classes_dex.read_bytes(), compress_type=zipfile.ZIP_DEFLATED)
    verify_raw_zip_names(destination)


def raw_zip_names(path: Path):
    data = path.read_bytes()
    eocd_offset = data.rfind(b"PK\x05\x06")
    if eocd_offset < 0 or eocd_offset + 22 > len(data):
        raise RuntimeError(f"ZIP end record is missing: {path}")
    _, _, _, _, total_entries, _, central_offset, _ = struct.unpack_from("<4s4H2LH", data, eocd_offset)
    names = []
    offset = central_offset
    for _ in range(total_entries):
        if data[offset:offset + 4] != b"PK\x01\x02":
            raise RuntimeError(f"Invalid central directory header at {offset}: {path}")
        name_length = int.from_bytes(data[offset + 28:offset + 30], "little")
        extra_length = int.from_bytes(data[offset + 30:offset + 32], "little")
        comment_length = int.from_bytes(data[offset + 32:offset + 34], "little")
        local_offset = int.from_bytes(data[offset + 42:offset + 46], "little")
        central_name = data[offset + 46:offset + 46 + name_length]
        if data[local_offset:local_offset + 4] != b"PK\x03\x04":
            raise RuntimeError(f"Invalid local header at {local_offset}: {path}")
        local_name_length = int.from_bytes(data[local_offset + 26:local_offset + 28], "little")
        local_name = data[local_offset + 30:local_offset + 30 + local_name_length]
        if local_name != central_name:
            raise RuntimeError(f"Local and central ZIP names differ: {local_name!r} != {central_name!r}")
        if b"\\" in local_name or b"\\" in central_name:
            raise RuntimeError(f"Backslash remains in raw ZIP name: {local_name!r}")
        names.append(central_name)
        offset += 46 + name_length + extra_length + comment_length
    return names


def verify_raw_zip_names(path: Path):
    names = raw_zip_names(path)
    if not names:
        raise RuntimeError(f"APK has no ZIP entries: {path}")
    return len(names)


def run_native_tests(javac: Path, java: Path):
    test_classes = BUILD_DIR / "test-classes"
    test_classes.mkdir(parents=True)
    sources = [
        JAVA_DIR / "com" / "roiril" / "mawarimi" / "tablet" / "ByteRange.java",
        JAVA_DIR / "com" / "roiril" / "mawarimi" / "tablet" / "RequestPolicy.java",
        TABLET_DIR / "tests" / "NativePolicyTest.java",
    ]
    run([javac, "-encoding", "UTF-8", "-source", "8", "-target", "8", "-d", test_classes, *sources])
    run([java, "-cp", test_classes, "com.roiril.mawarimi.tablet.NativePolicyTest"])


def create_debug_keystore(keytool: Path, keystore: Path):
    if keystore.is_file():
        return
    keystore.parent.mkdir(parents=True, exist_ok=True)
    run([
        keytool, "-genkeypair", "-keystore", keystore, "-storepass", "android",
        "-alias", "androiddebugkey", "-keypass", "android", "-keyalg", "RSA",
        "-keysize", "2048", "-validity", "10000", "-dname", "CN=Android Debug,O=Android,C=US",
    ])


def verify_assets(apk: Path):
    records = []
    with zipfile.ZipFile(apk, "r") as archive:
        names = set(archive.namelist())
        if "resources.arsc" not in names or archive.getinfo("resources.arsc").compress_type != zipfile.ZIP_STORED:
            raise RuntimeError("resources.arsc must be present and stored without compression")
        for source in sorted(path for path in ASSET_DIR.rglob("*") if path.is_file()):
            relative = source.relative_to(ASSET_DIR).as_posix()
            archive_name = f"assets/{relative}"
            if archive_name not in names:
                raise RuntimeError(f"APK is missing {archive_name}")
            info = archive.getinfo(archive_name)
            source_hash = sha256_file(source)
            apk_hash = hashlib.sha256(archive.read(archive_name)).hexdigest()
            if source_hash != apk_hash:
                raise RuntimeError(f"APK asset differs: {archive_name}")
            suffix = source.suffix.lower()
            if suffix in (".mp3", ".mp4") and info.compress_type != zipfile.ZIP_STORED:
                raise RuntimeError(f"media asset is compressed: {archive_name}")
            records.append({
                "path": archive_name,
                "bytes": source.stat().st_size,
                "sha256": source_hash,
                "zipStored": info.compress_type == zipfile.ZIP_STORED,
            })
    return records


def reset_build_directory():
    resolved_build = BUILD_DIR.resolve()
    expected_build = (TABLET_DIR / ".build").resolve()
    if resolved_build != expected_build or resolved_build.parent != TABLET_DIR.resolve():
        raise RuntimeError(f"Refusing to delete unexpected build directory: {resolved_build}")
    if BUILD_DIR.exists():
        shutil.rmtree(BUILD_DIR)


def build_instrumentation(aapt2: Path, d8: Path, zipalign: Path, apksigner: Path,
                          javac: Path, keystore: Path):
    test_dir = BUILD_DIR / "instrumentation"
    classes_dir = test_dir / "classes"
    dex_dir = test_dir / "dex"
    test_dir.mkdir()
    classes_dir.mkdir()
    dex_dir.mkdir()
    manifest = TABLET_DIR / "instrumentation" / "AndroidManifest.xml"
    sources = sorted((TABLET_DIR / "instrumentation" / "java").rglob("*.java"))
    if not manifest.is_file() or not sources:
        raise FileNotFoundError("Instrumentation manifest or Java source is missing")
    unsigned = test_dir / "unsigned.apk"
    assembled = test_dir / "assembled.apk"
    aligned = test_dir / "aligned.apk"
    output = OUTPUT_DIR / "doctor-tablet-test.apk"
    run([
        aapt2, "link", "-o", unsigned, "--manifest", manifest, "-I", ANDROID_JAR,
        "--min-sdk-version", "26", "--target-sdk-version", "34", "--version-code", "1",
        "--version-name", "1.0", "--debug-mode",
    ])
    run([
        javac, "-encoding", "UTF-8", "-source", "8", "-target", "8",
        "-classpath", ANDROID_JAR, "-d", classes_dir, *sources,
    ])
    run([d8, "--lib", ANDROID_JAR, "--min-api", "26", "--output", dex_dir,
         *sorted(classes_dir.rglob("*.class"))])
    repack_apk(unsigned, assembled, dex_dir / "classes.dex")
    run([zipalign, "-f", "4", assembled, aligned])
    if output.exists():
        output.unlink()
    run([
        apksigner, "sign", "--ks", keystore, "--ks-key-alias", "androiddebugkey",
        "--ks-pass", "pass:android", "--key-pass", "pass:android", "--out", output, aligned,
    ])
    verify = run([apksigner, "verify", "--verbose", output], capture=True).stdout
    raw_name_count = verify_raw_zip_names(output)
    print(verify, end="")
    print(f"Raw ZIP names verified: {raw_name_count}")
    print(f"Built {output}")


def build_instrumentation_only():
    if not ANDROID_JAR.is_file():
        raise FileNotFoundError(f"Android API jar is missing: {ANDROID_JAR}")
    aapt2 = executable(BUILD_TOOLS, "aapt2")
    d8 = executable(BUILD_TOOLS, "d8")
    zipalign = executable(BUILD_TOOLS, "zipalign")
    apksigner = executable(BUILD_TOOLS, "apksigner")
    javac = executable(JDK_BIN, "javac")
    keytool = executable(JDK_BIN, "keytool")
    keystore = Path.home() / ".android" / "debug.keystore"
    reset_build_directory()
    BUILD_DIR.mkdir(parents=True)
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    create_debug_keystore(keytool, keystore)
    build_instrumentation(aapt2, d8, zipalign, apksigner, javac, keystore)
    reset_build_directory()


def build(debug: bool, instrumentation: bool):
    required = [MAIN_DIR / "AndroidManifest.xml", ANDROID_JAR, ASSET_DIR / "web" / "index.html"]
    missing = [str(path) for path in required if not path.is_file()]
    if missing:
        raise FileNotFoundError("Missing build input: " + ", ".join(missing))

    aapt2 = executable(BUILD_TOOLS, "aapt2")
    d8 = executable(BUILD_TOOLS, "d8")
    zipalign = executable(BUILD_TOOLS, "zipalign")
    apksigner = executable(BUILD_TOOLS, "apksigner")
    javac = executable(JDK_BIN, "javac")
    java = executable(JDK_BIN, "java")
    keytool = executable(JDK_BIN, "keytool")

    reset_build_directory()
    BUILD_DIR.mkdir(parents=True)
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    run_native_tests(javac, java)

    compiled_resources = BUILD_DIR / "resources.zip"
    unsigned_apk = BUILD_DIR / "unsigned.apk"
    assembled_apk = BUILD_DIR / "assembled.apk"
    aligned_apk = BUILD_DIR / "aligned.apk"
    classes_dir = BUILD_DIR / "classes"
    dex_dir = BUILD_DIR / "dex"
    classes_dir.mkdir()
    dex_dir.mkdir()

    run([aapt2, "compile", "--dir", RES_DIR, "-o", compiled_resources])
    link = [
        aapt2, "link", "-o", unsigned_apk, "--manifest", MAIN_DIR / "AndroidManifest.xml",
        "-I", ANDROID_JAR, "--min-sdk-version", "26", "--target-sdk-version", "34",
        "--version-code", "1", "--version-name", "1.0", "-A", ASSET_DIR,
        "-0", "mp3", "-0", "mp4",
    ]
    if debug:
        link.append("--debug-mode")
    link.append(compiled_resources)
    run(link)

    java_sources = sorted(JAVA_DIR.rglob("*.java"))
    run([
        javac, "-encoding", "UTF-8", "-source", "8", "-target", "8",
        "-classpath", ANDROID_JAR, "-d", classes_dir, *java_sources,
    ])
    class_files = sorted(classes_dir.rglob("*.class"))
    run([d8, "--lib", ANDROID_JAR, "--min-api", "26", "--output", dex_dir, *class_files])
    repack_apk(unsigned_apk, assembled_apk, dex_dir / "classes.dex")
    run([zipalign, "-f", "4", assembled_apk, aligned_apk])

    keystore = Path.home() / ".android" / "debug.keystore"
    create_debug_keystore(keytool, keystore)
    output_name = "doctor-tablet-debug.apk" if debug else "doctor-tablet.apk"
    output_apk = OUTPUT_DIR / output_name
    if output_apk.exists():
        output_apk.unlink()
    run([
        apksigner, "sign", "--ks", keystore, "--ks-key-alias", "androiddebugkey",
        "--ks-pass", "pass:android", "--key-pass", "pass:android", "--out", output_apk, aligned_apk,
    ])
    verify = run([apksigner, "verify", "--verbose", "--print-certs", output_apk], capture=True).stdout
    raw_name_count = verify_raw_zip_names(output_apk)
    print(verify, end="")
    badging = run([aapt2, "dump", "badging", output_apk], capture=True).stdout
    assets = verify_assets(output_apk)
    evidence = {
        "apk": str(output_apk),
        "apkBytes": output_apk.stat().st_size,
        "apkSha256": sha256_file(output_apk),
        "debuggable": debug,
        "package": "com.roiril.mawarimi.tablet",
        "activity": "com.roiril.mawarimi.tablet.MainActivity",
        "assetCount": len(assets),
        "rawZipNameCount": raw_name_count,
        "rawZipNamesUseForwardSlash": True,
        "assets": assets,
        "badging": badging.splitlines(),
        "signatureVerification": verify.splitlines(),
    }
    evidence_path = OUTPUT_DIR / f"{output_apk.stem}-evidence.json"
    evidence_path.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(f"Built {output_apk}")
    print(f"Evidence {evidence_path}")
    if instrumentation:
        build_instrumentation(aapt2, d8, zipalign, apksigner, javac, keystore)
    reset_build_directory()


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description="Build the standalone doctor tablet APK without Gradle.")
    parser.add_argument("--debug", action="store_true", help="Enable Android debuggable mode and WebView debugging.")
    parser.add_argument("--instrumentation", action="store_true",
                        help="Also build the signed on-device functional test APK.")
    parser.add_argument("--instrumentation-only", action="store_true",
                        help="Rebuild only the on-device functional test APK.")
    args = parser.parse_args()
    if args.instrumentation_only:
        build_instrumentation_only()
        return
    build(args.debug, args.instrumentation)


if __name__ == "__main__":
    main()

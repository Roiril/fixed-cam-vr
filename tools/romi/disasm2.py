"""code セグメントを最小 ELF に包んで objdump -d に渡す（バイナリ直読みは Xtensa 版 objdump が落ちる）。"""
import struct, subprocess, sys, os
sys.stdout.reconfigure(encoding="utf-8")
base = r"C:\Users\kouga\Projects\Unity\fixed-cam-vr\output\romi"
objdump = os.path.expandvars(r"%USERPROFILE%\.platformio\packages\toolchain-xtensa-esp32s3\bin\xtensa-esp32s3-elf-objdump.exe")
img = open(base + r"\esp32-flash-backup-20261007.bin", "rb").read()[0x10000:0x10000 + 0x140000]

def make_elf(data, addr, name):
    shstr = b"\0.text\0.shstrtab\0"
    ehsize, shentsize = 52, 40
    text_off = ehsize
    shstr_off = text_off + len(data)
    shoff = (shstr_off + len(shstr) + 3) & ~3
    # ELF header: EM_XTENSA = 94
    eh = b"\x7fELF" + bytes([1, 1, 1, 0]) + b"\0" * 8
    eh += struct.pack("<HHIIIIIHHHHHH", 2, 94, 1, addr, 0, shoff, 0, ehsize, 0, 0, shentsize, 3, 2)
    sh = b"\0" * 40
    sh += struct.pack("<IIIIIIIIII", 1, 1, 0x6, addr, text_off, len(data), 0, 0, 4, 0)       # .text AX
    sh += struct.pack("<IIIIIIIIII", 7, 3, 0, 0, shstr_off, len(shstr), 0, 0, 1, 0)          # .shstrtab
    blob = eh + data + shstr
    blob += b"\0" * (shoff - len(blob)) + sh
    p = base + "\\" + name
    open(p, "wb").write(blob)
    return p

segs = []
nseg = img[1]
off = 24
for i in range(nseg):
    la, ln = struct.unpack("<II", img[off:off + 8])
    segs.append((la, off + 8, ln))
    off += 8 + ln

out = base + r"\disasm.txt"
with open(out, "w", encoding="utf-8") as f:
    for la, fo, ln in segs:
        if not (0x400D0000 <= la < 0x40400000 or 0x40080000 <= la < 0x400A0000):
            continue
        p = make_elf(img[fo:fo + ln], la, "seg_%08X.elf" % la)
        r = subprocess.run([objdump, "-d", p], capture_output=True, text=True)
        f.write(r.stdout)
        print("seg 0x%08X: %d lines rc=%d stderr=%r" % (la, r.stdout.count("\n"), r.returncode, r.stderr[:120]))
print("wrote", out)

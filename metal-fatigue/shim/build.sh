#!/bin/sh
# Builds DINPUT.dll (32-bit, no CRT) with clang + lld. No Windows SDK needed.
set -e
cd "$(dirname "$0")"
CF="--target=i686-pc-windows-msvc -O1 -ffreestanding -fno-builtin -fno-stack-protector -mno-stack-arg-probe -Wall -Wextra -Wno-unused-parameter"
llvm-dlltool -m i386 -k -d kernel32.def -l kernel32.lib
clang $CF -c core.c -o core.obj
clang $CF -c win.c -o win.obj
clang $CF -c crypto.c -o crypto.obj
lld-link /dll /nodefaultlib /machine:x86 /subsystem:windows /entry:DllMain@12 /safeseh:no \
  /def:dinput.def /out:../out/DINPUT.dll core.obj win.obj crypto.obj kernel32.lib

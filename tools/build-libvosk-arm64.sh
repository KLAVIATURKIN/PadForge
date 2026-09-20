#!/bin/bash
# Builds the Windows ARM64 libvosk.dll PadForge bundles at
# PadForge.App/Resources/Vosk/arm64/libvosk.dll.
#
# Vosk publishes Windows binaries for x64 and x86 only. Its maintainer wrote an
# ARM64 recipe (alphacep/vosk-api 1b308a30, travis/Dockerfile.winaarch64 and
# travis/build-wheels-winaarch64.sh) and never shipped its output. This is that
# recipe, with the same dependency chain and the same flags, run from Git Bash
# on a Windows x64 host instead of from Docker. Every difference from upstream
# is marked DIFF with its reason.
#
#   usage: tools/build-libvosk-arm64.sh <build-dir>
#
# Needs, and installs nothing:
#   - Git Bash (sh, perl, sed)
#   - llvm-mingw 20260908, the UCRT build for an x86_64 Windows host, unpacked.
#     https://github.com/mstorsjo/llvm-mingw/releases/tag/20260908
#     llvm-mingw-20260908-ucrt-x86_64.zip
#     sha256 1bcf74d06b724aeecaa6412ca85f5b26fb1da770e7cdcefa9263c9c5c3ad34b6
#     Point TOOLCHAIN at the unpacked folder.
#   - The CMake and Ninja that ship inside Visual Studio (VS_CMAKE_DIR).
#
# Every source is pinned to a commit. Upstream's Dockerfile pins only OpenBLAS.
# SRC may name a folder of local clones to save the downloads. The commits are
# checked out either way.
set -e

B=${1:?build dir in POSIX form, for example /c/tmp/libvosk-arm64}
TOOLCHAIN=${TOOLCHAIN:?path to the unpacked llvm-mingw-20260908-ucrt-x86_64 folder}
VS_CMAKE_DIR=${VS_CMAKE_DIR:-/c/Program Files/Microsoft Visual Studio/18/Community/Common7/IDE/CommonExtensions/Microsoft/CMake}
SRC=${SRC:-}
JOBS=${JOBS:-16}

TRIPLE=aarch64-w64-mingw32

#            name      upstream                                   commit                                    local clone name
SOURCES="
openfst   https://github.com/alphacep/openfst      18e94e63870ebcf79ebb42b7035cd3cb626ec090  openfst-vosk
clapack   https://github.com/alphacep/clapack      f4e1647de96343fb20eea08a9d4f0e0d04c31c51  clapack-vosk
kaldi     https://github.com/alphacep/kaldi        8b7fecf92b329b7253d9d1cd97898c8b13f9aef3  kaldi-vosk
vosk-api  https://github.com/alphacep/vosk-api     f73088da5840f9a382eb949f65cbfeaef901e983  vosk-api
OpenBLAS  https://github.com/OpenMathLib/OpenBLAS  0b678b19dc03f2a999d6e038814c4c50b9640a4e  OpenBLAS
"
# vosk-api f73088da is tag v0.3.38, the version of the managed Vosk package
# PadForge references and of the x64 libvosk.dll that package carries.
# OpenBLAS 0b678b19 is v0.3.20, upstream's pin for every platform it builds.

export PATH="$TOOLCHAIN/bin:$PATH"
TW=$(cygpath -m "$TOOLCHAIN")/bin
CMAKE="$VS_CMAKE_DIR/CMake/bin/cmake.exe"
NINJA_EXE="$VS_CMAKE_DIR/Ninja/ninja.exe"
NINJA=$(cygpath -m "$NINJA_EXE")
mkdir -p "$B" && cd "$B"
BW=$(cygpath -m "$B")
mkdir -p local/include local/lib

# core.autocrlf=false is REQUIRED. A Windows machine usually sets it to true,
# and CRLF breaks configure, the makefiles and OpenBLAS's perl scripts.
echo "$SOURCES" | while read -r name url commit local; do
  [ -z "$name" ] && continue
  if [ ! -d "$name" ]; then
    from=$url
    [ -n "$SRC" ] && [ -d "$SRC/$local" ] && from="$SRC/$local"
    git -c advice.detachedHead=false clone -q --no-hardlinks -c core.autocrlf=false "$from" "$name"
  fi
  git -C "$name" -c advice.detachedHead=false checkout -q "$commit"
  echo "$name at $(git -C "$name" rev-parse HEAD)"
done

# 1. OpenFST. DIFF: upstream runs autoreconf, configure and make (libtool). Git
#    Bash has no autotools, so the two static libraries vosk links are compiled
#    directly, with the flags configure.ac and upstream's CXXFLAGS produce. Same
#    sources, same flags.
cd "$B/openfst" && mkdir -p _obj
FST_FLAGS="-std=c++17 -fno-exceptions -Wno-deprecated-declarations -O3 -ftree-vectorize -DFST_NO_DYNAMIC_LINKING -Isrc/include"
ls src/lib/*.cc src/extensions/ngram/*.cc | xargs -P "$JOBS" -I{} sh -c "$TRIPLE-clang++ $FST_FLAGS -c {} -o _obj/\$(basename {} .cc).o"
rm -f "$B/local/lib/libfst.a" "$B/local/lib/libfstngram.a"
$TRIPLE-ar cr "$B/local/lib/libfst.a" _obj/compat.o _obj/encode.o _obj/flags.o _obj/fst.o _obj/fst-types.o _obj/mapped-file.o _obj/properties.o _obj/symbol-table.o _obj/symbol-table-ops.o _obj/weight.o _obj/util.o
$TRIPLE-ar cr "$B/local/lib/libfstngram.a" _obj/bitmap-index.o _obj/ngram-fst.o _obj/nthbit.o
cp -r src/include/fst "$B/local/include/"

# 2. OpenBLAS. Upstream's flags. DIFF: HOSTCC is the toolchain's own x86_64
#    compiler, where upstream's Linux container uses the system gcc.
cd "$B/OpenBLAS"
mingw32-make HOSTCC=x86_64-w64-mingw32-gcc CC=$TRIPLE-gcc ONLY_CBLAS=1 USE_LOCKING=1 USE_THREAD=0 USE_OPENMP=0 \
  TARGET=ARMV8 ARCH=arm64 BINARY=64 DYNAMIC_ARCH=0 -j "$JOBS" > "$B/openblas_build.log" 2>&1
mingw32-make PREFIX="$BW/local" install > "$B/openblas_install.log" 2>&1

# 3. CLAPACK. Upstream's flags. DIFF: the Ninja generator, and CMake 4 needs a
#    policy floor because the fork still says cmake_minimum_required(VERSION 2.6).
cd "$B/clapack" && rm -rf BUILD && mkdir BUILD && cd BUILD
"$CMAKE" -G Ninja -DCMAKE_MAKE_PROGRAM="$NINJA" -DCMAKE_POLICY_VERSION_MINIMUM=3.5 -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_C_COMPILER_TARGET=$TRIPLE -DCMAKE_C_COMPILER="$TW/$TRIPLE-gcc.exe" \
  -DCMAKE_AR="$TW/$TRIPLE-ar.exe" -DCMAKE_RANLIB="$TW/$TRIPLE-ranlib.exe" \
  -DCMAKE_SYSTEM_NAME=Windows -DCMAKE_SYSTEM_PROCESSOR=aarch64 -DCMAKE_CROSSCOMPILING=True .. > "$B/clapack_configure.log" 2>&1
"$NINJA_EXE" f2c blas lapack > "$B/clapack_build.log" 2>&1
cp F2CLIBS/libf2c/libf2c.a BLAS/SRC/libblas.a SRC/liblapack.a "$B/local/lib/"

# 4. Kaldi. Upstream's flags. DIFF: kaldi.mk paths are rewritten to C:/ form,
#    because mingw32-make is a native program and hands /c/... to clang as is.
cd "$B/kaldi/src"
CXX=$TRIPLE-g++ CXXFLAGS="-O3 -ftree-vectorize -DFST_NO_DYNAMIC_LINKING" ./configure --shared --mingw=yes --use-cuda=no \
  --mathlib=OPENBLAS_CLAPACK --host=$TRIPLE --openblas-clapack-root="$BW/local" \
  --fst-root="$BW/local" --fst-version=1.8.0 > "$B/kaldi_configure.log" 2>&1
sed -i -E 's#(^|[ =])/([a-zA-Z])/#\1\U\2:/#g' kaldi.mk
mingw32-make depend -j "$JOBS" > "$B/kaldi_depend.log" 2>&1
mingw32-make LLVM_BUILD=1 -j "$JOBS" online2 rnnlm > "$B/kaldi_build.log" 2>&1

# 5. libvosk.dll. DIFF from upstream's link line, three things:
#    -static folds libc++, libunwind and winpthreads into the DLL. Upstream's
#      script copies no runtime DLL at all, so a default llvm-mingw link, which
#      imports libc++.dll and libunwind.dll, would not load on a clean machine.
#    The .def file limits the exports to the C API in vosk_api.h.
#    --no-insert-timestamp keeps the link's clock out of the PE header.
cd "$B/vosk-api/src" && mkdir -p out
{ echo "LIBRARY libvosk.dll"; echo "EXPORTS"; sed -n 's/^[A-Za-z].*[ *]\(vosk_[a-z_0-9]*\) *(.*/    \1/p' vosk_api.h | sort -u; } > out/libvosk.def
OUTDIR=out EXTRA_LDFLAGS="out/libvosk.def -Wl,--out-implib,out/libvosk.lib -Wl,--no-insert-timestamp -static" CXX=$TRIPLE-g++ EXT=dll \
  KALDI_ROOT="$BW/kaldi" OPENFST_ROOT="$BW/local" OPENBLAS_ROOT="$BW/local" mingw32-make -j "$JOBS" > "$B/vosk_build.log" 2>&1

ls -la out/libvosk.dll
sha256sum out/libvosk.dll
echo "Copy out/libvosk.dll to PadForge.App/Resources/Vosk/arm64/libvosk.dll"

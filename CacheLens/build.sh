#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
export MACOSX_DEPLOYMENT_TARGET=10.15
mkdir -p build
if [ ! -d build/brotli ]; then
  git clone --depth 1 --branch v1.1.0 https://github.com/google/brotli.git build/brotli
fi
cmake -S build/brotli -B build/brotli-out -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DBROTLI_DISABLE_TESTS=ON -DCMAKE_OSX_ARCHITECTURES=x86_64 -DCMAKE_OSX_DEPLOYMENT_TARGET=10.15
cmake --build build/brotli-out --config Release -j 3
xcrun clang -arch x86_64 -mmacosx-version-min=10.15 -I build/brotli/c/include -c inflate.c -o build/inflate.o
LIBS=(build/inflate.o build/brotli-out/libbrotlidec.a build/brotli-out/libbrotlicommon.a)
xcrun swiftc -swift-version 5 -target x86_64-apple-macosx10.15 -import-objc-header inflate.h Cache.swift Tests/main.swift "${LIBS[@]}" -lz -o build/cache-tests
build/cache-tests
APP="build/CacheLens.app"
mkdir -p "$APP/Contents/MacOS"
mkdir -p "$APP/Contents/Resources"
cp build/brotli/LICENSE "$APP/Contents/Resources/Brotli-LICENSE.txt"
xcrun swiftc -O -swift-version 5 -target x86_64-apple-macosx10.15 -import-objc-header inflate.h Cache.swift Viewer.swift main.swift "${LIBS[@]}" -lz -framework Cocoa -framework WebKit -o "$APP/Contents/MacOS/CacheLens"
cp Info.plist "$APP/Contents/Info.plist"
codesign --force --sign - "$APP"
codesign --verify --deep --strict "$APP"
otool -L "$APP/Contents/MacOS/CacheLens"
ditto -c -k --sequesterRsrc --keepParent "$APP" build/CacheLens-macOS-10.15.zip

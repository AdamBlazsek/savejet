# CacheLens

A standalone Intel Mac app targeting macOS Catalina 10.15.7. Launch CacheLens.app, search, and click a cached page to render it. Python, Node, Chrome extensions, and developer tools are not required to run the finished application.

Chrome profiles under ~/Library/Caches/Google/Chrome are scanned automatically. The Choose Cache Folder button handles copied caches and custom locations. Original cache files are read only. Quit Chrome before scanning for a more consistent result.

Offline previews load cached HTML, styles, fonts and images. Scripts are disabled, forms are blocked, and previews do not intentionally load live network resources. Missing resources and pages dependent on JavaScript can look incomplete. This reads Chromium Simple Cache entries, not the legacy data_0 block-file format, history, deleted files, or Incognito sessions. Supports identity, gzip, deflate and Brotli compression; entries larger than 64 MiB are skipped. Search covers URLs and up to the first 2 MB of decoded HTML per entry. Modification dates are file dates, not guaranteed visit dates.

## Download

The Mac build workflow produces CacheLens-macOS-10.15.zip. Unzip and move CacheLens.app to Applications. Because this build is ad-hoc signed rather than Apple notarized, Catalina may require right-click > Open on first launch. Subsequent launches are a normal double-click. Apple Developer signing and notarization credentials are required to remove that first-launch prompt.

## Build once

On a Mac with Xcode command-line tools and CMake: `bash build.sh`. The script builds a statically bundled Brotli decoder, runs parser tests, compiles the native AppKit/WebKit app for Intel macOS 10.15, verifies its ad-hoc signature, and creates the ZIP. GitHub Actions performs the same steps. Runtime on Catalina must still be verified on a real Catalina Mac; a modern runner verifies compilation and parser tests only.

Format reference: https://chromium.googlesource.com/chromium/src/+/master/net/disk_cache/simple/simple_entry_format.h
Brotli decoder: https://github.com/google/brotli (v1.1.0, MIT licensed; license bundled in the app).

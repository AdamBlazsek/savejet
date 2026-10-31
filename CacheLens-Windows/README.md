# CacheLens for Windows

Double-click CacheLens.exe. It opens a local viewer in your default browser and scans Chrome profiles automatically. Keep the console window open while using it. No Python, .NET installation, administrator access, or browser extension is required. Windows 10/11 x64 is the target.

Search finds URLs, titles and available cached HTML text. Results label saved page bodies separately from history-only records. Click a saved page to render offline with cached CSS, images and fonts. Scripts and forms are disabled. Open original and Check archive are explicit links to external websites; scans and previews themselves stay local.

There is no date cutoff in the app. Repeated visits do not guarantee Chrome retained a page body. History commonly covers around 90 days, and may also have been cleared. A history record cannot reconstruct deleted contents. Cached file dates are modification dates, not visit dates. The app reads Simple Cache and legacy block-file cache; it does not recover deleted disk sectors, service-worker Cache Storage, Incognito data, or missing resources.

Quit Chrome before scanning if entries are locked or changing. You can also expand Choose a copied Chrome profile or cache folder and enter a full local folder path to a backup's User Data, profile, Cache, or Cache_Data directory. Originals are read without modifications. History is opened in SQLite read-only mode. Copied SQLite history may need the matching History-wal and History-shm files from the same snapshot.

Limits: 64 MiB per encoded/decoded resource; search indexes at most 2 MB of UTF-8 HTML text per page; up to 300 search results displayed, with the full matching count shown. Use search/date filters to narrow more results. Non-UTF-8 pages may display imperfectly. Legacy block-file shared dates may reflect other entries' changes. App data is held in memory; closing the app clears the scan.

## Build and verify

With .NET 8 SDK, run `dotnet publish -c Release -r win-x64 --self-contained true -o dist`. Then `dist/CacheLens.exe --self-test`. The Windows workflow also runs a browser smoke test against generated cache/history fixtures, including an old history entry, offline HTML/CSS/image rendering, disabled scripts and no external requests. Fixtures use synthetic data, never a real user's browsing history.

This unsigned app may prompt Windows SmartScreen on first run.

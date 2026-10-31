import Cocoa
import WebKit

final class OfflineHandler: NSObject, WKURLSchemeHandler {
    var entries: [String: CachedPage] = [:]
    func webView(_ webView: WKWebView, start task: WKURLSchemeTask) {
        guard let request = task.request.url, var parts = URLComponents(url: request, resolvingAgainstBaseURL: false) else { return }
        parts.scheme = parts.scheme == "cachehttps" ? "https" : "http"
        parts.fragment = nil
        guard let key = parts.url?.absoluteString, let entry = entries[key] else {
            task.didFailWithError(NSError(domain: "CacheLens", code: 404, userInfo: [NSLocalizedDescriptionKey: "This resource is not in Chrome’s cache."]))
            return
        }
        do {
            var body = try CacheReader.body(entry)
            if entry.mime.contains("html") || entry.mime.contains("css") {
                var text = String(data: body, encoding: .utf8) ?? String(data: body, encoding: .isoLatin1) ?? ""
                text = text.replacingOccurrences(of: "https://", with: "cachehttps://")
                    .replacingOccurrences(of: "http://", with: "cachehttp://")
                if entry.isPage {
                    // Block live fetches even when HTML contains unusual URL spellings.
                    let policy = "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src cachehttps: cachehttp: data:; style-src 'unsafe-inline' cachehttps: cachehttp:; font-src cachehttps: cachehttp: data:; media-src cachehttps: cachehttp:; frame-src 'none'; form-action 'none'; base-uri cachehttps: cachehttp:;\">"
                    text = policy + text
                }
                body = Data(text.utf8)
            }
            task.didReceive(URLResponse(url: request, mimeType: entry.mime, expectedContentLength: body.count, textEncodingName: "utf-8"))
            task.didReceive(body)
            task.didFinish()
        } catch {
            task.didFailWithError(NSError(domain: "CacheLens", code: 422, userInfo: [NSLocalizedDescriptionKey: "This entry is compressed with an unsupported format, incomplete, or has changed. Rescan after quitting Chrome."]))
        }
    }
    func webView(_ webView: WKWebView, stop task: WKURLSchemeTask) {}
}

final class AppDelegate: NSObject, NSApplicationDelegate, NSTableViewDataSource, NSTableViewDelegate, NSSearchFieldDelegate, WKNavigationDelegate {
    var window: NSWindow!
    let search = NSSearchField()
    let table = NSTableView()
    let status = NSTextField(labelWithString: "Finding Chrome’s cache…")
    let address = NSTextField(labelWithString: "Select a cached page")
    let handler = OfflineHandler()
    var web: WKWebView!
    var all: [CachedPage] = []
    var filtered: [CachedPage] = []
    var customRoots: [URL]?
    var scanning = false
    let rescanButton = NSButton(title: "Rescan", target: nil, action: nil)
    var scanSummary = ""

    func applicationDidFinishLaunching(_ notification: Notification) {
        let menu = NSMenu()
        let appItem = NSMenuItem(); menu.addItem(appItem)
        let appMenu = NSMenu(); appItem.submenu = appMenu
        appMenu.addItem(withTitle: "Quit CacheLens", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        let edit = NSMenuItem(); menu.addItem(edit); edit.submenu = NSMenu(title: "Edit")
        edit.submenu?.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.submenu?.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.submenu?.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        NSApp.mainMenu = menu
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1200, height: 780), styleMask: [.titled, .closable, .resizable, .miniaturizable], backing: .buffered, defer: false)
        window.title = "CacheLens — Chrome Cache Viewer"
        window.minSize = NSSize(width: 850, height: 520)
        window.center()
        let root = NSStackView(); root.orientation = .vertical; root.spacing = 12; root.edgeInsets = NSEdgeInsets(top: 16, left: 16, bottom: 12, right: 16)
        root.translatesAutoresizingMaskIntoConstraints = false
        window.contentView!.addSubview(root)
        NSLayoutConstraint.activate([root.leadingAnchor.constraint(equalTo: window.contentView!.leadingAnchor), root.trailingAnchor.constraint(equalTo: window.contentView!.trailingAnchor), root.topAnchor.constraint(equalTo: window.contentView!.topAnchor), root.bottomAnchor.constraint(equalTo: window.contentView!.bottomAnchor)])
        search.placeholderString = "Search cached pages by URL or page text"; search.delegate = self
        search.sendsSearchStringImmediately = true
        rescanButton.target = self; rescanButton.action = #selector(scan)
        let choose = NSButton(title: "Choose Cache Folder…", target: self, action: #selector(chooseFolder))
        let bar = NSStackView(views: [search, rescanButton, choose]); bar.spacing = 8
        root.addArrangedSubview(bar); bar.widthAnchor.constraint(equalTo: root.widthAnchor, constant: -32).isActive = true
        let split = NSSplitView(); split.isVertical = true; split.dividerStyle = .thin
        let scroll = NSScrollView(); scroll.hasVerticalScroller = true; scroll.documentView = table
        let col = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("page")); col.title = "Cached pages"; col.width = 350
        table.addTableColumn(col); table.headerView = nil; table.rowHeight = 62; table.delegate = self; table.dataSource = self
        table.columnAutoresizingStyle = .lastColumnOnlyAutoresizingStyle
        let preview = NSStackView(); preview.orientation = .vertical; preview.spacing = 8
        address.maximumNumberOfLines = 2; address.lineBreakMode = .byTruncatingMiddle; address.isSelectable = true
        preview.addArrangedSubview(address)
        let config = WKWebViewConfiguration()
        config.preferences.javaScriptEnabled = false
        config.websiteDataStore = .nonPersistent()
        config.setURLSchemeHandler(handler, forURLScheme: "cachehttps")
        config.setURLSchemeHandler(handler, forURLScheme: "cachehttp")
        web = WKWebView(frame: .zero, configuration: config); web.navigationDelegate = self
        preview.addArrangedSubview(web)
        address.widthAnchor.constraint(equalTo: preview.widthAnchor, constant: -16).isActive = true
        web.widthAnchor.constraint(equalTo: preview.widthAnchor).isActive = true
        split.addArrangedSubview(scroll); split.addArrangedSubview(preview)
        root.addArrangedSubview(split)
        split.widthAnchor.constraint(equalTo: bar.widthAnchor).isActive = true
        split.heightAnchor.constraint(greaterThanOrEqualToConstant: 350).isActive = true
        status.font = NSFont.systemFont(ofSize: 11); status.textColor = .secondaryLabelColor
        root.addArrangedSubview(status)
        status.widthAnchor.constraint(equalTo: bar.widthAnchor).isActive = true
        window.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true)
        split.setPosition(370, ofDividerAt: 0)
        scan()
    }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
    @objc func scan() {
        guard !scanning else { return }; scanning = true; rescanButton.isEnabled = false
        status.stringValue = "Reading Chrome’s cache…"; let roots = customRoots ?? CacheReader.roots()
        DispatchQueue.global(qos: .userInitiated).async {
            let result = CacheReader.scan(roots)
            DispatchQueue.main.async {
                self.all = result.pages
                self.handler.entries = [:]
                for page in result.pages {
                    if self.handler.entries[page.url] == nil { self.handler.entries[page.url] = page }
                }
                self.scanSummary = "\(result.pages.count) resources · \(result.skipped) unreadable entries"
                self.scanning = false; self.rescanButton.isEnabled = true; self.filter()
                if result.files == 0 {
                    self.status.stringValue = "No readable cache found. Choose Chrome’s cache folder, or quit Chrome and rescan."
                }
            }
        }
    }
    @objc func chooseFolder() {
        let panel = NSOpenPanel(); panel.canChooseDirectories = true; panel.canChooseFiles = false
        panel.message = "Choose a Chrome profile cache folder, Cache, or Cache_Data."
        if panel.runModal() == .OK, let url = panel.url { customRoots = [url]; scan() }
    }
    func controlTextDidChange(_ obj: Notification) { filter() }
    func filter() {
        let query = search.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        filtered = all.filter { $0.isPage && (query.isEmpty || $0.url.localizedCaseInsensitiveContains(query) || $0.text.localizedCaseInsensitiveContains(query)) }
        table.reloadData()
        status.stringValue = "\(filtered.count) pages · \(scanSummary) · Offline preview"
    }
    func numberOfRows(in tableView: NSTableView) -> Int { filtered.count }
    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        let page = filtered[row]
        let host = URL(string: page.url)?.host ?? page.url
        let title = NSTextField(labelWithString: host); title.font = NSFont.boldSystemFont(ofSize: 13)
        let detail = NSTextField(labelWithString: page.url); detail.font = NSFont.systemFont(ofSize: 11); detail.textColor = .secondaryLabelColor
        detail.lineBreakMode = .byTruncatingMiddle
        let date = NSTextField(labelWithString: "\(page.profile) · \(DateFormatter.localizedString(from: page.modified, dateStyle: .short, timeStyle: .short))")
        date.font = NSFont.systemFont(ofSize: 10); date.textColor = .secondaryLabelColor
        let cell = NSStackView(views: [title, detail, date]); cell.orientation = .vertical; cell.alignment = .leading; cell.spacing = 2
        return cell
    }
    func tableViewSelectionDidChange(_ notification: Notification) {
        guard table.selectedRow >= 0, table.selectedRow < filtered.count else { return }
        let page = filtered[table.selectedRow]; address.stringValue = page.url
        // The selected version wins when multiple Chrome profiles cached this URL.
        handler.entries[page.url] = page
        var parts = URLComponents(string: page.url); parts?.scheme = page.url.hasPrefix("https:") ? "cachehttps" : "cachehttp"
        if let url = parts?.url { web.load(URLRequest(url: url)) }
    }
    func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction, decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        let scheme = navigationAction.request.url?.scheme ?? ""
        decisionHandler(["cachehttps", "cachehttp", "about"].contains(scheme) ? .allow : .cancel)
    }
    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) { showError(error) }
    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) { showError(error) }
    func showError(_ error: Error) {
        if (error as NSError).code == NSURLErrorCancelled { return }
        status.stringValue = "Unable to preview this entry. It may be incomplete or use unsupported compression. Quit Chrome and rescan."
    }
}

import Foundation

struct CachedPage {
    let url: String
    let file: URL
    let profile: String
    let mime: String
    let encoding: String
    let body: Range<Int>
    let modified: Date
    let text: String
    var isPage: Bool { mime.contains("html") || mime.contains("xhtml") }
}

enum CacheError: Error { case invalid, unsupportedEncoding, tooLarge }

extension Data {
    func uint(_ offset: Int, _ count: Int) throws -> UInt64 {
        guard offset >= 0, count <= 8, offset <= self.count - count else { throw CacheError.invalid }
        return (0..<count).reduce(0) { $0 | UInt64(self[offset + $1]) << (8 * $1) }
    }
}

enum CacheReader {
    static let maxSize = 64 * 1024 * 1024
    static func read(_ file: URL, profile: String) throws -> CachedPage {
        let values = try file.resourceValues(forKeys: [.fileSizeKey, .contentModificationDateKey])
        guard (values.fileSize ?? 0) <= maxSize else { throw CacheError.tooLarge }
        let data = try Data(contentsOf: file)
        guard data.count >= 72, try data.uint(0, 8) == 0xfcfb6d1ba7725c30 else { throw CacheError.invalid }
        let keyLength = Int(try data.uint(12, 4))
        guard keyLength > 0, keyLength <= data.count - 72 else { throw CacheError.invalid }
        let key = String(decoding: data[24..<24+keyLength], as: UTF8.self)
        let regex = try NSRegularExpression(pattern: "https?://[^\\s\\u0000]+")
        guard let match = regex.matches(in: key, range: NSRange(key.startIndex..., in: key)).last,
              let range = Range(match.range, in: key) else { throw CacheError.invalid }
        let url = String(key[range])
        let eof = data.count - 24
        guard try data.uint(eof, 8) == 0xf4fa6f45970d41d8 else { throw CacheError.invalid }
        let flags = try data.uint(eof+8, 4)
        let metadataSize = Int(try data.uint(eof+16, 4))
        let metadataEnd = eof - ((flags & 2) != 0 ? 32 : 0)
        let metadataStart = metadataEnd - metadataSize
        let bodyStart = 24 + keyLength
        let bodyEnd = metadataStart - 24
        guard bodyEnd >= bodyStart, metadataStart >= 24,
              try data.uint(bodyEnd, 8) == 0xf4fa6f45970d41d8 else { throw CacheError.invalid }
        // HttpResponseInfo is a pickle containing a NUL-delimited HTTP header block.
        let metadata = String(decoding: data[metadataStart..<metadataEnd], as: UTF8.self)
        let lines = metadata.components(separatedBy: "\0")
        func header(_ name: String) -> String {
            let prefix = name.lowercased() + ":"
            return lines.first(where: { $0.lowercased().hasPrefix(prefix) })?.dropFirst(prefix.count).trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        }
        var mime = header("content-type").components(separatedBy: ";")[0].lowercased()
        if mime.isEmpty {
            let sample = String(decoding: data[bodyStart..<min(bodyEnd, bodyStart+200)], as: UTF8.self).lowercased()
            mime = sample.contains("<html") || sample.contains("<!doctype html") ? "text/html" : "application/octet-stream"
        }
        let encoding = header("content-encoding").lowercased()
        let raw = data.subdata(in: bodyStart..<bodyEnd)
        let decoded = try? decode(raw, encoding: encoding)
        let text = mime.contains("html") ? String(decoding: (decoded ?? Data()).prefix(2_000_000), as: UTF8.self) : ""
        return CachedPage(url: url, file: file, profile: profile, mime: mime, encoding: encoding,
                          body: bodyStart..<bodyEnd, modified: values.contentModificationDate ?? .distantPast, text: text)
    }

    static func decode(_ raw: Data, encoding: String) throws -> Data {
        if encoding.isEmpty || encoding == "identity" { return raw }
        guard ["gzip", "deflate", "br"].contains(encoding) else { throw CacheError.unsupportedEncoding }
        var out = Data(count: maxSize)
        let length: Int = out.withUnsafeMutableBytes { dest in
            raw.withUnsafeBytes { src in
                let input = src.bindMemory(to: UInt8.self).baseAddress
                let output = dest.bindMemory(to: UInt8.self).baseAddress
                if encoding == "br" { return Int(cache_brotli(input, raw.count, output, maxSize)) }
                return Int(cache_inflate(input, raw.count, output, maxSize, encoding == "gzip" ? 47 : 15))
            }
        }
        guard length >= 0 else { throw CacheError.invalid }
        out.count = length
        return out
    }

    static func body(_ page: CachedPage) throws -> Data {
        let data = try Data(contentsOf: page.file)
        guard data.count <= maxSize, page.body.upperBound <= data.count else { throw CacheError.invalid }
        return try decode(data.subdata(in: page.body), encoding: page.encoding)
    }

    static func roots() -> [URL] {
        let root = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Caches/Google/Chrome")
        let profiles = (try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil)) ?? []
        return profiles.filter { $0.lastPathComponent == "Default" || $0.lastPathComponent.hasPrefix("Profile ") }
    }

    static func scan(_ roots: [URL]) -> (pages: [CachedPage], skipped: Int, files: Int) {
        var pages: [CachedPage] = []
        var skipped = 0, files = 0
        for root in roots {
            guard let iterator = FileManager.default.enumerator(at: root, includingPropertiesForKeys: [.isRegularFileKey],
                options: [.skipsHiddenFiles, .skipsPackageDescendants]) else { continue }
            for case let file as URL in iterator where file.lastPathComponent.hasSuffix("_0") {
                files += 1
                do { pages.append(try read(file, profile: root.lastPathComponent)) } catch { skipped += 1 }
            }
        }
        return (pages.sorted { $0.modified > $1.modified }, skipped, files)
    }
}

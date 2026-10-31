import Foundation

func word(_ number: UInt64, _ size: Int) -> Data {
    Data((0..<size).map { UInt8(truncatingIfNeeded: number >> ($0 * 8)) })
}
func footer(_ size: Int, flags: UInt64 = 0) -> Data {
    word(0xf4fa6f45970d41d8, 8) + word(flags, 4) + word(0, 4) + word(UInt64(size), 4) + word(0, 4)
}
let temp = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
try FileManager.default.createDirectory(at: temp, withIntermediateDirectories: true)
defer { try? FileManager.default.removeItem(at: temp) }
let key = Data("1/0/https://example.com/page".utf8)
let body = Data("<!doctype html><html><body>Recovered page</body></html>".utf8)
let meta = Data("pickle\0HTTP/1.1 200 OK\0Content-Type: text/html; charset=utf-8\0\0".utf8)
func fixture(sha: Bool) -> Data {
    var data = word(0xfcfb6d1ba7725c30, 8) + word(9, 4) + word(UInt64(key.count), 4) + word(0, 8)
    data.append(key); data.append(body); data.append(footer(0)); data.append(meta)
    if sha { data.append(Data(repeating: 0, count: 32)) }
    data.append(footer(meta.count, flags: sha ? 2 : 0))
    return data
}
for sha in [false, true] {
    let file = temp.appendingPathComponent(sha ? "sha_0" : "plain_0")
    try fixture(sha: sha).write(to: file)
    let page = try CacheReader.read(file, profile: "Default")
    precondition(page.url == "https://example.com/page")
    precondition(page.isPage && page.text.contains("Recovered page"))
    let recovered = try CacheReader.body(page)
    precondition(recovered == body)
}
let broken = temp.appendingPathComponent("broken_0")
try Data("invalid".utf8).write(to: broken)
let result = CacheReader.scan([temp])
precondition(result.pages.count == 2 && result.skipped == 1)
for length in [0, 23, 24, 71, 72, 100] {
    try fixture(sha: true).prefix(length).write(to: broken)
    do { _ = try CacheReader.read(broken, profile: "test"); fatalError("Accepted truncated entry") }
    catch {}
}
let gzip: [UInt8] = [31,139,8,0,0,0,0,0,0,3,203,72,205,201,201,7,0,134,166,16,54,5,0,0,0]
let inflated = try CacheReader.decode(Data(gzip), encoding: "gzip")
precondition(inflated == Data("hello".utf8))
do { _ = try CacheReader.decode(Data(), encoding: "unknown"); fatalError("Accepted unknown encoding") }
catch CacheError.unsupportedEncoding {} 
print("PASS: cache layout, SHA footer, extraction, search text, corrupt/truncated entries, scan counts, gzip, unsupported encoding")

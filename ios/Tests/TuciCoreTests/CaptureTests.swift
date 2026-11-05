import Foundation
import Testing
@testable import TuciCore

// This response is also accepted by the Windows RecordingHttpHandler fixture.
// It intentionally omits roots and grammar and supplies two collocations.
let legacyCaptureJSON = """
{"is_valid_word":true,"error":"","spelling":{"input_word":"resilient","word":"resilient","status":"valid","suggestions":[]},"phonetics":{"uk":"/rɪˈzɪliənt/","us":""},"primary_pos":"adjective","meanings":[{"part_of_speech":"adjective","definition":"有韧性的"}],"examples":[{"english":"She remained resilient.","chinese":"她依然坚韧。"},{"english":"The economy is resilient.","chinese":"经济具有韧性。"}],"collocations":[{"english":"a resilient economy","chinese":"韧性强的经济体"},{"english":"resilient communities","chinese":"具有韧性的社区"}],"related":[{"word":"resilience","relation":"same_root"}]}
"""

struct CaptureTests {
    @Test func olderWindowsResponseRendersAndResolvesOnlyUniqueLinks() throws {
        let notes = ["名词/economy.md", "名词/resilience.md"].map { LibraryNote(path: $0, markdown: "") }
        let card = try CaptureService.parse(legacyCaptureJSON, word: "resilient", context: "[[user]] <p>", notes: notes)
        #expect(card.path == "形容词/resilient.md")
        #expect(card.markdown.contains("The [[名词/economy|economy]] is **resilient**."))
        #expect(card.markdown.contains("[[名词/resilience|resilience]]"))
        #expect(card.markdown.contains("&#91;&#91;user&#93;&#93; &lt;p&gt;"))
        let ambiguous = try CaptureService.parse(legacyCaptureJSON, word: "resilient", notes: notes + [LibraryNote(path: "archive/economy.md", markdown: "")])
        #expect(!ambiguous.markdown.contains("|economy]]"))
    }

    @Test func boundedCorrectionAndIdentity() throws {
        let corrected = legacyCaptureJSON.replacingOccurrences(of: #""input_word":"resilient""#, with: #""input_word":"resilent""#)
            .replacingOccurrences(of: #""status":"valid""#, with: #""status":"corrected""#)
        let card = try CaptureService.parse(corrected, word: "resilent")
        #expect(card.word == "resilient")
        #expect(card.markdown.contains("original_input: \"resilent\""))
        #expect(throws: CaptureFailure.self) { _ = try CaptureService.parse(corrected, word: "unrelated") }
        let inflected = corrected.replacingOccurrences(of: "resilent", with: "resilients")
        #expect(throws: CaptureFailure.self) { _ = try CaptureService.parse(inflected, word: "resilients") }
        #expect(throws: CaptureFailure.self) { _ = try CaptureService.parse(legacyCaptureJSON.replacingOccurrences(of: "remained resilient", with: "remained resiliently"), word: "resilient") }
        #expect(throws: CaptureFailure.self) { _ = try CapturedWord.normalize("../outside") }
    }

    @Test func streamNeverCommitsTruncationOrMissingStop() throws {
        var events = CaptureStream()
        try events.receive(line: #"data: {"choices":[{"delta":{"content":"一段"}}]}"#)
        try events.receive(line: "")
        try events.receive(line: "data: [DONE]")
        #expect(throws: CaptureFailure.self) { try events.receive(line: "") }
        var good = CaptureStream()
        for line in [#"data: {"choices":[{"delta":{"content":"完整"},"finish_reason":"stop"}]}"#,"","data: [DONE]",""] {
            try good.receive(line: line)
        }
        #expect(good.done && good.content == "完整")
        #expect(throws: CaptureFailure.self) {
            _ = try CaptureService.completionContent(Data(#"{"choices":[{"finish_reason":"length","message":{"content":"partial"}}]}"#.utf8))
        }
    }

    @Test func fallbackForExplicitStreamRejection() async throws {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [CaptureProtocol.self]
        let session = URLSession(configuration: config)
        defer { session.invalidateAndCancel() }
        let result = try await CaptureService(session: session).generate(word: "RESILIENT", context: "",
            api: .init(baseURL: "https://fixture.invalid/v1", apiKey: "fake-private-secret", model: "fixture"), classify: false, notes: [])
        #expect(CaptureProtocol.count("fixture.invalid") == 2)
        #expect(result.path == "resilient.md")
        #expect(result.markdown.contains("有韧性的"))
        #expect(result.markdown.contains("[已隐藏]") || result.markdown.contains("&#91;已隐藏&#93;"))
        #expect(!result.markdown.contains("fake-private-secret"))
    }

    @Test func cancelsInFlightRequestAndNeverRetriesAuthenticationErrors() async throws {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [CaptureProtocol.self]
        let session = URLSession(configuration: config)
        defer { session.invalidateAndCancel() }
        func run(_ host: String, deadline: Duration = .seconds(90)) async throws -> CapturedCard {
            try await CaptureService(session: session, deadline: deadline).generate(word: "word", context: "",
                api: .init(baseURL: "https://" + host + "/v1", apiKey: "fake-private-secret", model: "fixture"),
                classify: false, notes: [])
        }
        do { _ = try await run("denied.invalid"); Issue.record("Accepted a denied request") }
        catch { #expect(error.localizedDescription.contains("鉴权")) }
        #expect(CaptureProtocol.count("denied.invalid") == 1)

        do { _ = try await run("slow.invalid", deadline: .milliseconds(150)); Issue.record("Missing deadline") }
        catch { #expect(error.localizedDescription.contains("超时")) }
        #expect(CaptureProtocol.count("slow.invalid") == 1)

        let request = Task { try await run("cancel.invalid") }
        for _ in 0..<200 where CaptureProtocol.count("cancel.invalid") == 0 {
            try await Task.sleep(for: .milliseconds(10))
        }
        #expect(CaptureProtocol.count("cancel.invalid") == 1)
        let before = ContinuousClock.now
        request.cancel()
        do { _ = try await request.value; Issue.record("Missing cancellation") }
        catch { #expect(error is CancellationError) }
        #expect(before.duration(to: .now) < .seconds(3))
        #expect(CaptureProtocol.count("cancel.invalid") == 1)
    }

    @Test func endpointAndThirdPerson() throws {
        #expect(try CaptureService.endpoint("https://a.invalid/v1/").path == "/v1/chat/completions")
        #expect(try CaptureService.endpoint("http://127.0.0.1:8800/chat/completions").path == "/chat/completions")
        #expect(throws: CaptureFailure.self) { _ = try CaptureService.endpoint("https://name:secret@a.invalid/v1") }
        #expect(CapturedWord.exampleForms(word:"produce", lemma:"produce", verb:true).contains("produces"))
        #expect(!CapturedWord.exampleForms(word:"introduced", lemma:"introduce", verb:true).contains("introduces"))
    }
}

private final class CaptureProtocol: URLProtocol {
    private static var calls = [String:Int]()
    private static let lock = NSLock()
    static func count(_ host: String) -> Int { lock.withLock { calls[host] ?? 0 } }
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        guard request.url?.path == "/v1/chat/completions", request.value(forHTTPHeaderField: "Authorization") == "Bearer fake-private-secret" else {
            client?.urlProtocol(self, didFailWithError: URLError(.badURL)); return
        }
        let host = request.url!.host!
        Self.lock.withLock { Self.calls[host, default: 0] += 1 }
        if host == "slow.invalid" || host == "cancel.invalid" { return }
        if host == "denied.invalid" {
            let response = HTTPURLResponse(url: request.url!, statusCode: 401, httpVersion: nil, headerFields: nil)!
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: Data("stream is not supported".utf8))
            client?.urlProtocolDidFinishLoading(self)
            return
        }
        let data: Data
        if let body = request.httpBody { data = body }
        else if let body = request.httpBodyStream {
            body.open()
            defer { body.close() }
            var bytes = [UInt8](repeating: 0, count: 4096), result = Data()
            while true {
                let count = body.read(&bytes, maxLength: bytes.count)
                if count <= 0 { break }
                result.append(contentsOf: bytes.prefix(count))
            }
            data = result
        } else { data = Data() }
        let payload = (try? JSONSerialization.jsonObject(with: data)) as? [String:Any]
        let streaming = (payload?["stream"] as? Bool) ?? true
        let response = HTTPURLResponse(url: request.url!, statusCode: streaming ? 422 : 200, httpVersion: "HTTP/1.1",
                                       headerFields: ["Content-Type":"application/json"])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        let output: [String:Any] = streaming ? ["error":["message":"stream is not supported"]]
            : ["choices":[["finish_reason":"stop","message":["content":legacyCaptureJSON.replacingOccurrences(of: "有韧性的", with: "有韧性的 fake-private-secret")]]]]
        client?.urlProtocol(self, didLoad: try! JSONSerialization.data(withJSONObject: output))
        client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}

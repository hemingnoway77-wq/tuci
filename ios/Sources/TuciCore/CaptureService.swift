import Foundation

public struct CaptureService: Sendable {
    private let session: URLSession
    private let deadline: Duration
    public init(session: URLSession = .shared) { self.init(session: session, deadline: .seconds(90)) }
    init(session: URLSession, deadline: Duration) {
        self.session = session
        self.deadline = deadline
    }

    public func generate(word raw: String, context: String, api: AIConfiguration, classify: Bool, notes: [LibraryNote],
                         receiving: @escaping @Sendable () async -> Void = {}) async throws -> CapturedCard {
        let word = try CapturedWord.normalize(raw)
        guard context.utf16.count <= 2000 else { throw CaptureFailure("原句不能超过 2,000 个字符。") }
        let endpoint = try Self.endpoint(api.baseURL)
        let key = api.apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        let model = api.model.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !key.isEmpty, !model.isEmpty, model.count <= 256, key.count <= 8192,
              key.rangeOfCharacter(from: .controlCharacters) == nil else {
            throw CaptureFailure("请在设置中填写有效的 API Key 和模型名。")
        }
        do {
            return try await withThrowingTaskGroup(of: CapturedCard.self) { group in
                group.addTask {
                    try await Task.sleep(for: deadline)
                    throw CaptureFailure("AI 请求超时（最长 90 秒），请重试。")
                }
                group.addTask {
                    let content = try await complete(endpoint: endpoint, key: key, model: model, word: word, context: context, receiving: receiving)
                    let redacted = content.replacingOccurrences(of: key, with: "[已隐藏]")
                    let result = try Self.parse(redacted, word: word, context: context, notes: notes, classify: classify)
                    try Task.checkCancellation()
                    return result
                }
                defer { group.cancelAll() }
                return try await group.next()!
            }
        } catch {
            if Task.isCancelled { throw CancellationError() }
            if let safe = error as? CaptureFailure {
                CaptureDiagnostic.record(safe)
                throw safe
            }
            if (error as? URLError)?.code == .timedOut { throw CaptureFailure("请求超时，请检查网络后重试。") }
            throw CaptureFailure("网络连接失败，请检查网络和 API 地址。")
        }
    }

    static func parse(_ text: String, word: String, context: String = "", notes: [LibraryNote] = [],
                      classify: Bool = true, date: Date = Date()) throws -> CapturedCard {
        var json = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let fence = String(repeating: "\u{60}", count: 3)
        if json.lowercased().hasPrefix(fence + "json"), json.hasSuffix(fence), let start = json.firstIndex(of: "\n") {
            json = String(json[json.index(after: start)...].dropLast(3))
        }
        // Enforce the same depth bound as the Windows parser, including unknown fields.
        var depth = 0, inString = false, escaped = false
        for byte in json.utf8 {
            if escaped { escaped = false; continue }
            if byte == 92 && inString { escaped = true; continue }
            if byte == 34 { inString.toggle(); continue }
            guard !inString else { continue }
            if byte == 91 || byte == 123 { depth += 1 }
            if byte == 93 || byte == 125 { depth -= 1 }
            guard (0...16).contains(depth) else { throw CaptureFailure("AI 返回的 JSON 层级超出限制。") }
        }
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        let value: WordAnalysis
        do { value = try decoder.decode(WordAnalysis.self, from: Data(json.utf8)) }
        catch DecodingError.keyNotFound(let key, let context) {
            let path = (context.codingPath + [key]).map(\.stringValue).joined(separator: ".")
            throw CaptureFailure("AI 返回的词卡缺少字段「" + path + "」。")
        }
        catch DecodingError.typeMismatch(_, let context) {
            throw CaptureFailure("AI 返回的字段「" + context.codingPath.map(\.stringValue).joined(separator: ".") + "」类型不正确。")
        }
        catch { throw CaptureFailure("AI 返回的解析 JSON 不完整或字段值无效。") }
        let target = try value.validate(input: word)
        return try CaptureMarkdown.render(value, word: target, input: word, context: context, classify: classify, notes: notes, date: date)
    }

    static func endpoint(_ raw: String) throws -> URL {
        guard var parts = URLComponents(string: raw.trimmingCharacters(in: .whitespacesAndNewlines)),
              ["https","http"].contains(parts.scheme?.lowercased() ?? ""), parts.host?.isEmpty == false,
              parts.user == nil, parts.password == nil, parts.query == nil, parts.fragment == nil else {
            throw CaptureFailure("请填写完整的 HTTP 或 HTTPS 接口地址。")
        }
        while parts.path.hasSuffix("/") { parts.path.removeLast() }
        if !parts.path.lowercased().hasSuffix("/chat/completions") { parts.path += "/chat/completions" }
        guard let url = parts.url else { throw CaptureFailure("接口地址无效。") }
        return url
    }

    private func complete(endpoint: URL, key: String, model: String, word: String, context: String,
                          receiving: @escaping @Sendable () async -> Void) async throws -> String {
        let userMessage = try JSONSerialization.data(withJSONObject: ["word":word, "context":context])
        for stream in [true, false] {
            try Task.checkCancellation()
            var request = URLRequest(url: endpoint, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 90)
            request.httpMethod = "POST"
            request.setValue("Bearer " + key, forHTTPHeaderField: "Authorization")
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            var body: [String:Any] = ["model":model, "stream":stream, "messages":[["role":"system","content":capturePrompt],
                ["role":"user","content":String(decoding: userMessage, as: UTF8.self)]]]
            if endpoint.scheme == "https", endpoint.host?.lowercased() == "api.deepseek.com",
               ["deepseek-flash","deepseek-v4-pro"].contains(model.lowercased()) { body["thinking"] = ["type":"disabled"] }
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
            let (bytes, response) = try await session.bytes(for: request, delegate: NoAIRequestRedirect())
            defer { bytes.task.cancel() }
            guard let http = response as? HTTPURLResponse else { throw CaptureFailure("AI 服务响应无效。") }
            let limit = 1024 * 1024
            guard http.expectedContentLength <= Int64(limit) else { throw CaptureFailure("AI 返回内容过大。") }
            let success = (200..<300).contains(http.statusCode)
            if success, http.mimeType?.lowercased() == "text/event-stream" {
                var events = CaptureStream()
                var line = Data(), afterCR = false, count = 0, notified = false
                for try await byte in bytes {
                    count += 1
                    guard count <= limit else { throw CaptureFailure("AI 返回内容过大。") }
                    if byte == 10 && afterCR { afterCR = false; continue }
                    afterCR = byte == 13
                    if byte == 10 || afterCR {
                        guard let raw = String(data: line, encoding: .utf8) else { throw CaptureFailure("流式响应编码无效。") }
                        try events.receive(line: raw)
                        line.removeAll(keepingCapacity: true)
                        if !notified && !events.content.isEmpty { notified = true; await receiving() }
                        if events.done { return events.content }
                    } else { line.append(byte) }
                }
                throw CaptureFailure("AI 的流式响应未完整结束，请重试。")
            }
            var data = Data()
            for try await byte in bytes {
                guard data.count < limit else { throw CaptureFailure("AI 返回内容过大。") }
                data.append(byte)
            }
            if success {
                let content = try Self.completionContent(data)
                await receiving()
                return content
            }
            let errorText = String(decoding: data, as: UTF8.self)
            if stream, [400,422].contains(http.statusCode),
               errorText.range(of: #"\bstream\b["' ]*(?:parameter )?(?:is )?(?:not (?:supported|allowed)|unsupported|unrecognized|不支持)|(?:unsupported|unknown|unrecognized) (?:parameter|argument|field)[ :=\\"']*\bstream\b|(?:does not support|不支持)\s*["']?\bstream\b"#, options: [.regularExpression,.caseInsensitive]) != nil {
                continue
            }
            switch http.statusCode {
            case 401,403: throw CaptureFailure("API 鉴权失败，请检查 Key 和访问权限。")
            case 404: throw CaptureFailure("接口地址或模型不存在。")
            case 429: throw CaptureFailure("请求受限或额度不足，请稍后重试。")
            default: throw CaptureFailure("AI 服务返回 HTTP \(http.statusCode)，请检查配置后重试。")
            }
        }
        throw CaptureFailure("AI 接口未返回有效结果。")
    }

    static func completionContent(_ data: Data) throws -> String {
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String:Any], root["error"] == nil,
              let choices = root["choices"] as? [[String:Any]], choices.count == 1, let choice = choices.first,
              choice["finish_reason"] as? String == "stop", let message = choice["message"] as? [String:Any],
              (message["refusal"] as? String ?? "").isEmpty, let content = message["content"] as? String else {
            throw CaptureFailure("AI 返回的 Chat Completions 响应不完整或已被截断。")
        }
        return content
    }
}

// A redirected request may replay both the user's context and bearer token.
private final class NoAIRequestRedirect: NSObject, URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}

struct CaptureStream {
    var content = ""
    var done = false
    private var stopped = false
    private var firstLine = true
    private var payload: [String] = []
    mutating func receive(line raw: String) throws {
        let line = firstLine && raw.hasPrefix("\u{FEFF}") ? String(raw.dropFirst()) : raw
        firstLine = false
        if line.hasPrefix("data:") {
            let value = line.dropFirst(5)
            payload.append(String(value.first == " " ? value.dropFirst() : value))
            return
        }
        guard line.isEmpty, !payload.isEmpty else { return }
        let event = payload.joined(separator: "\n")
        payload = []
        let invalid = CaptureFailure("AI 流式响应格式无效或已被截断。")
        if event == "[DONE]" {
            guard stopped && !content.isEmpty else { throw invalid }
            done = true
            return
        }
        guard let root = try? JSONSerialization.jsonObject(with: Data(event.utf8)) as? [String:Any],
              root["error"] == nil, let choices = root["choices"] as? [[String:Any]] else { throw invalid }
        if choices.isEmpty { return }
        guard choices.count == 1, !stopped, let choice = choices.first, (choice["index"] as? Int ?? 0) == 0,
              let delta = choice["delta"] as? [String:Any], (delta["refusal"] as? String ?? "").isEmpty else { throw invalid }
        if let piece = delta["content"], !(piece is NSNull) {
            guard let text = piece as? String else { throw invalid }
            content += text
        }
        if let reason = choice["finish_reason"], !(reason is NSNull) {
            guard reason as? String == "stop" else { throw invalid }
            stopped = true
        }
    }
}

private enum CaptureDiagnostic {
    static let logger = DispatchQueue(label: "tuci.safe-analysis-log")
    static func record(_ error: CaptureFailure) {
        // Error reasons may include spelling suggestions, so persist only our own classifications.
        let category: String
        if error.reason.contains("鉴权") { category = "authentication" }
        else if error.reason.contains("超时") { category = "timeout" }
        else if error.reason.contains("拼写") { category = "spelling" }
        else { category = "analysis" }
        logger.async {
            let root = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
                .appendingPathComponent("TuciDiagnostics", isDirectory: true)
            let file = root.appendingPathComponent("analysis.log")
            do {
                try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
                var entries = (try? Data(contentsOf: file)) ?? Data()
                if entries.count > 256 * 1024 { entries.removeAll() }
                entries.append(Data("\(ISO8601DateFormatter().string(from: Date())) \(category)\n".utf8))
                try entries.write(to: file, options: .atomic)
            } catch { /* Keep the original user-facing error. */ }
        }
    }
}

import Foundation

public struct NoteOperation: Codable, Equatable, Sendable {
    public let operationId: String
    public let noteId: String
    public let baseVersion: Int
    public let path: String
    public let markdown: String
    public let deleted: Bool
    public let regions: [String: String]?
}

public struct DeviceGrant: Codable, Sendable {
    public let token: String
    public let vaultId: String
}

public struct SyncChange: Codable, Sendable {
    public let seq: Int
    public let noteId: String
    public let version: Int
    public let path: String
    public let markdown: String
    public let deleted: Bool
    public let regions: [String: String]?
}
public struct SyncPage: Codable, Sendable {
    public let changes: [SyncChange]
    public let cursor: Int
    public let hasMore: Bool
}
public struct OperationReceipt: Codable, Sendable {
    public let noteId: String
    public let version: Int
    public let path: String
    public let conflict: Bool
}

private final class NoRedirects: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) { completionHandler(nil) }
}

public struct SyncAPI: Sendable {
    public let base: URL
    private let session: URLSession
    public init(server: String, session: URLSession? = nil) throws {
        guard let url = URL(string: server.trimmingCharacters(in: .whitespacesAndNewlines)),
              let host = url.host, !host.isEmpty,
              (url.scheme?.lowercased() == "https" || (url.scheme == "http" && ["127.0.0.1", "[::1]"].contains(host))),
              url.user == nil, url.password == nil, url.query == nil, url.fragment == nil else { throw LibraryError.invalidServer }
        base = url
        self.session = session ?? Self.defaultSession
    }
    private static let defaultSession = URLSession(configuration: .ephemeral, delegate: NoRedirects(), delegateQueue: nil)
    private func call<T: Decodable>(_ endpoint: String, token: String?, body: Data? = nil, query: [URLQueryItem] = []) async throws -> T {
        var parts = URLComponents(url: base.appendingPathComponent("v1").appendingPathComponent(endpoint), resolvingAgainstBaseURL: false)!
        if !query.isEmpty { parts.queryItems = query }
        var request = URLRequest(url: parts.url!)
        request.timeoutInterval = 30
        request.httpMethod = body == nil ? "GET" : "POST"
        request.httpBody = body
        if let token { request.setValue("Bearer " + token, forHTTPHeaderField: "Authorization") }
        if body != nil { request.setValue("application/json", forHTTPHeaderField: "Content-Type") }
        let (data, response) = try await session.data(for: request)
        guard let status = (response as? HTTPURLResponse)?.statusCode else { throw LibraryError.invalidResponse }
        if status == 409 && endpoint == "changes" { throw LibraryError.restoredHistory }
        if status == 401 || status == 403 { throw LibraryError.denied }
        guard (200..<300).contains(status) else {
            throw NSError(domain: "兔词同步", code: status, userInfo: [NSLocalizedDescriptionKey: "同步请求失败（HTTP \(status)）。"])
        }
        return try JSONDecoder().decode(T.self, from: data)
    }
    public func redeem(code: String, name: String) async throws -> DeviceGrant {
        let reply: DeviceGrant = try await call("redeem", token: nil, body: JSONEncoder().encode(["code": code, "deviceName": name]))
        guard !reply.token.isEmpty, !reply.vaultId.isEmpty else { throw LibraryError.invalidResponse }
        return reply
    }
    public func pair(token: String) async throws -> String {
        struct Reply: Decodable { let code: String }
        let reply: Reply = try await call("pair", token: token, body: Data("{}".utf8))
        return reply.code
    }
    public func push(_ operation: NoteOperation, token: String) async throws -> OperationReceipt {
        try await call("operations", token: token, body: JSONEncoder().encode(operation))
    }
    public func changes(after: Int, token: String) async throws -> SyncPage {
        try await call("changes", token: token, query: [URLQueryItem(name: "after", value: String(after))])
    }
}

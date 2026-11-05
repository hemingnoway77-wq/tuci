import SwiftUI
import TuciCore

@MainActor
final class AppModel: ObservableObject {
    struct Connection: Codable {
        let server: String
        let vaultId: String
        let token: String
    }
    @Published private(set) var notes: [LibraryNote] = []
    @Published private(set) var pending = 0
    @Published private(set) var needsRecovery = false
    @Published private(set) var busy = false
    @Published private(set) var status = ""
    @Published private(set) var apiKey = ""
    @Published private(set) var connection: Connection?
    @Published var alert: String?
    @Published private(set) var startupIssue: String?
    private var book: Notebook?
    private var reviews = ReviewHistory()
#if TUCI_UI_TESTS
    private let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
#else
    private let directory = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("Tuci")
#endif
    var scrollPositions: [String: CGFloat] = [:]

    init() { load() }
    func load() {
        do {
            book = try Notebook(directory: directory)
            let file = directory.appendingPathComponent("roaming.json")
            if FileManager.default.fileExists(atPath: file.path) { reviews = try JSONDecoder().decode(ReviewHistory.self, from: Data(contentsOf: file)) }
#if TUCI_UI_TESTS
            if let book { try SimulatorFixture.seed(book) }
#else
            apiKey = try DeviceSecrets.get("ai").flatMap { String(data: $0, encoding: .utf8) } ?? ""
            connection = try DeviceSecrets.get("sync").map { try JSONDecoder().decode(Connection.self, from: $0) }
            if let connection { try book?.bind(to: identity(connection)) }
#endif
            startupIssue = nil
            reload()
            refreshLinks()
        } catch { startupIssue = error.localizedDescription }
    }
    private func identity(_ c: Connection) -> String { c.server + "|" + c.vaultId }
    private func reload() { notes = book?.notes ?? []; pending = book?.pendingCount ?? 0 }
    func saveAIKey(_ value: String) throws {
        try DeviceSecrets.set("ai", value: Data(value.utf8))
        apiKey = value
    }
    func save(_ path: String, _ markdown: String) throws {
        guard let book else { throw LibraryError.invalidResponse }
        try book.add(path: path, markdown: markdown)
        reload()
        // The new note is committed even if optional maintenance later fails.
        refreshLinks()
        Task { await synchronize() }
    }
    func refreshLinks() {
        do {
            let count = try book?.refreshLinks() ?? 0
            if count > 0 { reload() }
        } catch { alert = "笔记已保留，更新链接时遇到问题。\n" + error.localizedDescription }
    }
    func bind(server: String, code: String) async throws {
        guard let book, !busy else { throw LibraryError.invalidResponse }
        busy = true
        defer { busy = false }
        let api = try SyncAPI(server: server)
        let grant = try await api.redeem(code: code.trimmingCharacters(in: .whitespacesAndNewlines), name: "iPhone")
        let c = Connection(server: api.base.absoluteString.trimmingCharacters(in: CharacterSet(charactersIn: "/")),
                           vaultId: grant.vaultId, token: grant.token)
        try book.bind(to: identity(c))
        try DeviceSecrets.set("sync", value: JSONEncoder().encode(c))
        connection = c
    }
    func pair() async throws -> String {
        guard let c = connection else { throw LibraryError.denied }
        return try await SyncAPI(server: c.server).pair(token: c.token)
    }
    func recover() async throws {
        guard let book, !busy, needsRecovery else { return }
        try book.recoverAfterServerRestore()
        needsRecovery = false
        reload()
        await synchronize()
    }
    func synchronize() async {
        guard !busy, startupIssue == nil, let book, let c = connection else { return }
        busy = true
        defer { busy = false; reload() }
        do {
            let api = try SyncAPI(server: c.server)
            // Detect a restored database before any pending mutation can advance its old cursor.
            if book.cursor > 0 && book.nextOperation != nil { _ = try await api.changes(after: book.cursor, token: c.token) }
            var conflicts = 0
            while true {
                try Task.checkCancellation()
                while let operation = book.nextOperation {
                    let receipt = try await api.push(operation, token: c.token)
                    try book.acknowledge(operation, receipt)
                    if receipt.conflict { conflicts += 1 }
                }
                let page = try await api.changes(after: book.cursor, token: c.token)
                // A tap may have saved another note while the request was outstanding.
                if book.nextOperation != nil { continue }
                if !page.changes.isEmpty || page.hasMore { try book.apply(page) }
                if !page.hasMore { break }
            }
            status = conflicts > 0 ? "已同步，已保留 \(conflicts) 份冲突内容。" : "已同步 · " + Date.now.formatted(date: .omitted, time: .shortened)
        } catch LibraryError.restoredHistory {
            needsRecovery = true
            status = LibraryError.restoredHistory.localizedDescription
        } catch is CancellationError { }
        catch { if !Task.isCancelled { status = "待同步 · " + error.localizedDescription } }
    }
    struct Exposure: Identifiable {
        let id: UUID
        let note: LibraryNote
        let rememberedToday: Bool
    }
    func nextCard(finishing old: Exposure? = nil, remembered: Bool = false) -> Exposure? {
        var updated = reviews
        if let old {
            guard updated.currentExposure == old.id else { return nil }
            updated.finish(old.id, remembered: remembered)
        }
        guard let note = updated.draw(notes) else { return nil }
        let known = (updated.records[note.id]?.counts(on: .now, calendar: .current).0 ?? 0) > 0
        let exposure = updated.show(note.id)
        do {
            try JSONEncoder().encode(updated).write(to: directory.appendingPathComponent("roaming.json"), options: .atomic)
            reviews = updated
            return Exposure(id: exposure, note: note, rememberedToday: known)
        } catch { alert = error.localizedDescription; return nil }
    }
}

import Foundation

/// One atomic commit covers the visible content, outbox and server cursor.
/// A failed disk write never advances the in-memory cursor or drops a pending operation.
public final class Notebook {
    private struct State: Codable {
        var notes: [String: LibraryNote] = [:]
        var versions: [String: Int] = [:]
        var pending: [NoteOperation] = []
        var cursor = 0
        var binding: String?
        var regions: [String: [String: String]] = [:]
    }
    private let file: URL
    private var state: State
    public init(directory: URL) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        file = directory.appendingPathComponent("notebook-v1.json")
        if FileManager.default.fileExists(atPath: file.path) {
            state = try JSONDecoder().decode(State.self, from: Data(contentsOf: file))
        } else { state = State() }
    }
    public var notes: [LibraryNote] { state.notes.values.sorted { $0.path.localizedStandardCompare($1.path) == .orderedAscending } }
    public var nextOperation: NoteOperation? { state.pending.first }
    public var pendingCount: Int { state.pending.count }
    public var cursor: Int { state.cursor }
    public var binding: String? { state.binding }
    /// A restored server may have a shorter history. Keep an independent copy before
    /// republishing the visible notes as a first merge; never infer deletions from a rollback.
    public func recoverAfterServerRestore() throws {
        try FileManager.default.copyItem(at: file, to: file.deletingLastPathComponent()
            .appendingPathComponent("before-server-restore-" + UUID().uuidString + ".json"))
        try commit { next in
            next.cursor = 0
            next.versions.removeAll()
            next.pending = next.notes.values.sorted { $0.id < $1.id }.map { note in
                NoteOperation(operationId: UUID().uuidString, noteId: note.id, baseVersion: 0,
                              path: note.path, markdown: note.markdown, deleted: false, regions: next.regions[note.id])
            }
        }
    }
    public func bind(to identity: String) throws {
        guard !identity.isEmpty, state.binding == nil || state.binding == identity else { throw LibraryError.accountMismatch }
        if state.binding == nil { try commit { $0.binding = identity } }
    }
    private func commit(_ change: (inout State) throws -> Void) throws {
        var next = state
        try change(&next)
        try JSONEncoder().encode(next).write(to: file, options: .atomic)
        state = next
    }
    public func add(path: String, markdown: String, now: Date = Date()) throws {
        try validateNotePath(path)
        let note = LibraryNote(path: path, markdown: markdown, added: LibraryNote.creationDate(in: markdown) ?? now)
        guard !state.notes.values.contains(where: {
            $0.path.caseInsensitiveCompare(path) == .orderedSame ||
            (note.isWord && $0.isWord && $0.title.caseInsensitiveCompare(note.title) == .orderedSame)
        }) else { throw LibraryError.alreadyExists }
        let regions = ManagedSections.hashes(markdown)
        try commit {
            $0.notes[note.id] = note
            $0.pending.append(NoteOperation(operationId: UUID().uuidString, noteId: note.id, baseVersion: 0,
                                             path: path, markdown: markdown, deleted: false, regions: regions))
            $0.regions[note.id] = regions
        }
    }
    /// Only regions registered at original generation time may be changed.
    @discardableResult
    public func refreshLinks() throws -> Int {
        let notes = self.notes
        let index = ManagedSections.Index(notes)
        var changes: [(LibraryNote, [String: String])] = []
        for var note in notes {
            guard let regions = state.regions[note.id], !regions.isEmpty else { continue }
            let (updated, next) = ManagedSections.refresh(note, owned: regions, index: index)
            if updated != note.markdown {
                note.markdown = updated
                changes.append((note, next))
            }
        }
        guard !changes.isEmpty else { return 0 }
        try commit { state in
            for (note, regions) in changes {
                state.notes[note.id] = note
                state.regions[note.id] = regions
                state.pending.append(NoteOperation(operationId: UUID().uuidString, noteId: note.id,
                    baseVersion: state.versions[note.id] ?? 0, path: note.path, markdown: note.markdown,
                    deleted: false, regions: regions))
            }
        }
        return changes.count
    }

    public func acknowledge(_ operation: NoteOperation, _ receipt: OperationReceipt) throws {
        try validateNotePath(receipt.path)
        guard !receipt.noteId.isEmpty, receipt.version > 0,
              state.pending.first?.operationId == operation.operationId else { throw LibraryError.invalidResponse }
        try commit { state in
            state.pending.removeFirst()
            if var note = state.notes.removeValue(forKey: operation.noteId) {
                if receipt.noteId != operation.noteId { state.regions[receipt.noteId] = state.regions.removeValue(forKey: operation.noteId) }
                note.id = receipt.noteId
                note.path = receipt.path
                state.notes[note.id] = note
            }
            if operation.noteId != receipt.noteId { state.versions.removeValue(forKey: operation.noteId) }
            state.versions[receipt.noteId] = receipt.version
            if let next = state.pending.firstIndex(where: { $0.noteId == operation.noteId }) {
                // This operation has never left the FIFO outbox. Chain it to the accepted version.
                for i in next..<state.pending.count where state.pending[i].noteId == operation.noteId {
                    let old = state.pending[i]
                    state.pending[i] = NoteOperation(operationId: old.operationId, noteId: receipt.noteId,
                        baseVersion: i == next ? receipt.version : old.baseVersion, path: receipt.path,
                        markdown: old.markdown, deleted: old.deleted, regions: old.regions)
                }
            }
        }
    }
    public func apply(_ page: SyncPage, now: Date = Date()) throws {
        guard state.pending.isEmpty, page.cursor >= state.cursor, !page.hasMore || !page.changes.isEmpty else { throw LibraryError.invalidResponse }
        try commit { next in
            var seen = next.cursor
            for change in page.changes {
                guard change.seq > seen, change.seq <= page.cursor, change.version > 0, !change.noteId.isEmpty else { throw LibraryError.invalidResponse }
                seen = change.seq
                try validateNotePath(change.path)
                guard change.version >= (next.versions[change.noteId] ?? 0) else { continue }
                if change.deleted { next.notes.removeValue(forKey: change.noteId) }
                else {
                    let added = next.notes[change.noteId]?.added ?? LibraryNote.creationDate(in: change.markdown) ?? now
                    next.notes[change.noteId] = LibraryNote(path: change.path, markdown: change.markdown, id: change.noteId, added: added)
                }
                next.regions[change.noteId] = change.deleted ? nil : change.regions
                next.versions[change.noteId] = change.version
            }
            guard seen == page.cursor else { throw LibraryError.invalidResponse }
            next.cursor = page.cursor
        }
    }
}

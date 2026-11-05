import Foundation
import Testing
@testable import TuciCore

struct NotebookTests {
    private let stamp = Date(timeIntervalSince1970: 1_750_000_000)
    private func temporaryFolder() -> URL {
        FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    }

    @Test func validatesCompletePageBeforeCommittingCursor() throws {
        let dir = temporaryFolder()
        defer { try? FileManager.default.removeItem(at: dir) }
        let book = try Notebook(directory: dir)
        let first = SyncChange(seq: 1, noteId: "one", version: 1, path: "摘记/春.md", markdown: "个人笔记", deleted: false, regions: nil)
        let bad = SyncChange(seq: 2, noteId: "two", version: 1, path: "../escape.md", markdown: "", deleted: false, regions: nil)
        #expect(throws: LibraryError.self) { try book.apply(SyncPage(changes: [first, bad], cursor: 2, hasMore: false)) }
        #expect(book.notes.isEmpty && book.cursor == 0)
        try book.apply(SyncPage(changes: [first], cursor: 1, hasMore: false))
        let loaded = try Notebook(directory: dir)
        #expect(loaded.notes.first?.markdown == "个人笔记")
        #expect(loaded.cursor == 1)
        #expect(loaded.notes.first?.isWord == false)
    }
    @Test func retriesSameOperationAfterRelaunchAndProtectsOtherAccounts() throws {
        let dir = temporaryFolder()
        defer { try? FileManager.default.removeItem(at: dir) }
        let book = try Notebook(directory: dir)
        try book.bind(to: "server|A")
        try book.add(path: "动词/run.md", markdown: "my note")
        let restored = try Notebook(directory: dir)
        #expect(restored.nextOperation == book.nextOperation)
        #expect(throws: LibraryError.self) { try restored.bind(to: "server|B") }
        #expect(throws: LibraryError.self) { try restored.add(path: "名词/Run.md", markdown: "unwanted") }
        let op = try #require(restored.nextOperation)
        try restored.acknowledge(op, OperationReceipt(noteId: "canonical", version: 7, path: op.path, conflict: false))
        try restored.apply(SyncPage(changes: [.init(seq: 1, noteId: "canonical", version: 6, path: op.path, markdown: "stale", deleted: false, regions: nil)], cursor: 1, hasMore: false))
        #expect(restored.notes.first?.markdown == "my note")
    }
    @Test func restoresFromShorterServerHistoryWithoutInferringDeletes() throws {
        let dir = temporaryFolder()
        defer { try? FileManager.default.removeItem(at: dir) }
        let book = try Notebook(directory: dir)
        try book.bind(to: "A")
        try book.apply(SyncPage(changes: [
            SyncChange(seq: 6, noteId: "old", version: 2, path: "读书.md", markdown: "电脑笔记", deleted: false, regions: nil)
        ], cursor: 6, hasMore: false))
        try book.add(path: "离线.md", markdown: "手机笔记")
        try book.recoverAfterServerRestore()
        let loaded = try Notebook(directory: dir)
        #expect(loaded.binding == "A" && loaded.cursor == 0 && loaded.pendingCount == 2)
        #expect(loaded.nextOperation?.baseVersion == 0)
        #expect(Set(loaded.notes.map(\.markdown)) == ["电脑笔记", "手机笔记"])
        #expect(try FileManager.default.contentsOfDirectory(atPath: dir.path).contains { $0.hasPrefix("before-server-restore-") })
    }
    @Test func protectsHandEditedGeneratedSectionsDuringLinkRefresh() throws {
        let dir = temporaryFolder()
        defer { try? FileManager.default.removeItem(at: dir) }
        let book = try Notebook(directory: dir)
        let generated = "# visit\n\n#### 例句\n1. We visit the library daily.\n\n#### 我的笔记\nlibrary"
        let manual = generated.replacingOccurrences(of: "daily.", with: "on Fridays.")
        let owned = ManagedSections.hashes(generated)
        let updates = [
            SyncChange(seq: 1, noteId: "1", version: 1, path: "动词/visit.md", markdown: generated, deleted: false, regions: owned),
            SyncChange(seq: 2, noteId: "2", version: 1, path: "动词/trace.md", markdown: manual, deleted: false, regions: owned),
            SyncChange(seq: 3, noteId: "3", version: 1, path: "名词/library.md", markdown: "# library", deleted: false, regions: nil)]
        try book.apply(.init(changes: updates, cursor: 3, hasMore: false))
        #expect(try book.refreshLinks() == 1)
        #expect(book.notes.first { $0.id == "1" }?.markdown == generated.replacingOccurrences(of: "library daily", with: "[[名词/library|library]] daily"))
        #expect(book.notes.first { $0.id == "2" }?.markdown == manual)
        #expect(try book.refreshLinks() == 0)
    }
    @Test func roamingDoesNotCountPreviewOrDuplicateTaps() throws {
        var history = ReviewHistory()
        let now = stamp
        let first = history.show("one", now: now)
        history.finish(first, remembered: false, now: now)
        history.finish(first, remembered: false, now: now)
        let remembered = history.show("two", now: now)
        history.finish(remembered, remembered: true, now: now)
        let a = try #require(history.records["one"]), b = try #require(history.records["two"])
        #expect(a.skipped == 1 && b.remembered == 1)
        #expect(abs(a.weight(added: now, now: now) - b.weight(added: now, now: now) - 1) < 0.00001)
        #expect(b.weight(added: now, now: now) > 0)
        #expect(ReviewRecord().weight(added: now.addingTimeInterval(-86_400 * 30), now: now) > a.weight(added: now, now: now))
        #expect(a.counts(on: now.addingTimeInterval(86_400), calendar: .current).1 == 0)
        #expect(history.draw([.init(path: "词根/duc.md", markdown: "root")]) == nil)
    }
    @Test func readsWindowsFrontmatterAndPreservesAmbiguousLinks() {
        let raw = "---\r\ntype: english-word\r\ncreated: '2026-10-22T09:06:44.6864924+08:00'\r\n---\r\n# run\r\n\r\n**释义**：\r\n\r\n> v. 奔跑\r\n"
        let a = LibraryNote(path: "动词/run.md", markdown: raw)
        let b = LibraryNote(path: "名词/run.md", markdown: raw)
        #expect(!a.body.contains("created:") && a.meaning == "v. 奔跑")
        #expect(LibraryNote.creationDate(in: raw) != nil)
        #expect(LibraryNote.targets("run", from: a, among: [a,b]).count == 2)
        let namesake = LibraryNote(path: "run.md", markdown: "other")
        #expect(LibraryNote.targets("missing/folder/run", from: a, among: [namesake]).isEmpty)
        #expect(LibraryNote.targets("书架/英语/名词/run", from: a, among: [a,b], vaultPrefix: "书架/英语") == [b])
        #expect(LibraryNote.targets("英语单词/名词/run|run", from: a, among: [a,b]) == [b])
    }
}

import Foundation
import Testing
@testable import TuciCore

struct ServerRoundTripTests {
    private struct Fixture: Decodable {
        let server: String
        let code: String
        let vaultId: String
        let remoteMarkdown: String
    }

    /// Run via server/tests/ios_roundtrip.py against its own loopback server and private temp DB.
    @Test(.enabled(if: ProcessInfo.processInfo.environment["TUCI_IOS_ROUNDTRIP"] != nil))
    func recoverLostReplyAndMergeConflictingOfflineNote() async throws {
        let fixturePath = try #require(ProcessInfo.processInfo.environment["TUCI_IOS_ROUNDTRIP"])
        let fixture = try JSONDecoder().decode(Fixture.self, from: Data(contentsOf: URL(fileURLWithPath: fixturePath)))
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let path = "动词/introduced.md"
        let localBody = "# introduced\n\n我在手机离线时写的原句。\n"

        // Capture before pairing, exactly as a user might before initial setup.
        do {
            let offline = try Notebook(directory: directory)
            try offline.add(path: path, markdown: localBody)
        }

        let api = try SyncAPI(server: fixture.server)
        let grant = try await api.redeem(code: fixture.code, name: "Swift round-trip")
        #expect(grant.vaultId == fixture.vaultId)
        let identity = api.base.absoluteString + "|" + grant.vaultId

        let firstOperationID: String
        let firstServerNoteID: String
        do {
            let reopened = try Notebook(directory: directory)
            try reopened.bind(to: identity)
            let op = try #require(reopened.nextOperation)
            let receipt = try await api.push(op, token: grant.token)
            #expect(receipt.conflict)
            firstOperationID = op.operationId
            firstServerNoteID = receipt.noteId
            // Simulate termination after the server committed, before the client received its receipt.
        }

        // A new credential for the SAME vault must keep the pending outbox and cursor.
        let pairCode = try await api.pair(token: grant.token)
        let replacement = try await api.redeem(code: pairCode, name: "Swift paired")
        #expect(replacement.vaultId == grant.vaultId)
        let library = try Notebook(directory: directory)
        try library.bind(to: api.base.absoluteString + "|" + replacement.vaultId)
        let retry = try #require(library.nextOperation)
        #expect(retry.operationId == firstOperationID)
        let receipt = try await api.push(retry, token: replacement.token)
        #expect(receipt.noteId == firstServerNoteID)
        try library.acknowledge(retry, receipt)
        repeat {
            let page = try await api.changes(after: library.cursor, token: replacement.token)
            try library.apply(page)
            if !page.hasMore { break }
        } while true

        let saved = try Notebook(directory: directory)
        #expect(saved.pendingCount == 0)
        #expect(saved.notes.count == 2)
        #expect(saved.notes.first(where: { $0.path == path })?.markdown == fixture.remoteMarkdown)
        #expect(saved.notes.first(where: { $0.path == receipt.path })?.markdown == localBody)
        #expect(receipt.path != path)
        #expect(saved.cursor > 0)
        let next = try await api.changes(after: saved.cursor, token: grant.token)
        #expect(next.changes.isEmpty && next.cursor == saved.cursor)
    }
}

import XCTest

final class NotebookGestureTests: XCTestCase {
    @MainActor private lazy var app = XCUIApplication()
    @MainActor private var reader: XCUIElement { app.textViews["note.body"] }

    @MainActor private func launchApp() {
        continueAfterFailure = false
        app.launch()
    }

    // Enter through the same directory and navigation stack used in the shipping app.
    @MainActor private func openPassage() {
        launchApp()
        app.tabBars.buttons["词库"].tap()
        app.buttons["passage"].tap()
        XCTAssertTrue(app.navigationBars["passage"].waitForExistence(timeout: 10))
    }

    private struct Probe {
        let offset, x, y: CGFloat
        var hasLink: Bool { x > 0 && y > 0 }
        init?(_ encoded: Any?) {
            guard let encoded = encoded as? String else { return nil }
            let parts = encoded.components(separatedBy: "|").compactMap(Double.init)
            guard parts.count == 3 else { return nil }
            offset = CGFloat(parts[0]); x = CGFloat(parts[1]); y = CGFloat(parts[2])
        }
    }

    @MainActor private func waitForProbe(_ valid: @escaping (Probe) -> Bool) throws -> Probe {
        var snapshot: Probe?
        let condition = NSPredicate { _, _ in
            guard self.reader.exists, let result = Probe(self.reader.value), valid(result) else { return false }
            snapshot = result
            return true
        }
        let outcome = XCTWaiter.wait(for: [XCTNSPredicateExpectation(predicate: condition, object: nil)], timeout: 10)
        XCTAssertEqual(outcome, .completed, "没有找到已布局的词条或阅读位置")
        return try XCTUnwrap(snapshot)
    }

    @MainActor private func link(at probe: Probe) -> XCUICoordinate {
        reader.coordinate(withNormalizedOffset: .zero).withOffset(CGVector(dx: probe.x, dy: probe.y))
    }

    @MainActor func testCollectingAlreadySavedWordOpensExistingNote() {
        launchApp()
        let input = app.textFields["输入或粘贴单词"]
        input.tap()
        input.typeText("POLICY")
        let submit = app.buttons["解析并保存"]
        if !submit.isHittable { app.scrollViews.firstMatch.swipeUp() }
        submit.tap()
        XCTAssertTrue(app.staticTexts["已收录，可直接打开已有笔记。"].waitForExistence(timeout: 5))
        app.buttons.matching(NSPredicate(format: "label CONTAINS[cd] %@", "policy")).firstMatch.tap()
        XCTAssertTrue(app.navigationBars["policy"].waitForExistence(timeout: 5))
    }

    @MainActor func testSingleTapShowsPreviewAndOpensTarget() throws {
        openPassage()
        link(at: try waitForProbe(\.hasLink)).tap()
        let open = app.buttons["打开笔记"]
        XCTAssertTrue(open.waitForExistence(timeout: 5))
        XCTAssertTrue(app.staticTexts.containing(NSPredicate(format: "label CONTAINS %@", "政策")).firstMatch.exists)
        open.tap()
        XCTAssertTrue(app.navigationBars["policy"].waitForExistence(timeout: 5))
    }

    @MainActor func testDoubleTapNavigatesDirectlyAndBackRestoresReadingPosition() throws {
        openPassage()
        reader.swipeUp()
        // Wait for inertial scrolling to settle before recording the return location.
        var previous: CGFloat?
        let before = try waitForProbe { probe in
            defer { previous = probe.offset }
            return probe.offset > 80 && probe.hasLink && previous.map { abs($0 - probe.offset) < 1 } == true
        }
        link(at: before).doubleTap()
        XCTAssertTrue(app.navigationBars["policy"].waitForExistence(timeout: 5))
        XCTAssertFalse(app.buttons["打开笔记"].exists)
        app.navigationBars["policy"].buttons.element(boundBy: 0).tap()
        XCTAssertTrue(app.navigationBars["passage"].waitForExistence(timeout: 5))
        _ = try waitForProbe { abs($0.offset - before.offset) < 4 && $0.hasLink }
    }
}

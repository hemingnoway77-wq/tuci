#if TUCI_UI_TESTS
import UIKit
import TuciCore

/// Compiled only for the simulator smoke host, which has its own bundle ID.
enum SimulatorFixture {
    static func seed(_ notebook: Notebook) throws {
        guard notebook.notes.isEmpty else { return }
        try notebook.add(path: "policy.md", markdown: """
        # policy

        **发音**：/ˈpɒləsi/

        **释义**：

        > n. 政策。

        #### 我的笔记
        """)
        let paragraph = """
        Today's reading considers a public [[policy|policy]] and its effect on daily life.
        Different plans can produce different results for the same community.
        """
        let passage = (1...40).map { "#### Paragraph \($0)\n\n" + paragraph }.joined(separator: "\n\n")
        try notebook.add(path: "passage.md", markdown: "# passage\n\n" + passage)
    }

    /// XCTest cannot address the individual glyphs of a custom inline link. Give
    /// it the on-screen midpoint of one character using UITextInput's selection
    /// geometry. The test still sends real touchscreen taps through UIKit.
    static func probe(_ view: NoteTextView) -> String {
        var point: CGPoint?
        let visible = view.bounds.insetBy(dx: 8, dy: 8)
        view.attributedText.enumerateAttribute(NoteMarkup.attribute, in: NSRange(location: 0, length: view.attributedText.length)) { target, range, stop in
            guard target as? String == "policy|policy",
                  let start = view.position(from: view.beginningOfDocument, offset: range.location),
                  let end = view.position(from: start, offset: 1),
                  let textRange = view.textRange(from: start, to: end) else { return }
            let rect = view.firstRect(for: textRange)
            guard !rect.isNull && rect.width > 0 && visible.contains(rect) else { return }
            point = CGPoint(x: rect.midX - view.bounds.minX, y: rect.midY - view.bounds.minY)
            stop.pointee = true
        }
        return [view.contentOffset.y, point?.x ?? -1, point?.y ?? -1]
            .map { String(Double($0)) }.joined(separator: "|")
    }
}
#endif

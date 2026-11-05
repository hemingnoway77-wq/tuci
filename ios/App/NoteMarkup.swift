import SwiftUI
import UIKit
import TuciCore

enum NoteMarkup {
    static let wiki = try! NSRegularExpression(pattern: #"\[\[([^\[\]\r\n]+)\]\]"#)
    static let attribute = NSAttributedString.Key("TuciWiki")
    static func links(in note: LibraryNote) -> [String] {
        let text = render(note, tint: .label)
        var links: [String] = []
        var seen = Set<String>()
        text.enumerateAttribute(attribute, in: NSRange(location: 0, length: text.length)) { value, _, _ in
            if let target = value as? String, seen.insert(target).inserted { links.append(target) }
        }
        return links
    }
    static func excerpt(_ note: LibraryNote) -> String {
        String(note.body.components(separatedBy: "\n").filter { !$0.isEmpty && !$0.hasPrefix("#") && $0 != "---" }
            .prefix(4).joined(separator: "\n").prefix(340))
    }
    static func render(_ note: LibraryNote, tint: UIColor) -> NSAttributedString {
        let out = NSMutableAttributedString()
        var comment = false, codeFence: String?
        for raw in note.body.components(separatedBy: "\n") {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if comment { if line.contains("-->") { comment = false }; continue }
            if line.hasPrefix("<!--") { comment = !line.contains("-->"); continue }
            if line.hasPrefix("~~~") || line.hasPrefix("\u{60}\u{60}\u{60}") {
                if codeFence == nil { codeFence = String(line.prefix(3)) }
                else if line.hasPrefix(codeFence!) { codeFence = nil }
                continue
            }
            if line == "---" || line.isEmpty { continue }
            let hashes = line.prefix(while: { $0 == "#" }).count
            let isHeading = codeFence == nil && (1...6).contains(hashes) && line.dropFirst(hashes).first == " "
            let quote = codeFence == nil && line.hasPrefix(">")
            let visible: String
            if isHeading { visible = String(line.dropFirst(hashes + 1)) }
            else if quote { visible = String(line.dropFirst()).trimmingCharacters(in: .whitespaces) }
            else if line.hasPrefix("- ") { visible = "•  " + String(line.dropFirst(2)) }
            else { visible = raw }
            let style: UIFont.TextStyle = isHeading ? (hashes == 1 ? .largeTitle : .headline) : .body
            let base = UIFont.preferredFont(forTextStyle: style)
            let descriptor = hashes == 1 && isHeading ? base.fontDescriptor.withDesign(.serif) : nil
            let font = codeFence != nil ? UIFont.monospacedSystemFont(ofSize: base.pointSize, weight: .regular)
                : descriptor.map { UIFont(descriptor: $0, size: 0) } ?? base
            let paragraph = NSMutableParagraphStyle()
            paragraph.lineSpacing = 4
            paragraph.paragraphSpacing = isHeading ? 8 : 6
            paragraph.paragraphSpacingBefore = isHeading ? 12 : 0
            paragraph.headIndent = quote ? 12 : 0
            paragraph.firstLineHeadIndent = paragraph.headIndent
            let attributes: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: UIColor.label, .paragraphStyle: paragraph]
            func appendPlain(_ text: String) {
                guard codeFence == nil, !text.isEmpty else { out.append(NSAttributedString(string: text, attributes: attributes)); return }
                let inline = (try? AttributedString(markdown: text, options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace)))
                    .map(NSAttributedString.init) ?? NSAttributedString(string: text)
                let styled = NSMutableAttributedString(attributedString: inline)
                styled.addAttributes(attributes, range: NSRange(location: 0, length: styled.length))
                inline.enumerateAttribute(.inlinePresentationIntent, in: NSRange(location: 0, length: inline.length)) { value, range, _ in
                    if let value = value as? NSNumber, InlinePresentationIntent(rawValue: value.uintValue).contains(.stronglyEmphasized),
                       let bold = font.fontDescriptor.withSymbolicTraits(.traitBold) {
                        styled.addAttribute(.font, value: UIFont(descriptor: bold, size: font.pointSize), range: range)
                    }
                }
                out.append(styled)
            }
            let text = visible as NSString
            var cursor = 0
            let matches = codeFence == nil ? wiki.matches(in: visible, range: NSRange(location: 0, length: text.length)) : []
            for match in matches {
                appendPlain(text.substring(with: NSRange(location: cursor, length: match.range.location - cursor)))
                let target = text.substring(with: match.range(at: 1))
                let alias = target.components(separatedBy: "|").last ?? target
                let display = target.contains("|") ? alias : (alias as NSString).lastPathComponent
                var linked = attributes
                linked[attribute] = target
                linked[.foregroundColor] = tint
                linked[.underlineStyle] = NSUnderlineStyle.single.rawValue
                out.append(NSAttributedString(string: display, attributes: linked))
                cursor = NSMaxRange(match.range)
            }
            appendPlain(text.substring(from: cursor))
            out.append(NSAttributedString(string: "\n", attributes: attributes))
        }
        return out
    }
}

/// UIKit can receive a note before the text view has its final width. Applying
/// the offset in updateUIView would clamp a saved position to a zero-height layout.
final class NoteTextView: UITextView {
#if TUCI_UI_TESTS
    override var accessibilityValue: String? {
        get { SimulatorFixture.probe(self) }
        set { super.accessibilityValue = newValue }
    }
#endif
    private(set) var pendingOffset: CGFloat?
    private var restoring = false

    func show(_ text: NSAttributedString, restoring offset: CGFloat) {
        pendingOffset = offset
        attributedText = text
        setNeedsLayout()
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        guard !restoring, let target = pendingOffset, window != nil,
              bounds.width > 0, bounds.height > 0, textContainer.size.width > 0 else { return }
        restoring = true
        layoutManager.ensureLayout(for: textContainer)
        let lower = -adjustedContentInset.top
        let upper = max(lower, contentSize.height - bounds.height + adjustedContentInset.bottom)
        setContentOffset(CGPoint(x: contentOffset.x, y: min(max(lower, target), upper)), animated: false)
        pendingOffset = nil
        restoring = false
    }

    func wikiTarget(at location: CGPoint) -> String? {
        let point = CGPoint(x: location.x - textContainerInset.left, y: location.y - textContainerInset.top)
        let glyph = layoutManager.glyphIndex(for: point, in: textContainer)
        guard glyph < layoutManager.numberOfGlyphs,
              layoutManager.boundingRect(forGlyphRange: NSRange(location: glyph, length: 1), in: textContainer).contains(point)
        else { return nil }
        let index = layoutManager.characterIndexForGlyph(at: glyph)
        guard index < attributedText.length else { return nil }
        return attributedText.attribute(NoteMarkup.attribute, at: index, effectiveRange: nil) as? String
    }
}

final class WikiTapRecognizer: UITapGestureRecognizer {
    var firstTarget: String?
    override func reset() {
        super.reset()
        firstTarget = nil
    }
}

struct NativeNoteText: UIViewRepresentable {
    let note: LibraryNote
    let offset: CGFloat
    let position: (CGFloat) -> Void
    let link: (String, Bool) -> Void
    @Environment(\.dynamicTypeSize) private var size
    @Environment(\.colorScheme) private var scheme
    @AppStorage("theme") private var theme: PaperTheme = .warm

    func makeCoordinator() -> Coordinator { Coordinator(self) }
    func makeUIView(context: Context) -> NoteTextView {
        let view = NoteTextView(usingTextLayoutManager: false)
        view.isEditable = false
#if TUCI_UI_TESTS
        view.accessibilityIdentifier = "note.body"
        view.isAccessibilityElement = true
#endif
        view.backgroundColor = .clear
        view.textContainerInset = UIEdgeInsets(top: 14, left: 18, bottom: 32, right: 18)
        view.adjustsFontForContentSizeCategory = true
        view.delegate = context.coordinator
        let single = WikiTapRecognizer(target: context.coordinator, action: #selector(Coordinator.tap(_:)))
        let double = WikiTapRecognizer(target: context.coordinator, action: #selector(Coordinator.tap(_:)))
        double.numberOfTapsRequired = 2
        single.require(toFail: double)
        for recognizer in [single, double] { recognizer.delegate = context.coordinator; view.addGestureRecognizer(recognizer) }
        return view
    }
    func updateUIView(_ view: NoteTextView, context: Context) {
        context.coordinator.parent = self
        let signature = "\(note.id)/\(note.markdown.hashValue)/\(size)/\(scheme)/\(theme.rawValue)"
        if context.coordinator.signature != signature {
            context.coordinator.signature = signature
            view.show(NoteMarkup.render(note, tint: UIColor(theme.tint)), restoring: offset)
        }
    }
    final class Coordinator: NSObject, UITextViewDelegate, UIGestureRecognizerDelegate {
        var parent: NativeNoteText
        var signature = ""
        init(_ parent: NativeNoteText) { self.parent = parent }
        func scrollViewDidScroll(_ scrollView: UIScrollView) {
            guard let text = scrollView as? NoteTextView, text.window != nil, text.pendingOffset == nil else { return }
            parent.position(text.contentOffset.y)
        }

        func gestureRecognizer(_ recognizer: UIGestureRecognizer, shouldReceive touch: UITouch) -> Bool {
            guard let tap = recognizer as? WikiTapRecognizer, let view = tap.view as? NoteTextView,
                  let target = view.wikiTarget(at: touch.location(in: view)) else { return false }
            if let first = tap.firstTarget { return first == target }
            tap.firstTarget = target
            return true
        }

        func gestureRecognizer(_ recognizer: UIGestureRecognizer, shouldBeRequiredToFailBy other: UIGestureRecognizer) -> Bool {
            // For linked text the built-in selection taps wait for our recognizers.
            // Outside links our recognizers do not receive the touch at all.
            guard let view = recognizer.view as? NoteTextView, !(other is WikiTapRecognizer),
                  let otherView = other.view, otherView.isDescendant(of: view) else { return false }
            if other is UITapGestureRecognizer { return true }
            return view.interactions.compactMap { $0 as? UITextInteraction }
                .contains { $0.gesturesForFailureRequirements.contains(other) }
        }

        @objc func tap(_ sender: WikiTapRecognizer) {
            guard let view = sender.view as? NoteTextView,
                  let key = view.wikiTarget(at: sender.location(in: view)), key == sender.firstTarget else { return }
            parent.link(key, sender.numberOfTapsRequired == 2)
        }
    }
}

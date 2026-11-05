import CryptoKit
import Foundation

/// Mirrors the four generated Markdown regions registered by Windows. Hashes are
/// evidence of authorship; receiving a note never grants ownership of its body.
enum ManagedSections {
    private static func digest(_ value: String) -> String {
        var normalized = value.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
        while normalized.last == "\n" { normalized.removeLast() }
        return SHA256.hash(data: Data(normalized.utf8)).map { String(format: "%02x", $0) }.joined()
    }

    private static func ranges(_ text: String) -> [String: Range<String.Index>] {
        var result: [String: Range<String.Index>] = [:]
        for (key, pattern) in [
            ("examples", #"(?ms)^#### 例句[ \t]*\r?\n.*?(?=^#{1,4}[ \t]|\z)"#),
            ("relations", #"(?ms)^#### 语义关系[ \t]*\r?\n.*?(?=^#{1,4}[ \t]|\z)"#),
            ("forms", #"(?ms)^\*\*词形\*\*[：:].*?(?=^[ \t]*\r?$|^#{1,6}[ \t]|\z)"#),
            ("roots", #"(?ms)^\*\*词根\*\*[：:].*?(?=^[ \t]*\r?$|^#{1,6}[ \t]|\z)"#)
        ] {
            let matches = try! NSRegularExpression(pattern: pattern).matches(in: text, range: NSRange(text.startIndex..., in: text))
            if matches.count == 1 { result[key] = Range(matches[0].range, in: text) }
        }
        return result
    }
    static func hashes(_ text: String) -> [String: String] { ranges(text).mapValues { digest(String(text[$0])) } }

    struct Index {
        let words: [String: [LibraryNote]]
        let roots: [String: [LibraryNote]]
        init(_ notes: [LibraryNote]) {
            words = Dictionary(grouping: notes.filter(\.isWord), by: { $0.title.lowercased() })
            roots = Dictionary(grouping: notes.filter { $0.path.hasPrefix("词根/") },
                               by: { $0.title.lowercased().trimmingCharacters(in: CharacterSet(charactersIn: "-")) })
        }
    }
    private static let stop = Set("a an the and or but so because if when while as of in on at to for from by with without into over under about is am are was were be been being do does did have has had can could may might must will would shall should i me my mine we us our ours you your yours he him his she her hers it its they them their theirs this that these those not no yes very too also just then than there here who what which where why how all any each every some such own same up down out off again only even still more most much many few less other another both either neither".split(separator: " ").map(String.init))

    static func refresh(_ note: LibraryNote, owned: [String: String], index: Index) -> (String, [String: String]) {
        func wiki(_ target: LibraryNote, _ display: String) -> String {
            guard target.path.rangeOfCharacter(from: CharacterSet(charactersIn: "#^[]|")) == nil else { return display }
            return "[[" + String(target.path.dropLast(3)) + "|" + display + "]]"
        }
        func replace(_ line: String, pattern: String, transform: (String, Int) -> String) -> String {
            let regex = try! NSRegularExpression(pattern: pattern)
            var output = "", last = line.startIndex
            for match in regex.matches(in: line, range: NSRange(line.startIndex..., in: line)) {
                guard let range = Range(match.range, in: line) else { continue }
                output += line[last..<range.lowerBound] + transform(String(line[range]), match.range.location)
                last = range.upperBound
            }
            return output + line[last...]
        }
        func rewrite(_ line: String, mode: String) -> String {
            let isExample = line.range(of: #"^\s*\d+\.\s+"#, options: .regularExpression) != nil
            let relation = line.range(of: #"^\s*-\s+"#, options: .regularExpression)
            guard mode == "roots" || mode == "forms" || (mode == "examples" && isExample) || (mode == "relations" && relation != nil) else { return line }
            var previous: [String: (path: String, link: String)] = [:]
            func linked(_ target: LibraryNote, _ display: String) -> String {
                let path = String(target.path.dropLast(3)).lowercased()
                if let saved = previous[display], saved.path == path || saved.path.hasSuffix("/" + path) {
                    return saved.link
                }
                return wiki(target, display)
            }
            // Strip only app-generated word links whose filename matches the alias.
            let plain = replace(line, pattern: #"\[\[[^\[\]\n|]+\|[A-Za-z][A-Za-z'’/ -]*\]\]"#) { link, _ in
                let parts = String(link.dropFirst(2).dropLast(2)).components(separatedBy: "|")
                let stem = (parts[0] as NSString).lastPathComponent.trimmingCharacters(in: CharacterSet(charactersIn: "_-"))
                guard parts[1].components(separatedBy: "/").contains(where: {
                    $0.trimmingCharacters(in: .whitespaces).caseInsensitiveCompare(stem) == .orderedSame
                }) else { return link }
                previous[parts[1]] = (parts[0].lowercased(), link)
                return parts[1]
            }
            if mode == "roots" {
                return replace(plain, pattern: #"(?<=[：:；])[ \t]*[A-Za-z]+(?:\s*/\s*[A-Za-z]+)*(?=（)"#) { match, _ in
                    let label = match.trimmingCharacters(in: .whitespaces)
                    let ids = Set(label.lowercased().components(separatedBy: "/").flatMap { index.roots[$0.trimmingCharacters(in: .whitespaces)] ?? [] })
                    return String(match.prefix(while: { $0.isWhitespace })) + (ids.count == 1 ? linked(ids.first!, label) : label)
                }
            }
            let tokens = #"\[\[[^\n]*?\]\]|\[[^\n]*?\]\([^\n]*?\)|\x60[^\x60\n]*\x60|https?://\S+|(?<![A-Za-z0-9_])[A-Za-z]+(?:[-'’][A-Za-z]+)*(?![A-Za-z0-9_])"#
            var used = Set<String>()
            return replace(plain, pattern: tokens) { token, pos in
                let key = token.lowercased().replacingOccurrences(of: "’", with: "'")
                guard key != note.title.lowercased(), let group = index.words[key], group.count == 1 else { return token }
                if mode == "examples" && (stop.contains(key) || used.count >= 3 || !used.insert(key).inserted) { return token }
                if mode == "relations" {
                    guard let relation, pos == NSRange(relation, in: line).length else { return token }
                    let tail = (plain as NSString).substring(from: pos + token.utf16.count).trimmingCharacters(in: .whitespaces)
                    guard tail.hasPrefix("：") || tail.hasPrefix(":") else { return token }
                }
                return linked(group[0], token)
            }
        }
        var result = note.markdown, next = owned
        let sections = ranges(note.markdown).sorted { $0.value.lowerBound > $1.value.lowerBound }
        // Use UTF-16 offsets from the original text; later replacements do not move earlier regions.
        let buffer = NSMutableString(string: result)
        for (mode, range) in sections {
            let old = String(note.markdown[range])
            guard owned[mode]?.lowercased() == digest(old) else { continue }
            let updated = old.components(separatedBy: "\n").map { rewrite($0, mode: mode) }.joined(separator: "\n")
            if updated != old {
                buffer.replaceCharacters(in: NSRange(range, in: note.markdown), with: updated)
                next[mode] = digest(updated)
            }
        }
        result = buffer as String
        return (result, next)
    }
}

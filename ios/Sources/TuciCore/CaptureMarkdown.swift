import Foundation

public struct CapturedCard: Sendable {
    public let word: String
    public let originalInput: String
    public let path: String
    public let markdown: String
}

enum CaptureMarkdown {
    static func render(_ analysis: WordAnalysis, word: String, input: String, context: String,
                       classify: Bool, notes: [LibraryNote], date: Date) throws -> CapturedCard {
        guard let part = analysis.primaryPos, let meanings = analysis.meanings, let examples = analysis.examples,
              let collocations = analysis.collocations, let phonetics = analysis.phonetics else {
            throw CaptureFailure("AI 返回的词卡缺少必要字段。")
        }
        let spellingForms = CapturedWord.exampleForms(word: word, lemma: analysis.lemma, verb: meanings.contains { $0.partOfSpeech == .verb })
        func exampleText(_ source: String) -> String {
            let text = source as NSString
            var offset = 0, output = ""
            for match in CapturedWord.token.matches(in: source, range: NSRange(location: 0, length: text.length)) {
                let token = text.substring(with: match.range)
                guard spellingForms.contains(CapturedWord.canonical(token)) else { continue }
                output += plain(text.substring(with: NSRange(location: offset, length: match.range.location - offset)))
                output += "**" + plain(token) + "**"
                offset = NSMaxRange(match.range)
            }
            return output + plain(text.substring(from: offset))
        }
        var sections = ["---", "type: english-word", "word: \"\(word)\""]
        if word != input { sections.append("original_input: \"\(input)\"") }
        if let base = analysis.lemma { sections.append("lemma: \"\(try CapturedWord.normalize(base))\"") }
        sections += ["primary_pos: \(part.rawValue)", "parts_of_speech:"]
        var written = Set<String>()
        for value in meanings where written.insert(value.partOfSpeech.rawValue).inserted {
            sections.append("  - " + value.partOfSpeech.rawValue)
        }
        sections += ["created: '\(ISO8601DateFormatter().string(from: date))'", "tags:\n  - english\n  - vocabulary\n---", "", "# " + word, "---", ""]
        if word != input { sections += ["**拼写修正**：\(plain(input)) → \(plain(word))", ""] }
        func ipa(_ value: String) -> String {
            let text = value.trimmingCharacters(in: .whitespacesAndNewlines.union(CharacterSet(charactersIn: "/")))
            return text.isEmpty ? "未提供" : "/" + plain(text) + "/"
        }
        sections += ["**发音**：UK " + ipa(phonetics.uk) + " · US " + ipa(phonetics.us), ""]
        let roots = (analysis.roots ?? []).map { root in
            var visited = Set<String>()
            let terms = ([root.root] + root.variants).filter { visited.insert($0.lowercased()).inserted }
            return plain(terms.joined(separator: " / ")) + "（" + plain(root.meaning) + "）"
        }
        sections += ["**词根**：" + (roots.isEmpty ? "暂无可靠词根信息。" : roots.joined(separator: "；")), "", "**释义**：", ""]
        sections.append(meanings.map { "> " + $0.partOfSpeech.labels.abbreviation + " " + plain($0.definition) }.joined(separator: "  \n"))
        if let form = analysis.formNote, !form.isEmpty { sections += ["", "**词形**：" + plain(form)] }
        sections += ["", "#### 例句"]
        for (i, pair) in examples.enumerated() {
            sections += ["\(i+1). " + exampleText(pair.english), "   - " + plain(pair.chinese)]
        }
        sections += ["", "#### 常见搭配"] + collocations.map { "- " + plain($0.english) + "：" + plain($0.chinese) }
        sections += ["", "#### 语义关系"]
        for (kind, heading, empty) in [("synonym","近义词","暂无可靠的常用近义词。"),("antonym","反义词","无常用直接反义词。"),("same_root","同根词",""),("confusable","易混词","")] {
            var seen = Set<String>()
            let relations = (analysis.related ?? []).filter { $0.relation == kind }.compactMap { item -> String? in
                guard let term = try? CapturedWord.normalize(item.word), seen.insert(term).inserted else { return nil }
                return "- " + plain(term) + "：" + plain(item.explanation.flatMap { $0.isEmpty ? nil : $0 } ?? "暂未提供用法说明。")
            }
            if !relations.isEmpty || !empty.isEmpty {
                sections += ["##### " + heading] + (relations.isEmpty ? ["- " + empty] : Array(relations.prefix(4)))
            }
        }
        sections += ["", "#### 语法信息", "##### 句法模式"]
        if (analysis.grammar ?? []).isEmpty { sections.append("- 暂未提供可靠的句法说明。") }
        for item in analysis.grammar ?? [] { sections += ["- " + plain(item.pattern), "  - " + plain(item.explanation)] }
        if !context.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            sections += ["", "#### 遇见时的原句"] + context.components(separatedBy: .newlines).map { "> " + plain($0) }
        }
        sections += ["", "#### 我的笔记", ""]
        let stem = CapturedWord.reserved.contains(word) ? "_" + word : word
        let note = LibraryNote(path: (classify ? part.labels.folder + "/" : "") + stem + ".md",
                               markdown: sections.joined(separator: "\n") + "\n")
        let (linked, _) = ManagedSections.refresh(note, owned: ManagedSections.hashes(note.markdown), index: ManagedSections.Index(notes))
        return CapturedCard(word: word, originalInput: input, path: note.path, markdown: linked)
    }

    private static func plain(_ value: String) -> String {
        var result = ""
        for character in value {
            switch character {
            case "&": result += "&amp;"
            case "<": result += "&lt;"
            case ">": result += "&gt;"
            case "[": result += "&#91;"
            case "]": result += "&#93;"
            case "\\","*","_","#","|", "\u{60}": result += "\\" + String(character)
            default: result += character.isWhitespace ? " " : String(character)
            }
        }
        return result
    }
}

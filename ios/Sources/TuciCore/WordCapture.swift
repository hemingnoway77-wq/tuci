import Foundation

public struct AIConfiguration: Sendable {
    public var baseURL: String
    public var apiKey: String
    public var model: String
    public init(baseURL: String, apiKey: String, model: String) {
        self.baseURL = baseURL
        self.apiKey = apiKey
        self.model = model
    }
}

public struct CaptureFailure: LocalizedError {
    let reason: String
    public var errorDescription: String? { reason }
    init(_ reason: String) { self.reason = reason }
}

public enum CapturedWord {
    static let token = try! NSRegularExpression(pattern: "[A-Za-z0-9]+(?:[-'‘’][A-Za-z0-9]+)*")

    public static func normalize(_ text: String) throws -> String {
        let value = canonical(text.trimmingCharacters(in: .whitespacesAndNewlines))
        guard value.utf8.count <= 64,
              value.range(of: #"\A[a-z]+(?:[-'][a-z]+)*\z"#, options: .regularExpression) != nil else {
            throw CaptureFailure("请输入一个不超过 64 个字符的英文单词，可使用内部连字符或撇号。")
        }
        return value
    }

    static func canonical(_ text: String) -> String {
        text.replacingOccurrences(of: "‘", with: "'").replacingOccurrences(of: "’", with: "'").lowercased()
    }

    public static func matches(_ word: String, notes: [LibraryNote]) -> [LibraryNote] {
        notes.filter { note in
            guard note.isWord else { return false }
            let stem = ((note.path as NSString).lastPathComponent as NSString).deletingPathExtension
            return canonical(stem) == word || (reserved.contains(word) && canonical(stem) == "_" + word)
        }.sorted { $0.path < $1.path }
    }

    static let reserved: Set<String> = ["con", "prn", "aux", "nul"]

    static func exampleForms(word: String, lemma: String?, verb: Bool) -> Set<String> {
        guard verb, word == lemma, word.allSatisfy({ $0 >= "a" && $0 <= "z" }),
              !["be", "have"].contains(word), !word.hasSuffix("o"),
              !word.hasSuffix("z") || word.hasSuffix("zz") else { return [word] }
        var third = word + "s"
        if word.count > 1 && word.hasSuffix("y") && !"aeiou".contains(word.dropLast().last!) {
            third = String(word.dropLast()) + "ies"
        } else if ["s", "x", "z", "ch", "sh"].contains(where: { word.hasSuffix($0) }) {
            third = word + "es"
        }
        return [word, third]
    }

    static func isInflection(_ input: String, of lemma: String) -> Bool {
        if ["s","es","ed","d","ing","er","est"].contains(where: { input == lemma + $0 }) { return true }
        let stem = String(lemma.dropLast())
        if lemma.hasSuffix("y") && ["ies","ied","ier","iest"].contains(where: { input == stem + $0 }) { return true }
        if lemma.hasSuffix("e") && (input == stem + "ing" || input == lemma + "r" || input == lemma + "st") { return true }
        if lemma.count > 1, let last = lemma.last, !"aeiou".contains(last) {
            return ["ed","ing","er","est"].contains(where: { input == lemma + String(last) + $0 })
        }
        return false
    }

    static func distance(_ first: String, _ second: String) -> Int {
        let a = Array(first.utf8), b = Array(second.utf8)
        var costs = Array(repeating: Array(0...b.count), count: a.count + 1)
        for i in a.indices {
            costs[i + 1][0] = i + 1
            for j in b.indices {
                costs[i + 1][j + 1] = min(costs[i][j + 1] + 1, costs[i + 1][j] + 1, costs[i][j] + (a[i] == b[j] ? 0 : 1))
                if i > 0, j > 0, a[i] == b[j-1], a[i-1] == b[j] {
                    costs[i+1][j+1] = min(costs[i+1][j+1], costs[i-1][j-1] + 1)
                }
            }
        }
        return costs[a.count][b.count]
    }
}

struct WordAnalysis: Decodable {
    struct Spelling: Decodable { let inputWord, word, status: String; let suggestions: [String] }
    struct Phonetics: Decodable { let uk, us: String }
    struct Meaning: Decodable { let partOfSpeech: WordPart; let definition: String }
    struct Pair: Decodable { let english, chinese: String }
    struct Grammar: Decodable { let pattern, explanation: String }
    struct Relation: Decodable { let word, relation: String; let explanation: String? }
    struct Root: Decodable { let root, meaning: String; let variants: [String] }
    let isValidWord: Bool
    let error: String
    let spelling: Spelling?
    let lemma, formNote: String?
    let phonetics: Phonetics?
    let primaryPos: WordPart?
    let meanings: [Meaning]?
    let examples, collocations: [Pair]?
    let grammar: [Grammar]?
    let related: [Relation]?
    let roots: [Root]?

    func validate(input: String) throws -> String {
        func require(_ condition: Bool, _ field: String) throws {
            if !condition { throw CaptureFailure("AI 返回的「\(field)」不符合词卡要求，请重试或更换模型。") }
        }
        func checked(_ text: String, max: Int = 2000, empty: Bool = false) -> Bool {
            text.utf16.count <= max && (empty || !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            && !text.unicodeScalars.contains { CharacterSet.controlCharacters.subtracting(.whitespacesAndNewlines).contains($0) }
        }
        try require(checked(error, empty: isValidWord), "错误说明")
        if let spelling {
            let echoed = try CapturedWord.normalize(spelling.inputWord)
            try require(echoed == input && spelling.suggestions.count <= 3, "输入词回显")
            for suggestion in spelling.suggestions { _ = try CapturedWord.normalize(suggestion) }
        }
        if !isValidWord {
            if let spelling {
                try require(["uncertain","invalid"].contains(spelling.status) && spelling.word.isEmpty, "拼写判断")
                if !spelling.suggestions.isEmpty { throw CaptureFailure("请确认拼写，可能是 " + spelling.suggestions.joined(separator: "、") + "。") }
            }
            throw CaptureFailure("无法确认这是有效英文单词，请检查拼写或补充原句。")
        }
        guard let spelling, let phonetics, let primaryPos, let meanings, let examples, let collocations, let related else {
            throw CaptureFailure("AI 返回的词卡缺少必要字段。")
        }
        let word = try CapturedWord.normalize(spelling.word)
        let base = try lemma.map(CapturedWord.normalize)
        try require(spelling.suggestions.isEmpty, "拼写建议")
        switch spelling.status {
        case "valid": try require(word == input, "单词身份")
        case "corrected":
            let maximum = input.count <= 4 ? 1 : (input.count <= 9 ? 2 : 3)
            try require(word != input && CapturedWord.distance(input, word) <= maximum
                        && !CapturedWord.isInflection(input, of: word)
                        && !(base == input && CapturedWord.isInflection(word, of: input)), "拼写修正")
        default: throw CaptureFailure("AI 返回的拼写判断与释义不一致。")
        }
        try require(checked(phonetics.uk, empty: true) && checked(phonetics.us, empty: true), "音标")
        try require((1...3).contains(meanings.count) && meanings.contains { $0.partOfSpeech == primaryPos }
                    && meanings.allSatisfy { checked($0.definition) }, "释义与主要词性")
        let forms = CapturedWord.exampleForms(word: word, lemma: base, verb: meanings.contains { $0.partOfSpeech == .verb })
        try require(examples.count == 2 && examples.allSatisfy { pair in
            let text = pair.english as NSString
            return checked(pair.english) && checked(pair.chinese) && pair.english.split(whereSeparator: \.isWhitespace).count <= 20
            && CapturedWord.token.matches(in: pair.english, range: NSRange(location: 0, length: text.length))
                .contains { forms.contains(CapturedWord.canonical(text.substring(with: $0.range))) }
        }, "例句与目标词")
        try require((2...4).contains(collocations.count) && collocations.allSatisfy { checked($0.english) && checked($0.chinese) }, "搭配")
        try require(checked(formNote ?? "", max: 200, empty: true), "词形说明")
        try require((grammar ?? []).count <= 3 && (grammar ?? []).allSatisfy { checked($0.pattern, max: 120) && checked($0.explanation, max: 200) }, "语法")
        try require((roots ?? []).count <= 3 && (roots ?? []).allSatisfy { root in
            root.variants.count <= 3 && checked(root.meaning, max: 80) && ([root.root] + root.variants).allSatisfy {
                $0.range(of: #"\A-?[A-Za-z]{1,32}-?\z"#, options: .regularExpression) != nil
            }
        }, "词根")
        try require(related.count <= 12 && related.filter { $0.relation == "synonym" }.count <= 3
                    && related.filter { $0.relation == "antonym" }.count <= 2
                    && related.allSatisfy { ["synonym","antonym","same_root","confusable"].contains($0.relation)
                    && checked($0.word) && checked($0.explanation ?? "", max: 200, empty: true) }, "关联词")
        return word
    }
}

enum WordPart: String, Decodable, CaseIterable {
    case noun, verb, adjective, adverb, pronoun, preposition, conjunction, interjection, determiner, numeral, other
    var labels: (folder: String, abbreviation: String) {
        switch self {
        case .noun: ("名词", "n.")
        case .verb: ("动词", "v.")
        case .adjective: ("形容词", "adj.")
        case .adverb: ("副词", "adv.")
        case .pronoun: ("代词", "pron.")
        case .preposition: ("介词", "prep.")
        case .conjunction: ("连词", "conj.")
        case .interjection: ("感叹词", "interj.")
        case .determiner: ("限定词", "det.")
        case .numeral: ("数词", "num.")
        case .other: ("其他", "其他")
        }
    }
}

import Foundation

public struct LibraryNote: Codable, Identifiable, Hashable, Sendable {
    public var id: String
    public var path: String
    public var markdown: String
    public var added: Date

    public init(path: String, markdown: String, id: String = UUID().uuidString, added: Date = Date()) {
        self.path = path
        self.markdown = markdown
        self.id = id
        self.added = added
    }

    public var title: String {
        let stem = ((path as NSString).lastPathComponent as NSString).deletingPathExtension
        return ["_con", "_aux", "_prn", "_nul"].contains(stem.lowercased()) ? String(stem.dropFirst()) : stem
    }
    public var folder: String { (path as NSString).deletingLastPathComponent }

    public var isWord: Bool {
        !path.hasPrefix("词根/") && title.count <= 64 &&
        title.range(of: #"^[a-z]+(?:[-'][a-z]+)*$"#, options: [.regularExpression, .caseInsensitive]) != nil
    }

    public var body: String {
        let content = markdown.replacingOccurrences(of: "\r\n", with: "\n")
        guard content.hasPrefix("---\n"),
              let end = content.range(of: "\n---\n", range: content.index(content.startIndex, offsetBy: 3)..<content.endIndex)
        else { return content }
        return String(content[end.upperBound...])
    }

    public var phonetics: String {
        let row = body.components(separatedBy: "\n").first { $0.hasPrefix("**发音**") } ?? ""
        return row.replacingOccurrences(of: #"^\*\*发音\*\*[：:]\s*"#, with: "", options: .regularExpression)
    }
    public var meaning: String {
        let lines = body.components(separatedBy: "\n")
        guard let start = lines.firstIndex(where: { $0.contains("**释义**") }) else { return "" }
        return lines.dropFirst(start + 1).drop(while: { $0.isEmpty }).prefix(while: { $0.hasPrefix(">") })
            .map { String($0.dropFirst()).trimmingCharacters(in: .whitespaces) }.joined(separator: "\n")
    }

    public static func creationDate(in source: String) -> Date? {
        let markdown = source.replacingOccurrences(of: "\r\n", with: "\n")
        guard markdown.hasPrefix("---"), let end = markdown.range(of: "\n---", range: markdown.index(markdown.startIndex, offsetBy: 3)..<markdown.endIndex),
              let line = markdown[..<end.lowerBound].split(separator: "\n").first(where: { $0.hasPrefix("created:") }) else { return nil }
        let value = String(line.dropFirst(8)).trimmingCharacters(in: CharacterSet(charactersIn: "'\" \t\r"))
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = formatter.date(from: value) { return date }
        formatter.formatOptions = [.withInternetDateTime]
        return formatter.date(from: value)
    }

    public static func targets(_ link: String, from current: LibraryNote, among notes: [LibraryNote], vaultPrefix: String = "英语单词") -> [LibraryNote] {
        let raw = link.components(separatedBy: "|")[0].components(separatedBy: "#")[0]
            .trimmingCharacters(in: .whitespacesAndNewlines).precomposedStringWithCanonicalMapping.lowercased()
        guard !raw.isEmpty else { return [] }
        let name = raw.hasSuffix(".md") ? raw : raw + ".md"
        if name.contains("/") {
            let exact = notes.filter { $0.path.lowercased() == name }
            if !exact.isEmpty { return exact }
            let relative = notes.filter { $0.path.lowercased() == (current.folder + "/" + name) }
            if !relative.isEmpty { return relative }
            // An explicit folder link must not resolve to an unrelated namesake.
            let prefix = vaultPrefix.trimmingCharacters(in: CharacterSet(charactersIn: "/")).lowercased()
            guard !prefix.isEmpty, name.hasPrefix(prefix + "/") else { return [] }
            let localPath = String(name.dropFirst(prefix.count + 1))
            return notes.filter { $0.path.lowercased() == localPath }
        }
        return notes.filter { ($0.path as NSString).lastPathComponent.lowercased() == name }
    }
}

public enum LibraryError: LocalizedError {
    case invalidPath, alreadyExists, invalidServer, invalidResponse, accountMismatch, denied, restoredHistory
    public var errorDescription: String? {
        switch self {
        case .invalidPath: return "笔记路径不可用，请检查目录和文件名。"
        case .alreadyExists: return "该词条已存在。"
        case .invalidServer: return "请输入同步服务的 HTTPS 地址。"
        case .invalidResponse: return "同步服务返回了无法识别的数据。"
        case .accountMismatch: return "本机词库已绑定另一组凭据，请使用原有同步设置。"
        case .restoredHistory: return "服务器历史已回退。请核实备份，并重新核对词库。"
        case .denied: return "设备凭证无效或已撤销，请重新绑定设备。"
        }
    }
}

public func validateNotePath(_ path: String) throws {
    let parts = path.split(separator: "/", omittingEmptySubsequences: false)
    let forbidden = CharacterSet(charactersIn: #"<>:"\|?*"#).union(.controlCharacters)
    let devices = ["con", "prn", "aux", "nul", "conin$", "conout$", "clock$"] + "0123456789¹²³".flatMap { ["com\($0)", "lpt\($0)"] }
    guard path.utf16.count <= 1024, path.lowercased().hasSuffix(".md"), parts.allSatisfy({ part in
        !part.isEmpty && !part.hasPrefix(".") && !part.hasSuffix(" ") && !part.hasSuffix(".") &&
        part.utf8.count <= 255 && part.rangeOfCharacter(from: forbidden) == nil &&
        !devices.contains(String(part.split(separator: ".").first ?? "").trimmingCharacters(in: .whitespaces).lowercased())
    }) else { throw LibraryError.invalidPath }
}

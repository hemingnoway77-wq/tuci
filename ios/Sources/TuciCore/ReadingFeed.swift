import Foundation

public enum ReadingCategory: String, Codable, Sendable {
    case general, technology
    public var title: String { self == .general ? "泛读" : "科技" }
}

public struct ReadingArticle: Identifiable, Codable, Hashable, Sendable {
    public var id: URL { url }
    public let title, summary, source: String
    public let url: URL
    public let category: ReadingCategory
    public let published: Date?
}

struct ReadingFeed: Sendable {
    let name: String
    let url: URL
    let category: ReadingCategory
    static let sources = [
        ReadingFeed(name: "ScienceDaily · Society", url: URL(string: "https://www.sciencedaily.com/rss/top/society.xml")!, category: .general),
        ReadingFeed(name: "ScienceDaily · Environment", url: URL(string: "https://www.sciencedaily.com/rss/earth_climate/environmental_science.xml")!, category: .general),
        ReadingFeed(name: "Cloudflare Blog", url: URL(string: "https://blog.cloudflare.com/rss/")!, category: .technology),
        ReadingFeed(name: "BBC · Technology", url: URL(string: "https://feeds.bbci.co.uk/news/technology/rss.xml")!, category: .technology)
    ]
}

final class ReadingFeedParser: NSObject, XMLParserDelegate {
    private let feed: ReadingFeed
    private var stack = [String]()
    private var itemDepth: Int?
    private var fields = [String:String]()
    private var nestedLink: String?
    private var articles = [ReadingArticle]()
    private var links = Set<URL>()
    private init(feed: ReadingFeed) { self.feed = feed }

    static func parse(data: Data, feed: ReadingFeed) throws -> [ReadingArticle] {
        guard data.count <= 2 * 1024 * 1024, !data.contains(0),
              let xml = String(data: data, encoding: .utf8),
              xml.range(of: "<!DOCTYPE", options: .caseInsensitive) == nil else { throw URLError(.cannotParseResponse) }
        let parser = XMLParser(data: data)
        parser.shouldProcessNamespaces = true
        parser.shouldResolveExternalEntities = false
        parser.externalEntityResolvingPolicy = .never
        let delegate = ReadingFeedParser(feed: feed)
        parser.delegate = delegate
        guard parser.parse(), parser.parserError == nil else { throw URLError(.cannotParseResponse) }
        return delegate.articles
    }

    func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName qName: String?,
                attributes attributeDict: [String:String] = [:]) {
        stack.append(elementName)
        if stack.count > 64 { parser.abortParsing(); return }
        if itemDepth == nil && (elementName == "item" || elementName == "entry") {
            itemDepth = stack.count
            fields.removeAll(keepingCapacity: true)
            nestedLink = nil
        } else if let itemDepth, stack.count == itemDepth + 1 && elementName == "link" && nestedLink == nil,
                  [nil,"alternate"].contains(attributeDict["rel"]) {
            nestedLink = attributeDict["href"]
        }
    }

    func parser(_ parser: XMLParser, foundCharacters string: String) {
        guard let itemDepth, stack.count > itemDepth else { return }
        let key = stack[itemDepth]
        guard ["title","link","description","summary","content","encoded","pubDate","published","updated","date"].contains(key),
              (fields[key]?.utf8.count ?? 0) < 16000,
              !stack.dropFirst(itemDepth + 1).contains(where: { ["script","style"].contains($0.lowercased()) }) else { return }
        fields[key, default:""].append(contentsOf: string.prefix(16000))
    }

    func parser(_ parser: XMLParser, foundCDATA CDATABlock: Data) {
        self.parser(parser, foundCharacters: String(decoding: CDATABlock, as: UTF8.self))
    }

    func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName qName: String?) {
        defer { _ = stack.popLast() }
        guard itemDepth == stack.count else { return }
        itemDepth = nil
        guard articles.count < 80 else { return }
        let title = Self.clean(fields["title"] ?? "", max: 200)
        guard !title.isEmpty, let url = Self.safeURL(nestedLink ?? fields["link"] ?? "", relativeTo: feed.url), links.insert(url).inserted else { return }
        let summary = ["description","summary","content","encoded"].compactMap { fields[$0] }.first { !$0.isEmpty } ?? ""
        let published = ["pubDate","published","updated","date"].compactMap { fields[$0] }.compactMap(Self.date).first
        let cleaned = Self.clean(summary, max: 600)
        articles.append(.init(title: title, summary: cleaned.isEmpty ? "暂无摘要。" : cleaned, source: feed.name, url: url, category: feed.category, published: published))
    }

    static func safeURL(_ link: String, relativeTo base: URL? = nil) -> URL? {
        let raw = link.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !raw.isEmpty, raw.utf8.count <= 4096, let url = URL(string: raw, relativeTo: base)?.absoluteURL,
              var components = URLComponents(url: url, resolvingAgainstBaseURL: true), ["https","http"].contains(components.scheme?.lowercased() ?? ""),
              components.host?.isEmpty == false, components.user == nil, components.password == nil else { return nil }
        components.fragment = nil
        return components.url
    }

    private static func clean(_ text: String, max: Int) -> String {
        var result = String(text.prefix(16000))
        for _ in 0..<2 {
            for (encoded, decoded) in [("&amp;","&"),("&lt;","<"),("&gt;",">"),("&nbsp;"," "),("&quot;","\""),("&#39;","'"),("&apos;","'")] {
                result = result.replacingOccurrences(of: encoded, with: decoded)
            }
        }
        for pattern in [#"<(script|style)\b[^>]*>[\s\S]*?</\1\s*>"#, "<[^>]*>", #"\s+"#] {
            result = result.replacingOccurrences(of: pattern, with: " ", options: [.regularExpression,.caseInsensitive])
        }
        result = result.trimmingCharacters(in: .whitespacesAndNewlines)
        return result.count > max ? String(result.prefix(max-1)) + "…" : result
    }

    private static func date(_ raw: String) -> Date? {
        let text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        let iso = ISO8601DateFormatter()
        if let date = iso.date(from: text) { return date }
        iso.formatOptions.insert(.withFractionalSeconds)
        if let date = iso.date(from: text) { return date }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        for format in ["EEE, dd MMM yyyy HH:mm:ss z", "dd MMM yyyy HH:mm:ss z", "yyyy-MM-dd"] {
            formatter.dateFormat = format
            if let date = formatter.date(from: text) { return date }
        }
        return nil
    }
}

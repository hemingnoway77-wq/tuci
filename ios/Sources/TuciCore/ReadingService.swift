import Foundation
import CryptoKit

public struct DailyReading: Sendable {
    public let articles: [ReadingArticle]
    public let day: Date
    public let message: String
}

public struct ReadingService: Sendable {
    private let file: URL
    private let session: URLSession
    private let feeds: [ReadingFeed]
    static let limit = 2 * 1024 * 1024

    public init() {
        self.init(directory: FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0].appendingPathComponent("TuciReading"),
                  session: .shared, feeds: ReadingFeed.sources)
    }

    init(directory: URL, session: URLSession, feeds: [ReadingFeed]) {
        file = directory.appendingPathComponent("daily.json")
        self.session = session
        self.feeds = feeds
    }

    struct Cache: Codable {
        var day: Date
        var selected, pool: [ReadingArticle]
    }

    public func load(refresh: Bool, now: Date = Date()) async throws -> DailyReading {
        let calendar = Calendar.current, day = calendar.startOfDay(for: now)
        let previous = readCache()
        if !refresh, let previous, calendar.isDate(previous.day, inSameDayAs: day) {
            return .init(articles: previous.selected, day: day, message: "")
        }
        let results = await withTaskGroup(of: (Int,[ReadingArticle]?).self) { group in
            for (i, feed) in feeds.enumerated() {
                group.addTask { (i, try? await fetch(feed)) }
            }
            var results: [(Int,[ReadingArticle]?)] = []
            for await result in group { results.append(result) }
            return results.sorted { $0.0 < $1.0 }.map(\.1)
        }
        try Task.checkCancellation()
        let fresh = results.compactMap { $0 }.flatMap { $0 }
        guard !fresh.isEmpty else {
            return .init(articles: previous?.selected ?? [], day: previous?.day ?? day, message: previous == nil ? "暂时无法获取文章，请稍后刷新。" : "来源暂不可用，显示上次缓存的推荐。")
        }
        var seen = Set<URL>()
        let pool = Array((fresh + (previous?.pool ?? [])).filter { seen.insert($0.url).inserted }.prefix(300))
        let selected = Self.select(pool, day: day, previous: previous)
        var message = results.contains { $0 == nil } ? "部分来源暂不可用。" : ""
        do {
            let data = try JSONEncoder().encode(Cache(day: day, selected: selected, pool: pool))
            try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Task.checkCancellation()
            try data.write(to: file, options: .atomic)
        } catch is CancellationError { throw CancellationError() }
        catch { message += "本次推荐未能保存到本机。" }
        return .init(articles: selected, day: day, message: message)
    }

    private func fetch(_ feed: ReadingFeed) async throws -> [ReadingArticle] {
        var request = URLRequest(url: feed.url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 8)
        request.setValue("Tuci/1.0 (+daily-reading)", forHTTPHeaderField: "User-Agent")
        return try await withThrowingTaskGroup(of: [ReadingArticle].self) { group in
            group.addTask {
                try await Task.sleep(nanoseconds: 8_000_000_000)
                throw URLError(.timedOut)
            }
            group.addTask {
                let (bytes, response) = try await session.bytes(for: request)
                defer { bytes.task.cancel() }
                guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode),
                      http.expectedContentLength <= Int64(Self.limit) else { throw URLError(.badServerResponse) }
                var data = Data()
                for try await byte in bytes {
                    guard data.count < Self.limit else { throw URLError(.dataLengthExceedsMaximum) }
                    data.append(byte)
                }
                return try ReadingFeedParser.parse(data: data, feed: feed)
            }
            defer { group.cancelAll() }
            return try await group.next()!
        }
    }

    private func readCache() -> Cache? {
        guard let size = (try? file.resourceValues(forKeys: [.fileSizeKey]))?.fileSize, size <= Self.limit,
              let data = try? Data(contentsOf: file), let cache = try? JSONDecoder().decode(Cache.self, from: data),
              (1...3).contains(cache.selected.count), cache.pool.count <= 300,
              cache.selected.filter({ $0.category == .general }).count <= 2,
              cache.selected.filter({ $0.category == .technology }).count <= 1,
              (cache.selected + cache.pool).allSatisfy({ !$0.title.isEmpty && ReadingFeedParser.safeURL($0.url.absoluteString) != nil }) else { return nil }
        return cache
    }

    static func select(_ pool: [ReadingArticle], day: Date, previous: Cache?) -> [ReadingArticle] {
        let yesterday = Set((previous?.selected ?? []).map(\.url))
        let parts = Calendar.current.dateComponents([.year,.month,.day], from: day)
        let dayKey = String(format: "%04d-%02d-%02d", parts.year!,parts.month!,parts.day!)
        let keys = Dictionary(uniqueKeysWithValues: pool.map { article in
            (article.url, Array(SHA256.hash(data: Data((dayKey + ":" + article.url.absoluteString).utf8))))
        })
        var selected: [ReadingArticle] = []
        for category in [ReadingCategory.general, .technology] {
            let needed = category == .general ? 2 : 1
            if previous.map({ Calendar.current.isDate($0.day, inSameDayAs: day) }) == true {
                for old in previous?.selected ?? [] where old.category == category {
                    if let index = pool.firstIndex(where: { $0.url == old.url }) { selected.append(pool[index]) }
                }
            }
            while selected.filter({ $0.category == category }).count < needed {
                let sources = Set(selected.map(\.source))
                let next = pool.filter { article in article.category == category && !selected.contains(where: { $0.url == article.url }) }
                    .min { a, b in
                        if yesterday.contains(a.url) != yesterday.contains(b.url) { return !yesterday.contains(a.url) }
                        if sources.contains(a.source) != sources.contains(b.source) { return !sources.contains(a.source) }
                        return keys[a.url]!.lexicographicallyPrecedes(keys[b.url]!)
                    }
                guard let next else { break }
                selected.append(next)
            }
        }
        return selected
    }
}

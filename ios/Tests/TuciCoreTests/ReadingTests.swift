import Foundation
import Testing
@testable import TuciCore

struct ReadingTests {
    let feed = ReadingFeed(name: "Site", url: URL(string:"https://example.invalid/feeds/list.xml")!, category: .general)

    @Test func xmlDoesNotCreateUnsafeDestinationsOrDuplicateEntries() throws {
        let xml = """
        <feed xmlns="http://www.w3.org/2005/Atom">
        <entry><title>Story</title><link rel="alternate" href="/item#top"/><summary><![CDATA[<script>hidden</script><p>Words</p>]]></summary>
        <updated>2026-11-05T02:00:00Z</updated></entry>
        <entry><title>Duplicate</title><link href="/item"/></entry>
        <entry><title>Unsafe</title><link href="javascript:alert(1)"/></entry>
        </feed>
        """
        let articles = try ReadingFeedParser.parse(data: Data(xml.utf8), feed: feed)
        #expect(articles.count == 1)
        #expect(articles.first?.summary == "Words")
        #expect(articles.first?.url.absoluteString == "https://example.invalid/item")
        #expect(articles.first?.published != nil)
        #expect(throws: URLError.self) {
            _ = try ReadingFeedParser.parse(data: Data("<!DOCTYPE article [<!ENTITY x SYSTEM 'file:///secret'>]><rss/>".utf8), feed: feed)
        }
    }

    @Test func selectionStaysStableTodayThenRotatesAcrossSources() throws {
        let day = Date.now
        let pool: [ReadingArticle] = (0..<9).map { i in
            ReadingArticle(title:"\(i)", summary:"", source: i.isMultiple(of: 2) ? "a":"b", url: URL(string:"https://fixture.invalid/\(i)")!,
                           category: i < 6 ? .general : .technology, published: nil)
        }
        let first = ReadingService.select(pool, day: day, previous: nil)
        let previous = ReadingService.Cache(day: day, selected: first, pool: pool)
        #expect(first.count == 3)
        #expect(first[0].source != first[1].source)
        #expect(ReadingService.select(pool.reversed(), day: day, previous: previous) == first)
        let next = ReadingService.select(pool, day: Calendar.current.date(byAdding: .day, value: 1, to: day)!, previous: previous)
        #expect(Set(first.map(\.id)).isDisjoint(with: next.map(\.id)))
    }

    @Test func offlineReopensLastEditionWithoutChangingRecommendationDate() async throws {
        let day = Calendar.current.startOfDay(for: Date.now)
        let story = ReadingArticle(title:"A story", summary:"Cached", source:"Site", url: feed.url, category: .general, published: nil)
        let cache = ReadingService.Cache(day:day, selected:[story], pool:[story])
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory:true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories:true)
        defer { try? FileManager.default.removeItem(at: directory) }
        try JSONEncoder().encode(cache).write(to:directory.appendingPathComponent("daily.json"))
        let service = ReadingService(directory:directory, session:.shared, feeds:[])
        let today = try await service.load(refresh:false, now: day)
        #expect(today.articles == [story] && today.message.isEmpty)
        let offline = try await service.load(refresh:false, now: day.addingTimeInterval(48*3600))
        #expect(offline.day == day && offline.articles == [story] && !offline.message.isEmpty)
    }
}

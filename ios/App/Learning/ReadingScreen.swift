import SafariServices
import SwiftUI
import TuciCore

struct ReadingScreen: View {
    @Environment(\.paperColor) private var paper
    @State private var edition: DailyReading?
    @State private var article: ReadingArticle?
    @State private var refreshID = 0
    @State private var busy = false
    @State private var issue = ""

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                if let edition, !edition.articles.isEmpty {
                    Text(edition.day.formatted(date: .long, time: .omitted))
                        .font(.subheadline).foregroundStyle(.secondary)
                    ForEach(edition.articles) { story in
                        Divider()
                        Button { article = story } label: {
                            VStack(alignment: .leading, spacing: 10) {
                                HStack {
                                    Text(story.category.title).foregroundStyle(.tint)
                                    Text(story.source).foregroundStyle(.secondary)
                                    Spacer()
                                    Image(systemName: "arrow.up.forward").foregroundStyle(.secondary)
                                }.font(.caption.weight(.medium))
                                Text(story.title)
                                    .font(.system(.title2, design: .serif, weight: .medium))
                                    .fixedSize(horizontal: false, vertical: true)
                                Text(story.summary).font(.body).foregroundStyle(.secondary).lineLimit(5)
                                if let date = story.published {
                                    Text("发布于 \(date.formatted(date: .abbreviated, time: .omitted))")
                                        .font(.caption).foregroundStyle(.secondary)
                                }
                            }
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .contentShape(Rectangle())
                        }.buttonStyle(.plain).accessibilityHint("在应用内阅读原文")
                    }
                } else if busy {
                    ProgressView("正在获取推荐…").frame(maxWidth: .infinity).padding(.vertical, 60)
                } else {
                    ContentUnavailableView("暂无推荐文章", systemImage: "newspaper", description: Text(issue.isEmpty ? "下拉刷新或稍后再试。" : issue))
                }
                if !issue.isEmpty && !(edition?.articles.isEmpty ?? true) {
                    Text(issue).font(.footnote).foregroundStyle(.secondary)
                }
            }
            .padding(.horizontal, 22).padding(.vertical, 16)
        }
        .background(paper.ignoresSafeArea())
        .navigationTitle("每日阅读")
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Button { refreshID += 1 } label: { Image(systemName: "arrow.clockwise") }
                    .disabled(busy).accessibilityLabel("刷新文章")
            }
        }
        .refreshable { refreshID += 1 }
        .task(id: refreshID) {
            busy = true
            do {
                let result = try await ReadingService().load(refresh: refreshID > 0)
                try Task.checkCancellation()
                edition = result
                issue = result.message
            } catch {
                if !Task.isCancelled { issue = "暂时无法获取推荐，请稍后刷新。" }
            }
            if !Task.isCancelled { busy = false }
        }
        .sheet(item: $article) { story in
            ArticleBrowser(url: story.url).ignoresSafeArea()
        }
    }
}

private struct ArticleBrowser: UIViewControllerRepresentable {
    let url: URL
    @Environment(\.dismiss) private var dismiss
    final class Coordinator: NSObject, SFSafariViewControllerDelegate {
        let close: () -> Void
        init(close: @escaping () -> Void) { self.close = close }
        func safariViewControllerDidFinish(_ controller: SFSafariViewController) { close() }
    }
    func makeCoordinator() -> Coordinator { Coordinator { dismiss() } }
    func makeUIViewController(context: Context) -> SFSafariViewController {
        let configuration = SFSafariViewController.Configuration()
        configuration.entersReaderIfAvailable = true
        let browser = SFSafariViewController(url: url, configuration: configuration)
        browser.delegate = context.coordinator
        browser.dismissButtonStyle = .close
        return browser
    }
    func updateUIViewController(_ uiViewController: SFSafariViewController, context: Context) {}
}

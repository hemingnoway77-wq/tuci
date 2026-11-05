import SwiftUI
import TuciCore

@main
struct TuciApp: App {
    @StateObject private var model = AppModel()
    var body: some Scene { WindowGroup { HomeView().environmentObject(model) } }
}

enum NotebookRoute: Hashable {
    case folder(String), note(String), roam
}
enum PaperTheme: String, CaseIterable {
    case warm = "暖灰", ink = "墨白", rose = "玫红"
    var tint: Color {
        switch self { case .warm: return Color(red: 0.51, green: 0.25, blue: 0.23)
        case .rose: return Color(red: 0.67, green: 0.16, blue: 0.35)
        case .ink: return .primary }
    }
    var paper: Color {
        let light: UIColor
        switch self { case .warm: light = UIColor(red: 0.975, green: 0.961, blue: 0.925, alpha: 1)
        case .rose: light = UIColor(red: 1, green: 0.955, blue: 0.96, alpha: 1)
        case .ink: light = .systemBackground }
        return Color(uiColor: UIColor { $0.userInterfaceStyle == .dark ? .systemBackground : light })
    }
}
struct HomeView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.scenePhase) private var phase
    @AppStorage("theme") private var theme: PaperTheme = .warm
    @AppStorage("apiBase") private var base = ""
    @AppStorage("aiModel") private var name = ""
    @AppStorage("classify") private var classify = true
    @State private var tab = 0
    @State private var path: [NotebookRoute] = []
    @State private var settings = false
    var body: some View {
        Group {
            if let issue = model.startupIssue {
                ContentUnavailableView {
                    Label("暂时无法打开词库", systemImage: "book.closed")
                } description: { Text(issue) } actions: { Button("重新读取") { model.load() } }
            } else {
                TabView(selection: $tab) {
                    NavigationStack {
                        CaptureScreen(existing: model.notes, save: model.save, open: open,
                            api: AIConfiguration(baseURL: base, apiKey: model.apiKey, model: name), classify: classify)
                            .toolbar { settingsButton }
                    }.tabItem { Label("收录", systemImage: "square.and.pencil") }.tag(0)
                    NavigationStack { ReadingScreen().toolbar { settingsButton } }
                        .tabItem { Label("阅读", systemImage: "newspaper") }.tag(1)
                    NavigationStack(path: $path) {
                        DirectoryView(folder: "").navigationDestination(for: NotebookRoute.self) { destination in
                            switch destination {
                            case .folder(let name): DirectoryView(folder: name)
                            case .note(let id): NoteDetail(id: id, navigate: { path.append(.note($0.id)) })
                            case .roam: RoamingView(navigate: { path.append(.note($0.id)) })
                            }
                        }.toolbar { settingsButton }
                    }.tabItem { Label("词库", systemImage: "books.vertical") }.tag(2)
                }
            }
        }
        .tint(theme.tint)
        .environment(\.paperColor, theme.paper)
        .sheet(isPresented: $settings) { SettingsView() }
        .alert("兔词", isPresented: Binding(get: { model.alert != nil }, set: { if !$0 { model.alert = nil } })) {
            Button("好") { model.alert = nil }
        } message: { Text(model.alert ?? "") }
        .task(id: phase == .active) {
            guard phase == .active else { return }
            while !Task.isCancelled {
                await model.synchronize()
                do { try await Task.sleep(for: .seconds(60)) } catch { break }
            }
        }
    }
    @ToolbarContentBuilder private var settingsButton: some ToolbarContent {
        ToolbarItem(placement: .topBarTrailing) { Button { settings = true } label: { Image(systemName: "gearshape") }.accessibilityLabel("设置") }
    }
    private func open(_ note: LibraryNote) { path = [.note(note.id)]; tab = 2 }
}
private struct PaperKey: EnvironmentKey { static let defaultValue = PaperTheme.warm.paper }
extension EnvironmentValues {
    var paperColor: Color { get { self[PaperKey.self] } set { self[PaperKey.self] = newValue } }
}

import SwiftUI
import TuciCore

struct DirectoryView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.paperColor) private var paper
    let folder: String
    @State private var query = ""
    private var entries: [LibraryNote] {
        if !query.isEmpty {
            let prefix = folder.isEmpty ? "" : folder + "/"
            return model.notes.filter { $0.path.hasPrefix(prefix) && ($0.title.localizedCaseInsensitiveContains(query) || $0.body.localizedCaseInsensitiveContains(query)) }
        }
        return model.notes.filter { $0.folder == folder }
    }
    private var folders: [String] {
        let prefix = folder.isEmpty ? "" : folder + "/"
        return Set(model.notes.compactMap { note -> String? in
            guard note.path.hasPrefix(prefix) else { return nil }
            let rest = note.path.dropFirst(prefix.count)
            guard let slash = rest.firstIndex(of: "/") else { return nil }
            return prefix + String(rest[..<slash])
        }).sorted { $0.localizedStandardCompare($1) == .orderedAscending }
    }
    var body: some View {
        List {
            if folder.isEmpty && query.isEmpty && model.notes.contains(where: \.isWord) {
                Section {
                    NavigationLink(value: NotebookRoute.roam) {
                        Label("漫游复习", systemImage: "shuffle").font(.headline).padding(.vertical, 6)
                    }
                }
            }
            if query.isEmpty {
                ForEach(folders, id: \.self) { path in
                    NavigationLink(value: NotebookRoute.folder(path)) { Label((path as NSString).lastPathComponent, systemImage: "folder") }
                }
            }
            ForEach(entries) { note in
                NavigationLink(value: NotebookRoute.note(note.id)) {
                    VStack(alignment: .leading, spacing: 4) {
                        Text(note.title).font(.system(.title3, design: note.isWord ? .serif : .default))
                        if !note.meaning.isEmpty { Text(note.meaning).font(.subheadline).foregroundStyle(.secondary).lineLimit(1) }
                        else if !query.isEmpty { Text(note.folder).font(.caption).foregroundStyle(.secondary) }
                    }.padding(.vertical, 4)
                }
            }
        }
        .overlay {
            if model.notes.isEmpty {
                ContentUnavailableView("词库还是空的", systemImage: "books.vertical", description: Text("在「收录」添加第一个单词，或在设置中连接已有词库。"))
            } else if !query.isEmpty && entries.isEmpty { ContentUnavailableView.search(text: query) }
        }
        .scrollContentBackground(.hidden).background(paper)
        .searchable(text: $query, prompt: "搜索词条或笔记正文")
        .navigationTitle(folder.isEmpty ? "词库" : (folder as NSString).lastPathComponent)
    }
}

private struct Selection: Identifiable {
    let id = UUID()
    let link: String
    let candidates: [LibraryNote]
    let preview: Bool
}

struct NoteDetail: View {
    @AppStorage("vaultPrefix") private var vaultPrefix = "英语单词"
    @EnvironmentObject private var model: AppModel
    @Environment(\.paperColor) private var paper
    @Environment(\.accessibilityVoiceOverEnabled) private var voiceOver
    let id: String
    let navigate: (LibraryNote) -> Void
    @State private var selection: Selection?
    var body: some View {
        Group {
            if let note = model.notes.first(where: { $0.id == id }) {
                VStack(spacing: 0) {
                    NativeNoteText(note: note, offset: model.scrollPositions[id] ?? 0,
                                   position: { model.scrollPositions[id] = $0 },
                                   link: { target, double in resolve(target, from: note, double: double) })
                    if voiceOver {
                        let links = NoteMarkup.links(in: note)
                        if !links.isEmpty {
                            Menu("关联词条") {
                                ForEach(links, id: \.self) { link in
                                    Button(link.components(separatedBy: "|").last ?? link) { resolve(link, from: note, double: false) }
                                }
                            }.padding()
                        }
                    }
                }.navigationTitle(note.title)
            } else {
                ContentUnavailableView("这篇笔记已移除", systemImage: "doc")
            }
        }
        .background(paper).navigationBarTitleDisplayMode(.inline)
        .sheet(item: $selection) { selected in
            NavigationStack {
                if selected.candidates.count == 1, let note = selected.candidates.first {
                    ScrollView {
                        VStack(alignment: .leading, spacing: 14) {
                            Text(note.title).font(.system(.largeTitle, design: .serif)).textSelection(.enabled)
                            if !note.phonetics.isEmpty { Text(note.phonetics).foregroundStyle(.secondary) }
                            Divider()
                            Text(note.meaning.isEmpty ? NoteMarkup.excerpt(note) : note.meaning)
                                .font(.body).textSelection(.enabled)
                            Button("打开笔记") { selection = nil; navigate(note) }.buttonStyle(.borderedProminent)
                        }.frame(maxWidth: .infinity, alignment: .leading).padding(24)
                    }.background(paper)
                } else if selected.candidates.isEmpty {
                    ContentUnavailableView("词库中找不到这篇笔记", systemImage: "link",
                                           description: Text(selected.link.components(separatedBy: "|")[0]))
                } else {
                    List(selected.candidates) { note in
                        Button(note.path) {
                            if selected.preview { selection = Selection(link: selected.link, candidates: [note], preview: true) }
                            else { selection = nil; navigate(note) }
                        }
                    }.navigationTitle("选择笔记")
                }
            }.presentationDetents([.medium, .large]).presentationDragIndicator(.visible)
        }
    }
    private func resolve(_ link: String, from note: LibraryNote, double: Bool) {
        let matches = LibraryNote.targets(link, from: note, among: model.notes, vaultPrefix: vaultPrefix)
        if double, matches.count == 1 { navigate(matches[0]) }
        else { selection = Selection(link: link, candidates: matches, preview: !double) }
    }
}

struct RoamingView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.paperColor) private var paper
    let navigate: (LibraryNote) -> Void
    @State private var exposure: AppModel.Exposure?
    var body: some View {
        VStack(spacing: 0) {
            if let card = exposure {
                ScrollView {
                    VStack(alignment: .leading, spacing: 16) {
                        if card.rememberedToday { Label("今天记住过", systemImage: "checkmark.circle").foregroundStyle(.secondary).font(.caption) }
                        Text(card.note.title).font(.system(.largeTitle, design: .serif).weight(.medium))
                        if !card.note.phonetics.isEmpty { Text(card.note.phonetics).foregroundStyle(.secondary) }
                        Divider()
                        Text(card.note.meaning.isEmpty ? NoteMarkup.excerpt(card.note) : card.note.meaning).lineSpacing(6)
                        Button("阅读整篇笔记") { navigate(card.note) }.font(.subheadline)
                    }.frame(maxWidth: .infinity, alignment: .leading).padding(24)
                }
                HStack {
                    Button("下一张") { advance(card, false) }.buttonStyle(.bordered)
                    Button("今天已记住") { advance(card, true) }.buttonStyle(.borderedProminent)
                }.controlSize(.large).padding()
            } else { ContentUnavailableView("还没有可复习的单词", systemImage: "rectangle.stack") }
        }.background(paper).navigationTitle("漫游复习").navigationBarTitleDisplayMode(.inline)
        .onAppear { if exposure == nil { exposure = model.nextCard() } }
    }
    private func advance(_ card: AppModel.Exposure, _ remembered: Bool) {
        if let next = model.nextCard(finishing: card, remembered: remembered) { exposure = next }
    }
}

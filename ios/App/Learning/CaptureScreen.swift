import SwiftUI
import TuciCore

struct CaptureScreen: View {
    let existing: [LibraryNote]
    let save: (String, String) throws -> Void
    let open: (LibraryNote) -> Void
    let api: AIConfiguration
    let classify: Bool

    @Environment(\.paperColor) private var paper
    @FocusState private var focus: Field?
    @State private var word = ""
    @State private var context = ""
    @State private var message = ""
    @State private var task: Task<Void, Never>?
    @State private var resultWord: String?
    @State private var correctedFrom: String?
    @State private var progress = ""
    @State private var unsaved: CapturedCard?
    private enum Field { case word, context }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 24) {
                VStack(alignment: .leading, spacing: 10) {
                    Text("英文单词").font(.subheadline.weight(.medium))
                    TextField("输入或粘贴单词", text: $word)
                        .font(.system(.largeTitle, design: .serif))
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .keyboardType(.asciiCapable)
                        .submitLabel(.next)
                        .focused($focus, equals: .word)
                        .disabled(task != nil)
                        .onSubmit { focus = .context }
                    Divider()
                }
                VStack(alignment: .leading, spacing: 10) {
                    Text("遇见时的原句（选填）").font(.subheadline.weight(.medium))
                    TextField("粘贴原句，保留这个词出现的语境", text: $context, axis: .vertical)
                        .lineLimit(3...8)
                        .focused($focus, equals: .context)
                        .disabled(task != nil)
                        .padding(14)
                        .background(.background.opacity(0.65), in: RoundedRectangle(cornerRadius: 12))
                }
                if task == nil {
                    Button { begin() } label: {
                        Label("解析并保存", systemImage: "square.and.pencil")
                            .frame(maxWidth: .infinity).frame(minHeight: 36)
                    }.buttonStyle(.borderedProminent)
                        .disabled(word.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                } else {
                    HStack {
                        ProgressView()
                        Text(progress).font(.subheadline)
                        Spacer()
                        Button("取消") { task?.cancel() }
                    }
                }
                if !message.isEmpty {
                    Text(message).font(.callout).textSelection(.enabled)
                }
                if let resultWord {
                    let results = CapturedWord.matches(resultWord, notes: existing)
                    VStack(alignment: .leading, spacing: 8) {
                        if let from = correctedFrom { Text("拼写修正  \(from) → \(resultWord)").font(.caption).foregroundStyle(.secondary) }
                        ForEach(results) { note in
                            Button { open(note) } label: {
                                VStack(alignment: .leading, spacing: 8) {
                                    HStack {
                                        Text(note.title).font(.system(.title, design: .serif))
                                        Spacer()
                                        Image(systemName: "arrow.up.forward")
                                    }
                                    if !note.meaning.isEmpty { Text(note.meaning).font(.body).foregroundStyle(.primary) }
                                    if results.count > 1 { Text(note.path).font(.caption).foregroundStyle(.secondary) }
                                }.padding().background(.background.opacity(0.72), in: RoundedRectangle(cornerRadius: 12))
                            }.buttonStyle(.plain).accessibilityHint("打开完整笔记")
                        }
                    }
                }
                if let unsaved {
                    DisclosureGroup("查看并复制尚未保存的笔记") {
                        Text(unsaved.markdown).frame(maxWidth: .infinity, alignment: .leading).textSelection(.enabled)
                    }
                    Button("重试保存") { persist(unsaved) }
                }
            }
            .padding(.horizontal, 22).padding(.vertical, 24)
        }.scrollDismissesKeyboard(.interactively)
            .background(paper.ignoresSafeArea())
            .navigationTitle("收录")
    }

    @MainActor private func begin() {
        focus = nil
        guard task == nil else { return }
        message = ""
        resultWord = nil
        correctedFrom = nil
        unsaved = nil
        do {
            let input = try CapturedWord.normalize(word)
            let matches = CapturedWord.matches(input, notes: existing)
            if !matches.isEmpty { showExisting(input); return }
            let source = context
            progress = "正在请求 AI…"
            task = Task { @MainActor in
                defer { task = nil }
                do {
                    let card = try await CaptureService().generate(word: input, context: source, api: api, classify: classify, notes: existing) {
                        await MainActor.run { progress = "正在生成笔记…" }
                    }
                    try Task.checkCancellation()
                    if !CapturedWord.matches(card.word, notes: existing).isEmpty {
                        correctedFrom = card.word != input ? input : nil
                        word = card.word
                        showExisting(card.word)
                    } else { persist(card) }
                } catch {
                    message = Task.isCancelled ? "已取消，输入已保留。" : error.localizedDescription
                }
            }
        } catch { message = error.localizedDescription }
    }

    private func showExisting(_ target: String) {
        resultWord = target
        message = "已收录，可直接打开已有笔记。"
    }

    private func persist(_ card: CapturedCard) {
        do {
            try save(card.path, card.markdown)
            unsaved = nil
            resultWord = card.word
            correctedFrom = card.word != card.originalInput ? card.originalInput : nil
            word = card.word
            message = "已保存到词库。"
        } catch LibraryError.alreadyExists {
            unsaved = nil
            showExisting(card.word)
        } catch {
            unsaved = card
            message = "笔记未能保存，可重试或复制内容。\n" + error.localizedDescription
        }
    }
}

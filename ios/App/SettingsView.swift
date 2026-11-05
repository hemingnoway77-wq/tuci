import SwiftUI

struct SettingsView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @AppStorage("theme") private var theme: PaperTheme = .warm
    @AppStorage("apiBase") private var storedBase = ""
    @AppStorage("aiModel") private var storedModel = ""
    @AppStorage("classify") private var classify = true
    @State private var base = ""
    @State private var aiModel = ""
    @State private var key = ""
    @AppStorage("vaultPrefix") private var vaultPrefix = "英语单词"
    @State private var server = ""
    @State private var code = ""
    @State private var pairing = ""
    @State private var message: String?
    @State private var working = false
    @State private var replace = false
    @State private var confirmRecovery = false
    var body: some View {
        NavigationStack {
            Form {
                Section("外观") {
                    Picker("纸页主题", selection: $theme) {
                        ForEach(PaperTheme.allCases, id: \.self) { Text($0.rawValue).tag($0) }
                    }.pickerStyle(.segmented)
                }
                Section("单词收录") {
                    TextField("API 地址", text: $base).keyboardType(.URL).textInputAutocapitalization(.never).autocorrectionDisabled()
                    if base.lowercased().hasPrefix("http://") { Text("此地址使用明文 HTTP，API Key 和原句会经过当前网络。").font(.footnote).foregroundStyle(.secondary) }
                    TextField("模型名", text: $aiModel).textInputAutocapitalization(.never).autocorrectionDisabled()
                    SecureField("API Key", text: $key).textInputAutocapitalization(.never).autocorrectionDisabled()
                    Toggle("按主要词性分目录", isOn: $classify)
                    Button("保存 AI 配置") {
                        do {
                            try model.saveAIKey(key)
                            storedBase = base.trimmingCharacters(in: .whitespacesAndNewlines)
                            storedModel = aiModel.trimmingCharacters(in: .whitespacesAndNewlines)
                            message = "AI 配置已保存。"
                        } catch { message = error.localizedDescription }
                    }
                }
                Section("词库同步") {
                    if let c = model.connection {
                        LabeledContent("已连接", value: c.server)
                        if !model.status.isEmpty { Text(model.status).font(.footnote) }
                        if model.pending > 0 { LabeledContent("待上传", value: String(model.pending)) }
                        if model.needsRecovery { Button("重新核对词库") { confirmRecovery = true } }
                        Button("立即同步") { run { await model.synchronize() } }
                        Button("为另一台设备生成配对码") { run { pairing = try await model.pair() } }
                        if !pairing.isEmpty { LabeledContent("一次性配对码", value: pairing).textSelection(.enabled) }
                        Button("重新绑定此设备") { replace.toggle() }
                    }
                    if model.connection == nil || replace {
                        TextField("同步服务 HTTPS 地址", text: $server).keyboardType(.URL).textInputAutocapitalization(.never).autocorrectionDisabled()
                        SecureField("激活码或配对码", text: $code).textInputAutocapitalization(.never).autocorrectionDisabled()
                        Button("连接我的词库") {
                            run {
                                try await model.bind(server: server, code: code)
                                code = ""; replace = false
                                await model.synchronize()
                            }
                        }.disabled(code.isEmpty || server.isEmpty)
                    }
                    if working || model.busy { ProgressView() }
                }.disabled(working || model.busy)
                Section("笔记链接") {
                    TextField("Obsidian 中的词库相对目录", text: $vaultPrefix).textInputAutocapitalization(.never).autocorrectionDisabled()
                    Button("刷新词条链接") {
                        model.refreshLinks()
                        run { await model.synchronize() }
                    }
                }
            }.navigationTitle("设置")
                .toolbar { ToolbarItem(placement: .confirmationAction) { Button("完成") { dismiss() } } }
        }
        .confirmationDialog("备份本机笔记，再与服务器逐篇重新核对？", isPresented: $confirmRecovery, titleVisibility: .visible) {
            Button("备份并重新核对") { run { try await model.recover() } }
        }
        .onAppear { base = storedBase; aiModel = storedModel; key = model.apiKey; server = model.connection?.server ?? "" }
        .alert("设置", isPresented: Binding(get: { message != nil }, set: { if !$0 { message = nil } })) {
            Button("好") { message = nil }
        } message: { Text(message ?? "") }
    }
    private func run(_ action: @escaping @MainActor () async throws -> Void) {
        working = true
        Task { @MainActor in
            defer { working = false }
            do { try await action() } catch { message = error.localizedDescription }
        }
    }
}

import SwiftUI
import UniformTypeIdentifiers

struct MenuView: View {
    @State private var tab = 0

    var body: some View {
        TabView(selection: $tab) {
            NavigationStack { LayoutsView().closeButton() }
                .tabItem { Label("Layouts", systemImage: "square.grid.3x3.square") }.tag(0)
            NavigationStack { GamesView().closeButton() }
                .tabItem { Label("Games", systemImage: "gamecontroller") }.tag(1)
            NavigationStack { ConnectionTab().closeButton() }
                .tabItem { Label("PC", systemImage: "desktopcomputer") }.tag(2)
            NavigationStack { SettingsView().closeButton() }
                .tabItem { Label("Settings", systemImage: "gearshape") }.tag(3)
        }
        .tint(Theme.accent)
    }
}

private struct CloseButton: ViewModifier {
    @Environment(\.dismiss) var dismiss
    func body(content: Content) -> some View {
        content
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .navigationBarLeading) {
                    Button { dismiss() } label: { Label("Controller", systemImage: "chevron.backward") }
                }
            }
    }
}

extension View {
    func closeButton() -> some View { modifier(CloseButton()) }
}

// MARK: - Layouts

struct LayoutsView: View {
    @EnvironmentObject var store: LayoutStore
    @EnvironmentObject var model: AppModel
    @EnvironmentObject var conn: Connection
    @State private var editing: Layout?
    @State private var importing = false
    @State private var shareItem: ShareItem?

    struct ShareItem: Identifiable { let id = UUID(); let items: [Any] }

    var body: some View {
        List {
            Section {
                ForEach(store.layouts) { l in
                    HStack(spacing: 12) {
                        Image(systemName: store.activeId == l.id ? "checkmark.circle.fill" : "circle")
                            .foregroundColor(store.activeId == l.id ? Theme.accent : Theme.dim)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(l.name).font(.body.weight(.semibold))
                            Text(subtitle(l)).font(.caption).foregroundColor(Theme.dim).lineLimit(1)
                        }
                        Spacer()
                        Button { editing = l } label: { Image(systemName: "slider.horizontal.3") }.buttonStyle(.borderless)
                        Menu {
                            Button { _ = store.duplicate(l) } label: { Label("Duplicate", systemImage: "plus.square.on.square") }
                            Button { share(l, asLink: true) } label: { Label("Share link", systemImage: "link") }
                            Button { share(l, asLink: false) } label: { Label("Share file", systemImage: "doc") }
                            Button(role: .destructive) { store.delete(l.id) } label: { Label("Delete", systemImage: "trash") }
                        } label: { Image(systemName: "ellipsis.circle") }
                    }
                    .contentShape(Rectangle())
                    .onTapGesture { store.activate(l.id); model.show("Layout: \(l.name)") }
                }
                .onMove { store.move(from: $0, to: $1) }
            } header: {
                Text("Tap to use · \(Image(systemName: "slider.horizontal.3")) to edit")
            } footer: {
                Text("Give a layout PC game names (in its editor) and it switches on automatically when that game is focused on your PC.")
            }

            Section("Add") {
                Button { editing = newBlank() } label: { Label("New blank layout", systemImage: "plus") }
                Menu {
                    ForEach(Presets.all, id: \.name) { p in
                        Button(p.name) { store.add(p.reidentified()) }
                    }
                } label: { Label("Add a built-in layout", systemImage: "square.stack") }
                Button { importFromClipboard() } label: { Label("Paste shared link", systemImage: "doc.on.clipboard") }
                Button { importing = true } label: { Label("Import .tailpad file", systemImage: "square.and.arrow.down") }
            }
        }
        .navigationTitle("Layouts")
        .fullScreenCover(item: $editing) { l in EditorView(layout: l) }
        .sheet(item: $shareItem) { item in ShareSheet(items: item.items) }
        .fileImporter(isPresented: $importing, allowedContentTypes: [UTType(filenameExtension: "tailpad") ?? .json, .json]) { r in
            if case .success(let url) = r { model.open(url) }
        }
    }

    private func subtitle(_ l: Layout) -> String {
        var parts = [l.padType.title, "\(l.pages.reduce(0) { $0 + $1.controls.count }) controls"]
        if l.pages.count > 1 { parts.append("\(l.pages.count) pages") }
        if l.gyro.mode != .off { parts.append("gyro") }
        if !l.macros.isEmpty { parts.append("\(l.macros.count) macros") }
        if !l.games.isEmpty { parts.append("auto: " + l.games.joined(separator: ", ")) }
        return parts.joined(separator: " · ")
    }

    private func newBlank() -> Layout {
        let l = Layout(name: "My layout", pages: [LayoutPage(name: "Main", controls: [])])
        store.add(l, activate: false)
        return l
    }

    private func share(_ l: Layout, asLink: Bool) {
        if asLink, let url = store.shareURL(for: l) {
            shareItem = ShareItem(items: ["TailPad layout “\(l.name)” — open on an iPhone with TailPad:", url])
        } else if let file = store.exportFile(for: l) {
            shareItem = ShareItem(items: [file])
        }
    }

    private func importFromClipboard() {
        guard let s = UIPasteboard.general.string,
              let r = s.range(of: "tailpad://layout?d=[A-Za-z0-9_-]+", options: .regularExpression),
              let url = URL(string: String(s[r])) else {
            model.show("No TailPad link on the clipboard")
            return
        }
        model.open(url)
    }
}

struct ShareSheet: UIViewControllerRepresentable {
    let items: [Any]
    func makeUIViewController(context: Context) -> UIActivityViewController {
        UIActivityViewController(activityItems: items, applicationActivities: nil)
    }
    func updateUIViewController(_ vc: UIActivityViewController, context: Context) {}
}

// MARK: - Games

struct GamesView: View {
    @EnvironmentObject var conn: Connection
    @Environment(\.dismiss) var dismiss
    @State private var search = ""

    private var filtered: [PCGame] {
        search.isEmpty ? conn.games : conn.games.filter { $0.name.localizedCaseInsensitiveContains(search) }
    }

    var body: some View {
        Group {
            if !conn.status.isConnected {
                ContentUnavailable(title: "Not connected", text: "Connect to your PC to see its games.", icon: "wifi.slash")
            } else if conn.games.isEmpty {
                ContentUnavailable(title: "No games yet", text: "Steam and Epic games appear automatically. Add others in the PC app → Games.", icon: "gamecontroller")
            } else {
                ScrollView {
                    LazyVGrid(columns: [GridItem(.adaptive(minimum: 200), spacing: 14)], spacing: 14) {
                        ForEach(filtered) { g in
                            Button {
                                conn.launch(g)
                                DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) { dismiss() }
                            } label: { GameTile(game: g) }
                            .buttonStyle(.plain)
                        }
                    }
                    .padding()
                }
                .searchable(text: $search)
            }
        }
        .navigationTitle("Games on \(conn.pcName.isEmpty ? "PC" : conn.pcName)")
        .onAppear { conn.requestGames() }
        .refreshable { conn.requestGames() }
    }
}

struct GameTile: View {
    let game: PCGame
    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            ZStack {
                LinearGradient(colors: [Theme.panel, Theme.edge], startPoint: .topLeading, endPoint: .bottomTrailing)
                if let art = game.art {
                    AsyncImage(url: art) { img in img.resizable().scaledToFill() } placeholder: { Color.clear }
                } else {
                    Text(String(game.name.prefix(1))).font(.system(size: 36, weight: .heavy)).foregroundColor(Theme.dim)
                }
            }
            .frame(height: 94)
            .clipped()
            HStack {
                Text(game.name).font(.subheadline.weight(.semibold)).lineLimit(1)
                Spacer()
                Text(game.source).font(.caption2).foregroundColor(Theme.dim)
            }
            .padding(10)
        }
        .background(Theme.panel)
        .clipShape(RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).stroke(Theme.edge))
    }
}

struct ContentUnavailable: View {
    let title: String, text: String, icon: String
    var body: some View {
        VStack(spacing: 10) {
            Image(systemName: icon).font(.system(size: 34)).foregroundColor(Theme.dim)
            Text(title).font(.headline)
            Text(text).font(.subheadline).foregroundColor(Theme.dim).multilineTextAlignment(.center).frame(maxWidth: 380)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

// MARK: - PC connection

struct ConnectionTab: View {
    @EnvironmentObject var model: AppModel
    @EnvironmentObject var conn: Connection
    @EnvironmentObject var settings: AppSettings

    var body: some View {
        VStack(spacing: 0) {
            if conn.status.isConnected {
                HStack(spacing: 14) {
                    Image(systemName: "desktopcomputer").font(.title2).foregroundColor(Theme.accent)
                    VStack(alignment: .leading) {
                        Text("Connected to \(conn.pcName)").font(.headline)
                        Text("\(settings.host) · \(conn.pingMs.map { "\($0) ms" } ?? "…") · Player \(max(conn.player, 1))")
                            .font(.caption).foregroundColor(Theme.dim)
                    }
                    Spacer()
                    Button("Disconnect") { conn.disconnect() }.buttonStyle(.bordered)
                    Button("Forget PC", role: .destructive) {
                        conn.disconnect()
                        settings.forgetToken(for: settings.host)
                    }.buttonStyle(.bordered)
                }
                .padding()
                Divider()
            }
            ConnectView()
        }
        .navigationTitle("PC")
    }
}

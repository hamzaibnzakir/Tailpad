import Foundation

/// All layouts + which one is active. Saved as JSON in the app's Documents folder.
@MainActor
final class LayoutStore: ObservableObject {
    @Published private(set) var layouts: [Layout] = []
    @Published private(set) var activeId: UUID?
    /// the layout picked by hand — auto-switching returns to it when you leave a game
    private var manualId: UUID?
    @Published private(set) var autoSwitchedFor: String?

    private let fileURL: URL = {
        let dir = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        return dir.appendingPathComponent("layouts.json")
    }()

    private struct Saved: Codable { var layouts: [Layout]; var active: UUID? }

    init() {
        if let data = try? Data(contentsOf: fileURL), let saved = try? JSONDecoder().decode(Saved.self, from: data), !saved.layouts.isEmpty {
            layouts = saved.layouts
            activeId = saved.active ?? saved.layouts.first?.id
        } else {
            layouts = Presets.all
            activeId = layouts.first?.id
        }
        manualId = activeId
    }

    var active: Layout {
        layouts.first { $0.id == activeId } ?? layouts.first ?? Presets.xbox()
    }

    func save() {
        let saved = Saved(layouts: layouts, active: manualId ?? activeId)
        guard let data = try? JSONEncoder().encode(saved) else { return }
        try? data.write(to: fileURL, options: .atomic)
    }

    func activate(_ id: UUID) {
        activeId = id
        manualId = id
        autoSwitchedFor = nil
        save()
    }

    func update(_ layout: Layout) {
        if let i = layouts.firstIndex(where: { $0.id == layout.id }) { layouts[i] = layout } else { layouts.append(layout) }
        save()
    }

    func add(_ layout: Layout, activate: Bool = true) {
        layouts.append(layout)
        if activate { self.activate(layout.id) } else { save() }
    }

    func duplicate(_ layout: Layout) -> Layout {
        var copy = layout.reidentified()
        copy.name = layout.name + " copy"
        copy.games = []
        add(copy, activate: false)
        return copy
    }

    func delete(_ id: UUID) {
        layouts.removeAll { $0.id == id }
        if layouts.isEmpty { layouts = [Presets.xbox()] }
        if activeId == id || manualId == id { activate(layouts[0].id) } else { save() }
    }

    func move(from: IndexSet, to: Int) {
        layouts.move(fromOffsets: from, toOffset: to)
        save()
    }

    /// The PC told us which program is focused. Returns the layout we switched to, if any.
    @discardableResult
    func autoSwitch(exe: String) -> Layout? {
        let exe = exe.lowercased()
        if let match = layouts.first(where: { $0.games.contains { $0.lowercased() == exe } }) {
            if activeId != match.id { activeId = match.id; autoSwitchedFor = exe; return match }
            return nil
        }
        // left the game → back to the layout you picked
        if autoSwitchedFor != nil, let manualId, activeId != manualId {
            activeId = manualId
            autoSwitchedFor = nil
            return active
        }
        return nil
    }

    // MARK: sharing

    func shareURL(for layout: Layout) -> URL? {
        guard let json = try? JSONEncoder().encode(layout),
              let packed = Deflate.compress(json) else { return nil }
        var c = URLComponents()
        c.scheme = "tailpad"
        c.host = "layout"
        c.queryItems = [URLQueryItem(name: "d", value: packed.base64URLEncoded())]
        return c.url
    }

    func exportFile(for layout: Layout) -> URL? {
        let safe = layout.name.components(separatedBy: CharacterSet.alphanumerics.union(.whitespaces).inverted).joined()
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("\(safe.isEmpty ? "Layout" : safe).tailpad")
        guard let data = try? JSONEncoder().encode(layout), (try? data.write(to: url, options: .atomic)) != nil else { return nil }
        return url
    }

    enum ImportError: LocalizedError {
        case unreadable
        var errorDescription: String? { "That doesn't look like a TailPad layout." }
    }

    /// Accepts a tailpad://layout link, a .tailpad file, or raw layout JSON. Adds it and makes it active.
    @discardableResult
    func importLayout(from url: URL) throws -> Layout {
        if url.scheme == "tailpad" {
            guard let d = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems?.first(where: { $0.name == "d" })?.value,
                  let packed = Data(base64URLEncoded: d), let json = Deflate.decompress(packed) else { throw ImportError.unreadable }
            return try importLayout(json: json)
        }
        let access = url.startAccessingSecurityScopedResource()
        defer { if access { url.stopAccessingSecurityScopedResource() } }
        return try importLayout(json: Data(contentsOf: url))
    }

    @discardableResult
    func importLayout(json: Data) throws -> Layout {
        guard var layout = try? JSONDecoder().decode(Layout.self, from: json) else { throw ImportError.unreadable }
        layout = layout.reidentified()
        if layouts.contains(where: { $0.name == layout.name }) { layout.name += " (shared)" }
        add(layout)
        return layout
    }
}

// MARK: - App settings

@MainActor
final class AppSettings: ObservableObject {
    private let d = UserDefaults.standard

    @Published var host: String { didSet { d.set(host, forKey: "host") } }
    @Published var port: Int { didSet { d.set(port, forKey: "port") } }
    @Published var pcName: String { didSet { d.set(pcName, forKey: "pcName") } }
    @Published var haptics: Bool { didSet { d.set(haptics, forKey: "haptics") } }
    @Published var rumble: Bool { didSet { d.set(rumble, forKey: "rumble") } }
    @Published var autoSwitch: Bool { didSet { d.set(autoSwitch, forKey: "autoSwitch") } }
    @Published var sendMotion: Bool { didSet { d.set(sendMotion, forKey: "sendMotion") } }
    @Published var flipX: Bool { didSet { d.set(flipX, forKey: "flipX") } }
    @Published var flipY: Bool { didSet { d.set(flipY, forKey: "flipY") } }
    @Published var flipZ: Bool { didSet { d.set(flipZ, forKey: "flipZ") } }
    @Published var showStatus: Bool { didSet { d.set(showStatus, forKey: "showStatus") } }
    // remote play
    @Published var streamQuality: String { didSet { d.set(streamQuality, forKey: "streamQuality") } }
    @Published var streamAudio: Bool { didSet { d.set(streamAudio, forKey: "streamAudio") } }
    @Published var overlayOpacity: Double { didSet { d.set(overlayOpacity, forKey: "overlayOpacity") } }
    @Published var hideTouchWithController: Bool { didSet { d.set(hideTouchWithController, forKey: "hideTouchWithController") } }

    /// (height, fps, kbps) for the chosen stream quality
    var streamParams: (Int, Int, Int) {
        switch streamQuality {
        case "540p60": return (540, 60, 6_000)
        case "1080p60": return (1080, 60, 20_000)
        case "720p30": return (720, 30, 6_000)
        default: return (720, 60, 10_000)
        }
    }

    let deviceId: String

    init() {
        host = d.string(forKey: "host") ?? ""
        port = d.object(forKey: "port") as? Int ?? 47800
        pcName = d.string(forKey: "pcName") ?? ""
        haptics = d.object(forKey: "haptics") as? Bool ?? true
        rumble = d.object(forKey: "rumble") as? Bool ?? true
        autoSwitch = d.object(forKey: "autoSwitch") as? Bool ?? true
        sendMotion = d.object(forKey: "sendMotion") as? Bool ?? true
        flipX = d.bool(forKey: "flipX")
        flipY = d.bool(forKey: "flipY")
        flipZ = d.bool(forKey: "flipZ")
        showStatus = d.object(forKey: "showStatus") as? Bool ?? true
        streamQuality = d.string(forKey: "streamQuality") ?? "720p60"
        streamAudio = d.object(forKey: "streamAudio") as? Bool ?? true
        overlayOpacity = d.object(forKey: "overlayOpacity") as? Double ?? 0.5
        hideTouchWithController = d.object(forKey: "hideTouchWithController") as? Bool ?? true
        if let id = d.string(forKey: "deviceId") { deviceId = id } else {
            deviceId = UUID().uuidString
            d.set(deviceId, forKey: "deviceId")
        }
    }

    var isConfigured: Bool { !host.trimmingCharacters(in: .whitespaces).isEmpty }

    func token(for host: String) -> String { d.string(forKey: "token.\(host)") ?? "" }
    func setToken(_ t: String, for host: String) { d.set(t, forKey: "token.\(host)") }
    func forgetToken(for host: String) { d.removeObject(forKey: "token.\(host)") }
}

// MARK: - helpers

enum Deflate {
    static func compress(_ data: Data) -> Data? {
        try? (data as NSData).compressed(using: .zlib) as Data
    }
    static func decompress(_ data: Data) -> Data? {
        guard let out = try? (data as NSData).decompressed(using: .zlib) as Data, out.count < 4_000_000 else { return nil }
        return out
    }
}

extension Data {
    func base64URLEncoded() -> String {
        base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    init?(base64URLEncoded s: String) {
        var b = s.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        while b.count % 4 != 0 { b += "=" }
        self.init(base64Encoded: b)
    }
}

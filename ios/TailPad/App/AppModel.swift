import SwiftUI
import Combine

/// Wires everything together: layouts ⇄ touch engine ⇄ gyro ⇄ connection.
@MainActor
final class AppModel: ObservableObject {
    let settings = AppSettings()
    let store = LayoutStore()
    let mixer = InputMixer()
    let haptics = Haptics()
    let motion: MotionEngine
    let connection: Connection
    let engine: PadEngine

    @Published var toast: String?

    /// Launch arguments used by CI to screenshot each screen: -demoScreen play|editor|menu|games|connect -demoLayout N
    struct Demo { let screen: String; let layout: Int }
    let demo: Demo? = {
        let a = ProcessInfo.processInfo.arguments
        guard let i = a.firstIndex(of: "-demoScreen"), i + 1 < a.count else { return nil }
        let l = a.firstIndex(of: "-demoLayout").flatMap { $0 + 1 < a.count ? Int(a[$0 + 1]) : nil } ?? 0
        return Demo(screen: a[i + 1], layout: l)
    }()
    private var bag = Set<AnyCancellable>()

    init() {
        motion = MotionEngine(mixer: mixer)
        connection = Connection(mixer: mixer, haptics: haptics)
        engine = PadEngine(layout: store.active, mixer: mixer, motion: motion, haptics: haptics)
        engine.connection = connection

        connection.onTokenSaved = { [weak self] host, token in self?.settings.setToken(token, for: host) }
        connection.onPCName = { [weak self] name in self?.settings.pcName = name }
        connection.onForeground = { [weak self] exe in
            guard let self, self.settings.autoSwitch else { return }
            if let l = self.store.autoSwitch(exe: exe) { self.show("Layout: \(l.name)") }
        }

        // whenever the active layout changes (picked, edited, auto-switched) → reload everything
        store.$layouts.combineLatest(store.$activeId)
            // use the emitted values: @Published fires *before* the property is updated
            .map { layouts, id in layouts.first { $0.id == id } ?? layouts.first ?? Presets.xbox() }
            .removeDuplicates()
            .sink { [weak self] l in self?.apply(l) }
            .store(in: &bag)

        settings.objectWillChange
            .debounce(for: .milliseconds(50), scheduler: RunLoop.main)
            .sink { [weak self] in self?.applySettings() }
            .store(in: &bag)

        connection.$toast.compactMap { $0 }.sink { [weak self] t in self?.show(t) }.store(in: &bag)

        applySettings()
        apply(store.active)

        if let demo {
            if store.layouts.indices.contains(demo.layout) { store.activate(store.layouts[demo.layout].id) }
            if demo.screen != "connect" { connection.demo() }
        }
    }

    private func apply(_ l: Layout) {
        engine.load(l)
        connection.setPadType(l.padType)
        motion.configure(l.gyro, sendRaw: settings.sendMotion, flips: (settings.flipX, settings.flipY, settings.flipZ))
    }

    private func applySettings() {
        haptics.enabled = settings.haptics
        haptics.rumbleEnabled = settings.rumble
        motion.configure(store.active.gyro, sendRaw: settings.sendMotion, flips: (settings.flipX, settings.flipY, settings.flipZ))
    }

    func connect(code: String = "") {
        if demo != nil { return }
        let host = settings.host.trimmingCharacters(in: .whitespacesAndNewlines)
        connection.connect(host: host, port: settings.port, deviceId: settings.deviceId,
                           token: settings.token(for: host), code: code, padType: store.active.padType)
    }

    func show(_ text: String) {
        toast = text
        let current = text
        DispatchQueue.main.asyncAfter(deadline: .now() + 2.2) { [weak self] in
            if self?.toast == current { self?.toast = nil }
        }
    }

    /// tailpad://pair?host=…&port=…&code=…  or  tailpad://layout?d=…  or a .tailpad file
    func open(_ url: URL) {
        if url.scheme == "tailpad", url.host == "pair" {
            let q = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? []
            func v(_ k: String) -> String? { q.first { $0.name == k }?.value }
            if let h = v("host"), !h.isEmpty { settings.host = h }
            if let p = v("port").flatMap(Int.init) { settings.port = p }
            if let pc = v("pc") { settings.pcName = pc }
            connect(code: v("code") ?? "")
            return
        }
        do {
            let l = try store.importLayout(from: url)
            show("Added layout “\(l.name)”")
        } catch {
            show(error.localizedDescription)
        }
    }
}

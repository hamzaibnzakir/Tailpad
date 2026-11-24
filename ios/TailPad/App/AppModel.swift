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
            .map { [weak self] _, _ in self?.store.active }
            .compactMap { $0 }
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

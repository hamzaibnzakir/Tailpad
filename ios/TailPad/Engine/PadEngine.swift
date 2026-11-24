import UIKit

/// Turns touches on the current page into controller input, and keeps the visual state for drawing.
@MainActor
final class PadEngine: ObservableObject {
    struct Visual: Equatable {
        var pressed: Set<UUID> = []
        var pulled: Set<UUID> = []
        var toggled: Set<UUID> = []
        var knobs: [UUID: CGPoint] = [:]        // -1…1, y down
        var origins: [UUID: CGPoint] = [:]      // floating stick centre, in view points
        var dpad: [UUID: Set<PadButton>] = [:]
        var sliders: [UUID: Double] = [:]
        var touchpadDot: [UUID: CGPoint] = [:]
    }

    @Published private(set) var visual = Visual()
    @Published private(set) var pageIndex = 0
    @Published private(set) var layout: Layout

    let mixer: InputMixer
    let motion: MotionEngine
    let haptics: Haptics
    weak var connection: Connection?

    /// control frames in view coordinates, set by the view on layout
    var frames: [UUID: CGRect] = [:]

    private struct TouchState {
        let control: Control
        var origin: CGPoint
        var last: CGPoint
        var edgeOn = false
        var pullOn = false
        var touchpadVel = CGPoint.zero
    }
    private var touches: [ObjectIdentifier: TouchState] = [:]
    private var touchpadTimer: Timer?

    init(layout: Layout, mixer: InputMixer, motion: MotionEngine, haptics: Haptics) {
        self.layout = layout
        self.mixer = mixer
        self.motion = motion
        self.haptics = haptics
    }

    var page: LayoutPage { layout.pages[min(pageIndex, layout.pages.count - 1)] }

    func load(_ l: Layout) {
        let keepPage = l.id == layout.id ? pageIndex : 0
        releaseAll()
        visual.toggled = []
        motion.setToggle(false)
        layout = l
        pageIndex = min(keepPage, max(0, l.pages.count - 1))
    }

    func setPage(_ i: Int) {
        guard i != pageIndex, layout.pages.indices.contains(i) else { return }
        releaseAll()
        pageIndex = i
    }

    func releaseAll() {
        let active = Array(touches.values)
        touches.removeAll()
        for t in active { end(t, cancelled: true) }
        mixer.clearAll()
        visual.pressed = []; visual.pulled = []; visual.knobs = [:]; visual.origins = [:]
        visual.dpad = [:]; visual.touchpadDot = [:]
        visual.sliders = visual.sliders.filter { id, _ in page.controls.first { $0.id == id }?.spring == false }
        // re-apply sticky things (toggles, non-spring sliders) so they survive
        for c in page.controls {
            if visual.toggled.contains(c.id) { mixer.set(c.id, contribution(c.action, mode: .hold)) }
            if let v = visual.sliders[c.id] { applySlider(c, v) }
        }
    }

    // MARK: hit testing

    func control(at p: CGPoint) -> Control? {
        // topmost (last drawn) wins; small slop so edges aren't dead
        for c in page.controls.reversed() {
            guard let f = frames[c.id] else { continue }
            let slop: CGFloat = c.kind == .touchpad ? 0 : 6
            let hit: Bool
            switch c.shape {
            case .circle:
                let r = min(f.width, f.height) / 2 + slop
                hit = hypot(p.x - f.midX, p.y - f.midY) <= r
            default:
                hit = f.insetBy(dx: -slop, dy: -slop).contains(p)
            }
            if hit { return c }
        }
        return nil
    }

    // MARK: touches (called by TouchSurface)

    func began(_ id: ObjectIdentifier, at p: CGPoint) {
        guard let c = control(at: p) else { return }
        var t = TouchState(control: c, origin: p, last: p)
        if c.kind == .stick, let f = frames[c.id] { t.origin = c.floating ? p : CGPoint(x: f.midX, y: f.midY) }
        touches[id] = t
        switch c.kind {
        case .button: pressButton(c)
        case .stick:
            if c.floating { visual.origins[c.id] = p }
            moveStick(&touches[id]!, p)
        case .dpad: moveDpad(c, p)
        case .slider: moveSlider(c, p)
        case .touchpad:
            visual.touchpadDot[c.id] = p
            if c.touchOut == .rightStick { startTouchpadDecay() }
        }
    }

    func moved(_ id: ObjectIdentifier, to p: CGPoint) {
        guard var t = touches[id] else { return }
        let c = t.control
        switch c.kind {
        case .button:
            if let pull = c.pull, let f = frames[c.id] {
                let out = hypot(p.x - f.midX, p.y - f.midY) > max(f.width, f.height) * 0.85
                if out != t.pullOn {
                    t.pullOn = out
                    if out { visual.pulled.insert(c.id); mixer.set(pullKey(c.id), contribution(pull, mode: .hold)); haptics.tap() }
                    else { visual.pulled.remove(c.id); mixer.clear(pullKey(c.id)) }
                }
            }
        case .stick: moveStick(&t, p)
        case .dpad: moveDpad(c, p)
        case .slider: moveSlider(c, p)
        case .touchpad: moveTouchpad(c, &t, p)
        }
        t.last = p
        touches[id] = t
    }

    func ended(_ id: ObjectIdentifier) {
        guard let t = touches.removeValue(forKey: id) else { return }
        end(t, cancelled: false)
    }

    private func end(_ t: TouchState, cancelled: Bool) {
        let c = t.control
        switch c.kind {
        case .button:
            releaseButton(c)
            if t.pullOn { visual.pulled.remove(c.id); mixer.clear(pullKey(c.id)) }
        case .stick:
            visual.knobs[c.id] = nil
            visual.origins[c.id] = nil
            mixer.clear(c.id)
            if t.edgeOn { mixer.clear(edgeKey(c.id)); if let e = c.edge { runSideEffects(e, down: false) } }
        case .dpad:
            visual.dpad[c.id] = nil
            mixer.clear(c.id)
        case .slider:
            if c.spring { visual.sliders[c.id] = nil; mixer.clear(c.id) }
        case .touchpad:
            visual.touchpadDot[c.id] = nil
            if touches.values.contains(where: { $0.control.id == c.id }) == false { mixer.clear(c.id) }
        }
    }

    private func pullKey(_ id: UUID) -> UUID { derived(id, 1) }
    private func edgeKey(_ id: UUID) -> UUID { derived(id, 2) }
    private func derived(_ id: UUID, _ n: UInt8) -> UUID {
        var u = id.uuid
        u.15 ^= n
        return UUID(uuid: u)
    }

    // MARK: buttons

    private func contribution(_ a: PadAction, mode: ButtonMode, hz: Double = 12) -> Contribution {
        var c = Contribution()
        if mode == .turbo { c.turbo = a.buttonBits; c.turboHz = hz } else { c.buttons = a.buttonBits }
        c.lt = a.lt; c.rt = a.rt
        return c
    }

    private func pressButton(_ c: Control) {
        haptics.tap()
        if let p = c.action.page { setPage(p); return }
        switch c.mode {
        case .toggle:
            if visual.toggled.contains(c.id) {
                visual.toggled.remove(c.id)
                mixer.clear(c.id)
                if c.action.gyroHold { motion.setToggle(false) }
                if let m = layout.macro(c.action.macro), m.loop { connection?.stopMacro(m.id) }
            } else {
                visual.toggled.insert(c.id)
                mixer.set(c.id, contribution(c.action, mode: .hold))
                if c.action.gyroHold { motion.setToggle(true) }
                if let m = layout.macro(c.action.macro) { connection?.runMacro(m) }
            }
        case .hold, .turbo:
            visual.pressed.insert(c.id)
            mixer.set(c.id, contribution(c.action, mode: c.mode, hz: c.turboHz))
            runSideEffects(c.action, down: true)
        }
    }

    private func releaseButton(_ c: Control) {
        guard c.mode != .toggle, c.action.page == nil else { return }
        // another finger may still hold the same button
        if touches.values.contains(where: { $0.control.id == c.id }) { return }
        visual.pressed.remove(c.id)
        mixer.clear(c.id)
        runSideEffects(c.action, down: false)
    }

    private func runSideEffects(_ a: PadAction, down: Bool) {
        if a.gyroHold { motion.hold(down ? 1 : -1) }
        if let m = layout.macro(a.macro) {
            if down { connection?.runMacro(m) } else if m.loop { connection?.stopMacro(m.id) }
        }
    }

    // MARK: stick

    private func moveStick(_ t: inout TouchState, _ p: CGPoint) {
        let c = t.control
        guard let f = frames[c.id] else { return }
        let radius = min(f.width, f.height) * 0.42
        var dx = (p.x - t.origin.x) / radius, dy = (p.y - t.origin.y) / radius
        let mag = hypot(dx, dy)
        if mag > 1 { dx /= mag; dy /= mag }
        visual.knobs[c.id] = CGPoint(x: dx, y: dy)
        // radial dead zone, rescaled so movement starts smoothly just outside it
        var outX = 0.0, outY = 0.0
        let m = min(1, Double(mag))
        if m > c.deadzone, mag > 0 {
            let scaled = (m - c.deadzone) / (1 - c.deadzone)
            let len = Double(hypot(dx, dy))
            outX = Double(dx) / len * scaled
            outY = -Double(dy) / len * scaled
        }
        var con = Contribution()
        if c.stick == .left { con.lx = outX; con.ly = outY } else { con.rx = outX; con.ry = outY }
        mixer.set(c.id, con)

        if let e = c.edge {
            let on = m >= c.edgeAt
            if on != t.edgeOn {
                t.edgeOn = on
                if on { mixer.set(edgeKey(c.id), contribution(e, mode: .hold)); haptics.tap() } else { mixer.clear(edgeKey(c.id)) }
                runSideEffects(e, down: on)
            }
        }
    }

    // MARK: d-pad

    private func moveDpad(_ c: Control, _ p: CGPoint) {
        guard let f = frames[c.id] else { return }
        let dx = p.x - f.midX, dy = p.y - f.midY
        var dirs = Set<PadButton>()
        if hypot(dx, dy) > min(f.width, f.height) * 0.12 {
            let a = atan2(-dy, dx) * 180 / .pi // 0 = right, 90 = up
            let sector = c.eightWay ? 45.0 : 90.0
            let snapped = (Double(a) / sector).rounded() * sector
            let rad = snapped * .pi / 180
            let sx = cos(rad), sy = sin(rad)
            if sx > 0.38 { dirs.insert(.right) } else if sx < -0.38 { dirs.insert(.left) }
            if sy > 0.38 { dirs.insert(.up) } else if sy < -0.38 { dirs.insert(.down) }
        }
        if visual.dpad[c.id] != dirs {
            if !dirs.isEmpty { haptics.tap() }
            visual.dpad[c.id] = dirs
        }
        var con = Contribution()
        con.buttons = dirs.reduce(0) { $0 | $1.bit }
        mixer.set(c.id, con)
    }

    // MARK: slider (analog trigger / throttle)

    private func moveSlider(_ c: Control, _ p: CGPoint) {
        guard let f = frames[c.id] else { return }
        let v = Double(max(0, min(1, (f.maxY - p.y) / max(f.height, 1))))
        let v2 = v < 0.04 ? 0 : v
        visual.sliders[c.id] = v2
        applySlider(c, v2)
    }

    private func applySlider(_ c: Control, _ v: Double) {
        var con = Contribution()
        switch c.sliderTarget {
        case .lt: con.lt = v
        case .rt: con.rt = v
        case .leftUp: con.ly = v
        case .rightUp: con.ry = v
        }
        mixer.set(c.id, con)
    }

    // MARK: touchpad

    private func moveTouchpad(_ c: Control, _ t: inout TouchState, _ p: CGPoint) {
        let dx = p.x - t.last.x, dy = p.y - t.last.y
        visual.touchpadDot[c.id] = p
        switch c.touchOut {
        case .mouse:
            mixer.addMouse(Double(dx) * 2.2 * c.sensitivity, Double(dy) * 2.2 * c.sensitivity)
        case .rightStick:
            // finger speed → stick deflection; decays when the finger stops
            let k = 0.045 * c.sensitivity
            var con = Contribution()
            let (x, y) = InputMixer.clampStick(Double(dx) * k * 2, Double(-dy) * k * 2)
            con.rx = x; con.ry = y
            mixer.set(c.id, con)
            lastTouchpadMove[c.id] = ProcessInfo.processInfo.systemUptime
        }
    }

    private var lastTouchpadMove: [UUID: TimeInterval] = [:]

    private func startTouchpadDecay() {
        guard touchpadTimer == nil else { return }
        touchpadTimer = Timer.scheduledTimer(withTimeInterval: 1.0 / 60, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.decayTouchpads() }
        }
    }

    private func decayTouchpads() {
        let now = ProcessInfo.processInfo.systemUptime
        var anyActive = false
        for (id, last) in lastTouchpadMove {
            if now - last > 0.05 { mixer.clear(id); lastTouchpadMove[id] = nil } else { anyActive = true }
        }
        if !anyActive && !touches.values.contains(where: { $0.control.kind == .touchpad }) {
            touchpadTimer?.invalidate(); touchpadTimer = nil
        }
    }
}

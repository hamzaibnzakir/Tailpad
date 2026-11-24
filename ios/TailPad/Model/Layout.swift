import Foundation

// MARK: - Buttons (bit positions match docs/PROTOCOL.md)

enum PadButton: Int, Codable, CaseIterable, Identifiable {
    case a = 0, b, x, y, lb, rb, back, start, guide, ls, rs, up, down, left, right, touchpad
    var id: Int { rawValue }
    var bit: UInt32 { 1 << UInt32(rawValue) }

    func label(_ type: PadType) -> String {
        switch (self, type) {
        case (.a, .ds4): return "✕"
        case (.b, .ds4): return "○"
        case (.x, .ds4): return "□"
        case (.y, .ds4): return "△"
        case (.lb, .ds4): return "L1"
        case (.rb, .ds4): return "R1"
        case (.back, .ds4): return "Share"
        case (.start, .ds4): return "Options"
        case (.guide, .ds4): return "PS"
        case (.a, _): return "A"
        case (.b, _): return "B"
        case (.x, _): return "X"
        case (.y, _): return "Y"
        case (.lb, _): return "LB"
        case (.rb, _): return "RB"
        case (.back, _): return "View"
        case (.start, _): return "Menu"
        case (.guide, _): return "Guide"
        case (.ls, _): return "L3"
        case (.rs, _): return "R3"
        case (.up, _): return "▲"
        case (.down, _): return "▼"
        case (.left, _): return "◀"
        case (.right, _): return "▶"
        case (.touchpad, _): return "Touchpad"
        }
    }
}

enum PadType: String, Codable, CaseIterable, Identifiable {
    case x360, ds4
    var id: String { rawValue }
    var title: String { self == .ds4 ? "PlayStation" : "Xbox" }
}

// MARK: - Actions

/// What a button does. Several things can happen at once (e.g. LT + RB).
struct PadAction: Codable, Hashable {
    var buttons: [PadButton] = []
    var lt: Double = 0
    var rt: Double = 0
    var macro: UUID? = nil
    var page: Int? = nil
    var gyroHold: Bool = false

    var isEmpty: Bool { buttons.isEmpty && lt == 0 && rt == 0 && macro == nil && page == nil && !gyroHold }
    var buttonBits: UInt32 { buttons.reduce(0) { $0 | $1.bit } }

    init(buttons: [PadButton] = [], lt: Double = 0, rt: Double = 0, macro: UUID? = nil, page: Int? = nil, gyroHold: Bool = false) {
        self.buttons = buttons; self.lt = lt; self.rt = rt; self.macro = macro; self.page = page; self.gyroHold = gyroHold
    }

    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self)
        buttons = try c.decodeIfPresent([PadButton].self, forKey: .buttons) ?? []
        lt = try c.decodeIfPresent(Double.self, forKey: .lt) ?? 0
        rt = try c.decodeIfPresent(Double.self, forKey: .rt) ?? 0
        macro = try c.decodeIfPresent(UUID.self, forKey: .macro)
        page = try c.decodeIfPresent(Int.self, forKey: .page)
        gyroHold = try c.decodeIfPresent(Bool.self, forKey: .gyroHold) ?? false
    }
}

// MARK: - Controls

enum ControlKind: String, Codable, CaseIterable, Identifiable {
    case button, stick, dpad, slider, touchpad
    var id: String { rawValue }
    var title: String {
        switch self {
        case .button: return "Button"
        case .stick: return "Joystick"
        case .dpad: return "D-pad"
        case .slider: return "Trigger slider"
        case .touchpad: return "Touchpad"
        }
    }
}

enum ControlShape: String, Codable, CaseIterable, Identifiable {
    case circle, rounded, pill
    var id: String { rawValue }
}

enum ButtonMode: String, Codable, CaseIterable, Identifiable {
    case hold, toggle, turbo
    var id: String { rawValue }
    var title: String { self == .hold ? "Hold" : self == .toggle ? "Toggle (tap on / tap off)" : "Turbo (auto-repeat)" }
}

enum StickSide: String, Codable, CaseIterable, Identifiable {
    case left, right
    var id: String { rawValue }
}

enum SliderTarget: String, Codable, CaseIterable, Identifiable {
    case lt, rt, leftUp, rightUp
    var id: String { rawValue }
    var title: String {
        switch self {
        case .lt: return "Left trigger"
        case .rt: return "Right trigger"
        case .leftUp: return "Left stick (up)"
        case .rightUp: return "Right stick (up)"
        }
    }
}

enum TouchOutput: String, Codable, CaseIterable, Identifiable {
    case rightStick, mouse
    var id: String { rawValue }
    var title: String { self == .mouse ? "PC mouse" : "Right stick" }
}

struct Control: Codable, Identifiable, Hashable {
    var id = UUID()
    var kind: ControlKind
    /// centre, as a fraction of the pad's width / height
    var x: Double
    var y: Double
    /// size as a fraction of the pad's height (keeps shapes round on every phone)
    var w: Double
    var h: Double
    var shape: ControlShape = .circle
    var label: String = ""
    var color: String = "#2B3140"
    var textColor: String = "#E8EBF1"
    var opacity: Double = 1

    // button
    var action = PadAction()
    var mode: ButtonMode = .hold
    var turboHz: Double = 12
    /// drag your thumb outward to also trigger this (e.g. aim → fire)
    var pull: PadAction? = nil

    // stick
    var stick: StickSide = .left
    var deadzone: Double = 0.08
    var floating = false
    /// pushing the stick past `edgeAt` triggers this (e.g. auto-sprint)
    var edge: PadAction? = nil
    var edgeAt: Double = 0.92

    // d-pad
    var eightWay = true

    // slider
    var sliderTarget: SliderTarget = .rt
    var spring = true

    // touchpad
    var touchOut: TouchOutput = .rightStick
    var sensitivity: Double = 1

    init(kind: ControlKind, x: Double, y: Double, w: Double, h: Double? = nil, shape: ControlShape = .circle, label: String = "",
         color: String = "#2B3140", action: PadAction = .init()) {
        self.kind = kind; self.x = x; self.y = y; self.w = w; self.h = h ?? w
        self.shape = shape; self.label = label; self.color = color; self.action = action
    }

    enum CodingKeys: String, CodingKey {
        case id, kind, x, y, w, h, shape, label, color, textColor, opacity, action, mode, turboHz, pull,
             stick, deadzone, floating, edge, edgeAt, eightWay, sliderTarget, spring, touchOut, sensitivity
    }

    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self)
        id = try c.decodeIfPresent(UUID.self, forKey: .id) ?? UUID()
        kind = try c.decodeIfPresent(ControlKind.self, forKey: .kind) ?? .button
        x = try c.decodeIfPresent(Double.self, forKey: .x) ?? 0.5
        y = try c.decodeIfPresent(Double.self, forKey: .y) ?? 0.5
        let width = try c.decodeIfPresent(Double.self, forKey: .w) ?? 0.15
        w = width
        h = try c.decodeIfPresent(Double.self, forKey: .h) ?? width
        shape = try c.decodeIfPresent(ControlShape.self, forKey: .shape) ?? .circle
        label = try c.decodeIfPresent(String.self, forKey: .label) ?? ""
        color = try c.decodeIfPresent(String.self, forKey: .color) ?? "#2B3140"
        textColor = try c.decodeIfPresent(String.self, forKey: .textColor) ?? "#E8EBF1"
        opacity = try c.decodeIfPresent(Double.self, forKey: .opacity) ?? 1
        action = try c.decodeIfPresent(PadAction.self, forKey: .action) ?? .init()
        mode = try c.decodeIfPresent(ButtonMode.self, forKey: .mode) ?? .hold
        turboHz = try c.decodeIfPresent(Double.self, forKey: .turboHz) ?? 12
        pull = try c.decodeIfPresent(PadAction.self, forKey: .pull)
        stick = try c.decodeIfPresent(StickSide.self, forKey: .stick) ?? .left
        deadzone = try c.decodeIfPresent(Double.self, forKey: .deadzone) ?? 0.08
        floating = try c.decodeIfPresent(Bool.self, forKey: .floating) ?? false
        edge = try c.decodeIfPresent(PadAction.self, forKey: .edge)
        edgeAt = try c.decodeIfPresent(Double.self, forKey: .edgeAt) ?? 0.92
        eightWay = try c.decodeIfPresent(Bool.self, forKey: .eightWay) ?? true
        sliderTarget = try c.decodeIfPresent(SliderTarget.self, forKey: .sliderTarget) ?? .rt
        spring = try c.decodeIfPresent(Bool.self, forKey: .spring) ?? true
        touchOut = try c.decodeIfPresent(TouchOutput.self, forKey: .touchOut) ?? .rightStick
        sensitivity = try c.decodeIfPresent(Double.self, forKey: .sensitivity) ?? 1
    }

    /// Label shown on the control: custom text, or derived from what it does.
    func displayLabel(_ type: PadType) -> String {
        if !label.isEmpty { return label }
        switch kind {
        case .stick: return stick == .left ? "L" : "R"
        case .dpad: return ""
        case .touchpad: return touchOut == .mouse ? "Mouse" : "Look"
        case .slider: return sliderTarget == .lt ? (type == .ds4 ? "L2" : "LT") : sliderTarget == .rt ? (type == .ds4 ? "R2" : "RT") : "Throttle"
        case .button:
            if action.lt > 0 && action.buttons.isEmpty { return type == .ds4 ? "L2" : "LT" }
            if action.rt > 0 && action.buttons.isEmpty { return type == .ds4 ? "R2" : "RT" }
            if action.gyroHold { return "Gyro" }
            if let p = action.page { return "Page \(p + 1)" }
            return action.buttons.map { $0.label(type) }.joined(separator: "+")
        }
    }
}

// MARK: - Macros

struct MacroStep: Codable, Hashable, Identifiable {
    var id = UUID()
    var buttons: [PadButton] = []
    var lt: Double = 0
    var rt: Double = 0
    var lx: Double = 0
    var ly: Double = 0
    var ms: Int = 60

    init(buttons: [PadButton] = [], lt: Double = 0, rt: Double = 0, lx: Double = 0, ly: Double = 0, ms: Int = 60) {
        self.buttons = buttons; self.lt = lt; self.rt = rt; self.lx = lx; self.ly = ly; self.ms = ms
    }

    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self)
        id = try c.decodeIfPresent(UUID.self, forKey: .id) ?? UUID()
        buttons = try c.decodeIfPresent([PadButton].self, forKey: .buttons) ?? []
        lt = try c.decodeIfPresent(Double.self, forKey: .lt) ?? 0
        rt = try c.decodeIfPresent(Double.self, forKey: .rt) ?? 0
        lx = try c.decodeIfPresent(Double.self, forKey: .lx) ?? 0
        ly = try c.decodeIfPresent(Double.self, forKey: .ly) ?? 0
        ms = try c.decodeIfPresent(Int.self, forKey: .ms) ?? 60
    }

    var json: [String: Any] {
        ["b": buttons.reduce(UInt32(0)) { $0 | $1.bit }, "lt": lt, "rt": rt, "lx": lx, "ly": ly, "ms": ms]
    }
}

struct Macro: Codable, Hashable, Identifiable {
    var id = UUID()
    var name: String
    var steps: [MacroStep]
    /// keep repeating while the button is held
    var loop = false

    init(name: String, steps: [MacroStep], loop: Bool = false) { self.name = name; self.steps = steps; self.loop = loop }

    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self)
        id = try c.decodeIfPresent(UUID.self, forKey: .id) ?? UUID()
        name = try c.decodeIfPresent(String.self, forKey: .name) ?? "Macro"
        steps = try c.decodeIfPresent([MacroStep].self, forKey: .steps) ?? []
        loop = try c.decodeIfPresent(Bool.self, forKey: .loop) ?? false
    }
}

// MARK: - Gyro

enum GyroMode: String, Codable, CaseIterable, Identifiable {
    case off, always, whileButton, whileAiming
    var id: String { rawValue }
    var title: String {
        switch self {
        case .off: return "Off"
        case .always: return "Always on"
        case .whileButton: return "While a Gyro button is held/on"
        case .whileAiming: return "While aiming (left trigger held)"
        }
    }
}

enum GyroTarget: String, Codable, CaseIterable, Identifiable {
    case rightStick, mouse, steer
    var id: String { rawValue }
    var title: String {
        switch self {
        case .rightStick: return "Right stick (aim / camera)"
        case .mouse: return "PC mouse (aim)"
        case .steer: return "Steering (tilt → left stick)"
        }
    }
}

struct GyroConfig: Codable, Hashable {
    var mode: GyroMode = .off
    var target: GyroTarget = .rightStick
    var sensitivity: Double = 1
    var invertX = false
    var invertY = false

    init(mode: GyroMode = .off, target: GyroTarget = .rightStick, sensitivity: Double = 1) {
        self.mode = mode; self.target = target; self.sensitivity = sensitivity
    }

    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self)
        mode = try c.decodeIfPresent(GyroMode.self, forKey: .mode) ?? .off
        target = try c.decodeIfPresent(GyroTarget.self, forKey: .target) ?? .rightStick
        sensitivity = try c.decodeIfPresent(Double.self, forKey: .sensitivity) ?? 1
        invertX = try c.decodeIfPresent(Bool.self, forKey: .invertX) ?? false
        invertY = try c.decodeIfPresent(Bool.self, forKey: .invertY) ?? false
    }
}

// MARK: - Layout

struct LayoutPage: Codable, Hashable, Identifiable {
    var id = UUID()
    var name: String
    var controls: [Control]

    init(name: String, controls: [Control]) { self.name = name; self.controls = controls }

    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self)
        id = try c.decodeIfPresent(UUID.self, forKey: .id) ?? UUID()
        name = try c.decodeIfPresent(String.self, forKey: .name) ?? "Page"
        controls = try c.decodeIfPresent([Control].self, forKey: .controls) ?? []
    }
}

struct Layout: Codable, Hashable, Identifiable {
    var id = UUID()
    var name: String
    var padType: PadType = .x360
    /// PC programs (e.g. "eldenring.exe") that switch to this layout automatically
    var games: [String] = []
    var pages: [LayoutPage]
    var macros: [Macro] = []
    var gyro = GyroConfig()
    var background: String = "#0E1014"

    init(name: String, padType: PadType = .x360, pages: [LayoutPage], macros: [Macro] = [], gyro: GyroConfig = .init(), games: [String] = []) {
        self.name = name; self.padType = padType; self.pages = pages; self.macros = macros; self.gyro = gyro; self.games = games
    }

    init(from d: Decoder) throws {
        let c = try d.container(keyedBy: CodingKeys.self)
        id = try c.decodeIfPresent(UUID.self, forKey: .id) ?? UUID()
        name = try c.decodeIfPresent(String.self, forKey: .name) ?? "Layout"
        padType = try c.decodeIfPresent(PadType.self, forKey: .padType) ?? .x360
        games = try c.decodeIfPresent([String].self, forKey: .games) ?? []
        let decodedPages = try c.decodeIfPresent([LayoutPage].self, forKey: .pages) ?? []
        pages = decodedPages.isEmpty ? [LayoutPage(name: "Main", controls: [])] : decodedPages
        macros = try c.decodeIfPresent([Macro].self, forKey: .macros) ?? []
        gyro = try c.decodeIfPresent(GyroConfig.self, forKey: .gyro) ?? .init()
        background = try c.decodeIfPresent(String.self, forKey: .background) ?? "#0E1014"
    }

    func macro(_ id: UUID?) -> Macro? { id.flatMap { mid in macros.first { $0.id == mid } } }

    /// Fresh ids everywhere — used when importing a shared layout so it never collides with yours.
    func reidentified() -> Layout {
        var l = self
        l.id = UUID()
        var macroMap: [UUID: UUID] = [:]
        l.macros = macros.map { var m = $0; let n = UUID(); macroMap[m.id] = n; m.id = n; return m }
        func fix(_ a: PadAction?) -> PadAction? {
            guard var a else { return nil }
            if let m = a.macro { a.macro = macroMap[m] }
            return a
        }
        l.pages = pages.map { p in
            var p = p
            p.id = UUID()
            p.controls = p.controls.map { c in
                var c = c
                c.id = UUID()
                c.action = fix(c.action)!
                c.pull = fix(c.pull)
                c.edge = fix(c.edge)
                return c
            }
            return p
        }
        return l
    }
}

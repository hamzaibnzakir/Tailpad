import Foundation

/// Built-in layouts. Users duplicate and edit these; the originals are restorable from Layouts → Add.
enum Presets {
    static let base = "#232936", shoulder = "#1B2029"

    static func btn(_ b: PadButton, _ x: Double, _ y: Double, _ s: Double = 0.16, color: String = Presets.base, label: String = "") -> Control {
        Control(kind: .button, x: x, y: y, w: s, label: label, color: color, action: PadAction(buttons: [b]))
    }

    static func pill(_ action: PadAction, _ x: Double, _ y: Double, w: Double = 0.2, h: Double = 0.11, label: String = "", color: String = Presets.shoulder) -> Control {
        Control(kind: .button, x: x, y: y, w: w, h: h, shape: .rounded, label: label, color: color, action: action)
    }

    static func stick(_ side: StickSide, _ x: Double, _ y: Double, _ s: Double) -> Control {
        var c = Control(kind: .stick, x: x, y: y, w: s, color: "#161A21")
        c.stick = side
        return c
    }

    static func dpad(_ x: Double, _ y: Double, _ s: Double, eight: Bool = true) -> Control {
        var c = Control(kind: .dpad, x: x, y: y, w: s, color: base)
        c.eightWay = eight
        return c
    }

    static func shoulders(_ t: PadType) -> [Control] {
        [
            pill(PadAction(lt: 1), 0.07, 0.1, w: 0.24),
            pill(PadAction(buttons: [.lb]), 0.215, 0.1, w: 0.22),
            pill(PadAction(buttons: [.rb]), 0.785, 0.1, w: 0.22),
            pill(PadAction(rt: 1), 0.93, 0.1, w: 0.24),
        ]
    }

    static func systemRow(_ t: PadType, y: Double = 0.14) -> [Control] {
        [
            btn(.back, 0.42, y, 0.11, color: shoulder),
            btn(.guide, 0.5, y, 0.12, color: shoulder, label: t == .ds4 ? "PS" : "⊗"),
            btn(.start, 0.58, y, 0.11, color: shoulder),
        ]
    }

    static func xbox() -> Layout {
        var c = shoulders(.x360) + systemRow(.x360)
        c += [
            stick(.left, 0.17, 0.56, 0.5),
            dpad(0.36, 0.8, 0.34),
            stick(.right, 0.64, 0.8, 0.34),
            btn(.y, 0.845, 0.31, color: "#3A3520"), btn(.a, 0.845, 0.72, color: "#1F3A2A"),
            btn(.x, 0.76, 0.515, color: "#1F2E40"), btn(.b, 0.93, 0.515, color: "#3D2222"),
            btn(.ls, 0.44, 0.42, 0.1, color: shoulder), btn(.rs, 0.56, 0.42, 0.1, color: shoulder),
        ]
        return Layout(name: "Xbox Classic", padType: .x360, pages: [LayoutPage(name: "Main", controls: c)])
    }

    static func playstation() -> Layout {
        var l = xbox()
        l.name = "PlayStation"
        l.padType = .ds4
        var controls = l.pages[0].controls
        for i in controls.indices where controls[i].kind == .button {
            switch controls[i].action.buttons.first {
            case .y: controls[i].color = "#1D3A35"; controls[i].textColor = "#4FD1B5"
            case .b: controls[i].color = "#3D2228"; controls[i].textColor = "#FF6B7A"
            case .a: controls[i].color = "#1F2A44"; controls[i].textColor = "#7FA8FF"
            case .x: controls[i].color = "#3A2238"; controls[i].textColor = "#F59ADF"
            default: break
            }
        }
        controls.append(pill(PadAction(buttons: [.touchpad]), 0.5, 0.29, w: 0.36, h: 0.1, label: "Touchpad"))
        l.pages[0].controls = controls
        return l
    }

    static func shooter() -> Layout {
        var look = Control(kind: .touchpad, x: 0.68, y: 0.55, w: 0.95, h: 0.62, shape: .rounded, color: "#12151B")
        look.touchOut = .rightStick
        look.sensitivity = 1.2
        var move = stick(.left, 0.18, 0.62, 0.52)
        move.floating = true
        move.edge = PadAction(buttons: [.ls])     // push to the edge = sprint
        var aim = pill(PadAction(lt: 1), 0.09, 0.3, w: 0.2, h: 0.2, label: "AIM", color: "#2A2440")
        aim.pull = PadAction(rt: 1)                // drag outward while aiming = fire
        aim.shape = .circle
        let fire = Control(kind: .button, x: 0.91, y: 0.68, w: 0.24, label: "FIRE", color: "#4A2222", action: PadAction(rt: 1))
        let c: [Control] = [
            look, move,
            pill(PadAction(buttons: [.lb]), 0.2, 0.1, w: 0.22, label: "LB"),
            pill(PadAction(buttons: [.rb]), 0.8, 0.1, w: 0.22, label: "RB"),
            btn(.back, 0.4, 0.22, 0.1, color: shoulder), btn(.start, 0.6, 0.22, 0.1, color: shoulder),
            aim, fire,
            btn(.a, 0.91, 0.36, 0.15, label: "JUMP"), btn(.b, 0.8, 0.86, 0.13, label: "CRCH"),
            btn(.x, 0.77, 0.6, 0.13, label: "RELD"), btn(.y, 0.79, 0.33, 0.12, label: "SWAP"),
            btn(.rs, 0.66, 0.86, 0.11, label: "MELEE"), dpad(0.37, 0.84, 0.26, eight: false),
        ]
        return Layout(name: "Shooter (gyro aim)", pages: [LayoutPage(name: "Main", controls: c)],
                      gyro: GyroConfig(mode: .whileAiming, target: .rightStick, sensitivity: 1))
    }

    static func racing() -> Layout {
        var gas = Control(kind: .slider, x: 0.91, y: 0.58, w: 0.2, h: 0.72, shape: .rounded, label: "GAS", color: "#1F3A2A")
        gas.sliderTarget = .rt
        var brake = Control(kind: .slider, x: 0.09, y: 0.58, w: 0.2, h: 0.72, shape: .rounded, label: "BRAKE", color: "#3D2222")
        brake.sliderTarget = .lt
        let c: [Control] = [
            gas, brake,
            pill(PadAction(buttons: [.lb]), 0.24, 0.12, w: 0.22, label: "LB"),
            pill(PadAction(buttons: [.rb]), 0.76, 0.12, w: 0.22, label: "RB"),
            btn(.a, 0.75, 0.75, 0.17, label: "A"), btn(.b, 0.75, 0.45, 0.15, label: "B"),
            btn(.x, 0.25, 0.45, 0.15, label: "X"), btn(.y, 0.25, 0.75, 0.17, label: "Y"),
            btn(.back, 0.4, 0.26, 0.11, color: shoulder), btn(.start, 0.6, 0.26, 0.11, color: shoulder),
            dpad(0.5, 0.72, 0.36, eight: false),
        ]
        return Layout(name: "Racing (tilt to steer)", pages: [LayoutPage(name: "Main", controls: c)],
                      gyro: GyroConfig(mode: .always, target: .steer, sensitivity: 1))
    }

    static func fighting() -> Layout {
        let fireball = Macro(name: "Fireball", steps: [
            MacroStep(buttons: [.down], ms: 40), MacroStep(buttons: [.down, .right], ms: 40),
            MacroStep(buttons: [.right], ms: 40), MacroStep(buttons: [.right, .x], ms: 60),
        ])
        let uppercut = Macro(name: "Uppercut", steps: [
            MacroStep(buttons: [.right], ms: 40), MacroStep(buttons: [.down], ms: 40),
            MacroStep(buttons: [.down, .right], ms: 40), MacroStep(buttons: [.down, .right, .x], ms: 60),
        ])
        let c: [Control] = [
            dpad(0.17, 0.6, 0.6, eight: true),
            btn(.x, 0.68, 0.43, 0.16, label: "LP"), btn(.y, 0.8, 0.37, 0.16, label: "MP"), btn(.rb, 0.92, 0.43, 0.16, label: "HP"),
            btn(.a, 0.68, 0.75, 0.16, label: "LK"), btn(.b, 0.8, 0.69, 0.16, label: "MK"),
            Control(kind: .button, x: 0.92, y: 0.75, w: 0.16, label: "HK", color: base, action: PadAction(rt: 1)),
            pill(PadAction(macro: fireball.id), 0.38, 0.24, w: 0.24, label: "Fireball", color: "#3A2A18"),
            pill(PadAction(macro: uppercut.id), 0.62, 0.24, w: 0.24, label: "Uppercut", color: "#3A2A18"),
            btn(.start, 0.92, 0.1, 0.11, color: shoulder), btn(.back, 0.08, 0.1, 0.11, color: shoulder),
            pill(PadAction(buttons: [.lb]), 0.5, 0.9, w: 0.2, label: "LB"),
        ]
        return Layout(name: "Fighting (with combos)", pages: [LayoutPage(name: "Main", controls: c)], macros: [fireball, uppercut])
    }

    static func retro() -> Layout {
        let main: [Control] = [
            dpad(0.17, 0.6, 0.52, eight: true),
            btn(.b, 0.75, 0.72, 0.19, label: "B"), btn(.a, 0.9, 0.56, 0.19, label: "A"),
            btn(.y, 0.69, 0.46, 0.15, label: "Y"), btn(.x, 0.84, 0.3, 0.15, label: "X"),
            pill(PadAction(buttons: [.lb]), 0.12, 0.1, w: 0.24, label: "L"),
            pill(PadAction(buttons: [.rb]), 0.88, 0.1, w: 0.24, label: "R"),
            pill(PadAction(buttons: [.back]), 0.43, 0.88, w: 0.16, h: 0.09, label: "SELECT"),
            pill(PadAction(buttons: [.start]), 0.57, 0.88, w: 0.16, h: 0.09, label: "START"),
            pill(PadAction(page: 1), 0.5, 0.3, w: 0.18, h: 0.09, label: "More ›", color: "#2A2440"),
        ]
        let extra: [Control] = [
            stick(.left, 0.2, 0.58, 0.5), stick(.right, 0.8, 0.58, 0.5),
            pill(PadAction(lt: 1), 0.12, 0.1, w: 0.24, label: "ZL"),
            pill(PadAction(rt: 1), 0.88, 0.1, w: 0.24, label: "ZR"),
            btn(.ls, 0.42, 0.55, 0.13), btn(.rs, 0.58, 0.55, 0.13),
            btn(.guide, 0.5, 0.82, 0.13, label: "HOME"),
            pill(PadAction(page: 0), 0.5, 0.3, w: 0.18, h: 0.09, label: "‹ Back", color: "#2A2440"),
        ]
        return Layout(name: "Retro / Emulators", pages: [LayoutPage(name: "Main", controls: main), LayoutPage(name: "Extra", controls: extra)])
    }

    static var all: [Layout] { [xbox(), playstation(), shooter(), racing(), fighting(), retro()] }
}

import SwiftUI

/// Draws one control in its current state.
struct ControlView: View {
    let control: Control
    let padType: PadType
    var visual = PadEngine.Visual()
    var selected = false
    var showFloatingBase = true

    private var fill: Color { Color(hex: control.color) }
    private var ink: Color { Color(hex: control.textColor) }
    private var pressed: Bool { visual.pressed.contains(control.id) || visual.toggled.contains(control.id) }

    var body: some View {
        GeometryReader { geo in
            let s = min(geo.size.width, geo.size.height)
            ZStack {
                switch control.kind {
                case .button: button(s)
                case .stick: stick(geo.size)
                case .dpad: dpad(geo.size)
                case .slider: slider(geo.size)
                case .touchpad: touchpad(geo.size)
                }
                if selected {
                    ControlShapeView(shape: control.kind == .dpad || control.kind == .stick ? .circle : control.shape)
                        .stroke(Theme.accent, style: StrokeStyle(lineWidth: 2, dash: [6, 4]))
                }
            }
            .frame(width: geo.size.width, height: geo.size.height)
        }
        .opacity(control.opacity)
    }

    // MARK: button

    @ViewBuilder private func button(_ s: CGFloat) -> some View {
        let toggled = visual.toggled.contains(control.id)
        let pulled = visual.pulled.contains(control.id)
        ZStack {
            ControlShapeView(shape: control.shape)
                .fill(fill)
                .overlay(ControlShapeView(shape: control.shape).fill(Color.white.opacity(pressed ? 0.22 : 0)))
                .overlay(ControlShapeView(shape: control.shape)
                    .stroke(toggled ? Theme.accent : pulled ? Theme.warn : Color.white.opacity(0.12), lineWidth: toggled || pulled ? 3 : 1.5))
            if control.pull != nil {
                ControlShapeView(shape: control.shape)
                    .stroke(Theme.warn.opacity(pulled ? 0.9 : 0.25), style: StrokeStyle(lineWidth: 1.5, dash: [3, 5]))
                    .scaleEffect(1.7)
            }
            Text(control.displayLabel(padType))
                .font(.system(size: max(9, s * (control.displayLabel(padType).count > 3 ? 0.22 : 0.38)), weight: .bold, design: .rounded))
                .minimumScaleFactor(0.5)
                .lineLimit(1)
                .padding(.horizontal, 4)
                .foregroundColor(ink)
            if control.mode == .turbo {
                Text("TURBO").font(.system(size: max(7, s * 0.13), weight: .heavy)).foregroundColor(Theme.warn)
                    .offset(y: s * 0.3)
            }
        }
        .scaleEffect(pressed && !toggled ? 0.93 : 1)
        .animation(.easeOut(duration: 0.06), value: pressed)
    }

    // MARK: stick

    @ViewBuilder private func stick(_ size: CGSize) -> some View {
        let s = min(size.width, size.height)
        let k = visual.knobs[control.id] ?? .zero
        let active = visual.knobs[control.id] != nil
        let floatingActive = control.floating && active
        ZStack {
            Circle().fill(fill.opacity(floatingActive ? 0.25 : 1))
                .overlay(Circle().stroke(Color.white.opacity(active ? 0.3 : 0.12), lineWidth: 1.5))
            if control.edge != nil {
                Circle().stroke(Theme.warn.opacity(0.3), style: StrokeStyle(lineWidth: 1, dash: [2, 6]))
                    .scaleEffect(CGFloat(control.edgeAt) * 0.84)
            }
            if !floatingActive {
                Circle()
                    .fill(Color(hex: "#2B3140"))
                    .overlay(Circle().stroke(Color.white.opacity(0.25), lineWidth: 2))
                    .overlay(Text(control.displayLabel(padType)).font(.system(size: s * 0.1, weight: .bold)).foregroundColor(Theme.dim))
                    .frame(width: s * 0.44, height: s * 0.44)
                    .offset(x: k.x * s * 0.42, y: k.y * s * 0.42)
                    .shadow(color: .black.opacity(0.5), radius: 6, y: 3)
            }
            if control.floating && !active {
                Text("touch anywhere").font(.system(size: s * 0.06)).foregroundColor(Theme.dim).offset(y: s * 0.36)
            }
        }
    }

    // MARK: d-pad

    @ViewBuilder private func dpad(_ size: CGSize) -> some View {
        let s = min(size.width, size.height)
        let on = visual.dpad[control.id] ?? []
        let arm = s * 0.34
        ZStack {
            ForEach([PadButton.up, .down, .left, .right], id: \.self) { d in
                let offset: CGSize = {
                    switch d {
                    case .up: return CGSize(width: 0, height: -arm)
                    case .down: return CGSize(width: 0, height: arm)
                    case .left: return CGSize(width: -arm, height: 0)
                    default: return CGSize(width: arm, height: 0)
                    }
                }()
                RoundedRectangle(cornerRadius: s * 0.06)
                    .fill(fill)
                    .overlay(RoundedRectangle(cornerRadius: s * 0.06).fill(Color.white.opacity(on.contains(d) ? 0.25 : 0)))
                    .overlay(RoundedRectangle(cornerRadius: s * 0.06).stroke(Color.white.opacity(0.12), lineWidth: 1.5))
                    .overlay(Text(d.label(padType)).font(.system(size: s * 0.11)).foregroundColor(ink.opacity(0.8)))
                    .frame(width: s * 0.32, height: s * 0.32)
                    .offset(offset)
            }
            Circle().fill(fill).frame(width: s * 0.2, height: s * 0.2)
        }
    }

    // MARK: slider

    @ViewBuilder private func slider(_ size: CGSize) -> some View {
        let v = visual.sliders[control.id] ?? 0
        ZStack(alignment: .bottom) {
            ZStack(alignment: .bottom) {
                Rectangle().fill(fill.opacity(0.45))
                Rectangle().fill(fill).frame(height: size.height * CGFloat(v))
            }
            .frame(width: size.width, height: size.height)
            .clipShape(ControlShapeView(shape: control.shape))
            ControlShapeView(shape: control.shape).stroke(Color.white.opacity(0.15), lineWidth: 1.5)
            VStack {
                Text(control.displayLabel(padType)).font(.system(size: min(size.width * 0.22, 18), weight: .bold)).foregroundColor(ink)
                Text("\(Int(v * 100))%").font(.system(size: min(size.width * 0.16, 13), weight: .medium, design: .monospaced))
                    .foregroundColor(ink.opacity(0.7))
            }
            .frame(maxHeight: .infinity)
        }
    }

    // MARK: touchpad

    @ViewBuilder private func touchpad(_ size: CGSize) -> some View {
        ZStack {
            ControlShapeView(shape: control.shape).fill(fill.opacity(0.6))
            ControlShapeView(shape: control.shape).stroke(Color.white.opacity(0.08), style: StrokeStyle(lineWidth: 1, dash: [5, 7]))
            Text(control.displayLabel(padType).uppercased())
                .font(.system(size: 11, weight: .semibold)).tracking(2).foregroundColor(Theme.dim.opacity(0.6))
        }
    }
}

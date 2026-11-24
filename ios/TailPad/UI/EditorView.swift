import SwiftUI

/// Full-screen layout editor: drag to move, drag the corner to resize, tap to edit a control.
struct EditorView: View {
    @EnvironmentObject var store: LayoutStore
    @Environment(\.dismiss) var dismiss
    @State var draft: Layout
    @State private var pageIndex = 0
    @State private var selected: UUID?
    @State private var showLayoutSettings = false
    @State private var showMacros = false
    @State private var snap = true
    @State private var dragStart: [UUID: (Double, Double)] = [:]
    @State private var sizeStart: [UUID: (Double, Double)] = [:]
    @State private var confirmDiscard = false
    private let original: Layout

    init(layout: Layout, select: UUID? = nil) {
        _draft = State(initialValue: layout)
        _selected = State(initialValue: select)
        original = layout
    }

    private var controls: [Control] { draft.pages[pageIndex].controls }

    private func binding(_ id: UUID) -> Binding<Control>? {
        guard let i = draft.pages[pageIndex].controls.firstIndex(where: { $0.id == id }) else { return nil }
        return Binding(
            get: { draft.pages[pageIndex].controls.indices.contains(i) ? draft.pages[pageIndex].controls[i] : Control(kind: .button, x: 0, y: 0, w: 0.1) },
            set: { if draft.pages[pageIndex].controls.indices.contains(i) { draft.pages[pageIndex].controls[i] = $0 } })
    }

    var body: some View {
        GeometryReader { geo in
            let size = geo.size
            ZStack(alignment: .top) {
                Color(hex: draft.background).ignoresSafeArea()
                grid(size).allowsHitTesting(false)
                Color.clear.contentShape(Rectangle()).onTapGesture { selected = nil }

                ForEach(controls) { c in
                    let f = controlFrame(c, in: size)
                    ControlView(control: c, padType: draft.padType, selected: selected == c.id)
                        .frame(width: f.width, height: f.height)
                        .contentShape(Rectangle())
                        .position(x: f.midX, y: f.midY)
                        .gesture(moveGesture(c, size))
                        .onTapGesture { selected = c.id }
                    if selected == c.id {
                        resizeHandle(c, size)
                            .position(x: f.maxX, y: f.maxY)
                    }
                }

                toolbar
                    .padding(.top, 6)

                if let id = selected, let b = binding(id) {
                    let onLeft = b.wrappedValue.x > 0.5
                    HStack {
                        if !onLeft { Spacer() }
                        ControlInspector(control: b, layout: $draft, pageCount: draft.pages.count,
                                         onDelete: { delete(id) }, onDuplicate: { duplicate(id) }, onFront: { bringToFront(id) },
                                         onClose: { selected = nil })
                            .frame(width: min(340, size.width * 0.42))
                            .frame(maxHeight: size.height - 50)
                            .background(RoundedRectangle(cornerRadius: 16).fill(Theme.panel.opacity(0.97)))
                            .overlay(RoundedRectangle(cornerRadius: 16).stroke(Theme.edge))
                            .padding(.top, 44).padding(.horizontal, 10).padding(.bottom, 8)
                        if onLeft { Spacer() }
                    }
                }
            }
        }
        .ignoresSafeArea(edges: .bottom)
        .persistentSystemOverlays(.hidden)
        .sheet(isPresented: $showLayoutSettings) { LayoutSettingsSheet(layout: $draft, pageIndex: $pageIndex) }
        .sheet(isPresented: $showMacros) { MacroListSheet(layout: $draft) }
        .confirmationDialog("Discard your changes?", isPresented: $confirmDiscard) {
            Button("Discard changes", role: .destructive) { dismiss() }
        }
    }

    // MARK: toolbar

    private var toolbar: some View {
        HStack(spacing: 6) {
            Button { if draft == original { dismiss() } else { confirmDiscard = true } } label: { Text("Cancel") }
                .buttonStyle(.bordered)

            Menu {
                ForEach(draft.pages.indices, id: \.self) { i in
                    Button { selected = nil; pageIndex = i } label: {
                        if i == pageIndex { Label(draft.pages[i].name, systemImage: "checkmark") } else { Text(draft.pages[i].name) }
                    }
                }
                Divider()
                Button { addPage() } label: { Label("Add page", systemImage: "plus") }
            } label: {
                Label(draft.pages[pageIndex].name, systemImage: "rectangle.stack").lineLimit(1)
            }
            .buttonStyle(.bordered)

            Menu {
                ForEach(ControlKind.allCases) { k in
                    Button(k.title) { add(k) }
                }
            } label: { Label("Add", systemImage: "plus.circle.fill") }
            .buttonStyle(.borderedProminent).tint(Theme.accent).foregroundColor(.black)

            Button { showMacros = true } label: { Label("Macros", systemImage: "wand.and.stars") }.buttonStyle(.bordered)
            Button { showLayoutSettings = true } label: { Label("Layout", systemImage: "gearshape") }.buttonStyle(.bordered)
            Toggle(isOn: $snap) { Image(systemName: "grid") }.toggleStyle(.button).buttonStyle(.bordered)

            Button { store.update(draft); dismiss() } label: { Text("Save").bold() }
                .buttonStyle(.borderedProminent).tint(Theme.accent).foregroundColor(.black)
        }
        .font(.system(size: 13, weight: .semibold))
        .controlSize(.small)
        .padding(6)
        .background(Capsule().fill(Theme.panel.opacity(0.95)))
        .overlay(Capsule().stroke(Theme.edge))
    }

    // MARK: gestures

    private func snapped(_ v: Double) -> Double { snap ? (v * 50).rounded() / 50 : v }

    private func moveGesture(_ c: Control, _ size: CGSize) -> some Gesture {
        DragGesture(minimumDistance: 3)
            .onChanged { g in
                selected = c.id
                let start = dragStart[c.id] ?? (c.x, c.y)
                if dragStart[c.id] == nil { dragStart[c.id] = start }
                guard let b = binding(c.id) else { return }
                b.wrappedValue.x = min(1, max(0, snapped(start.0 + Double(g.translation.width / size.width))))
                b.wrappedValue.y = min(1, max(0, snapped(start.1 + Double(g.translation.height / size.height))))
            }
            .onEnded { _ in dragStart[c.id] = nil }
    }

    private func resizeHandle(_ c: Control, _ size: CGSize) -> some View {
        Circle().fill(Theme.accent).frame(width: 22, height: 22)
            .overlay(Image(systemName: "arrow.up.left.and.arrow.down.right").font(.system(size: 10, weight: .bold)).foregroundColor(.black))
            .gesture(DragGesture(minimumDistance: 1)
                .onChanged { g in
                    let start = sizeStart[c.id] ?? (c.w, c.h)
                    if sizeStart[c.id] == nil { sizeStart[c.id] = start }
                    guard let b = binding(c.id) else { return }
                    let dw = Double(g.translation.width / size.height) * 2, dh = Double(g.translation.height / size.height) * 2
                    let round = b.wrappedValue.kind == .stick || b.wrappedValue.kind == .dpad || b.wrappedValue.shape == .circle
                    if round {
                        let d = max(dw, dh)
                        b.wrappedValue.w = min(1.2, max(0.06, snapped(start.0 + d)))
                        b.wrappedValue.h = b.wrappedValue.w
                    } else {
                        b.wrappedValue.w = min(2.0, max(0.06, snapped(start.0 + dw)))
                        b.wrappedValue.h = min(1.0, max(0.06, snapped(start.1 + dh)))
                    }
                }
                .onEnded { _ in sizeStart[c.id] = nil })
    }

    private func grid(_ size: CGSize) -> some View {
        Canvas { ctx, s in
            guard snap else { return }
            let step = s.width / 20
            var p = Path()
            var x: CGFloat = 0
            while x <= s.width { p.move(to: CGPoint(x: x, y: 0)); p.addLine(to: CGPoint(x: x, y: s.height)); x += step }
            var y: CGFloat = 0
            let stepY = s.height / 10
            while y <= s.height { p.move(to: CGPoint(x: 0, y: y)); p.addLine(to: CGPoint(x: s.width, y: y)); y += stepY }
            ctx.stroke(p, with: .color(.white.opacity(0.04)), lineWidth: 1)
        }
    }

    // MARK: edits

    private func add(_ k: ControlKind) {
        var c: Control
        switch k {
        case .button: c = Control(kind: .button, x: 0.5, y: 0.5, w: 0.16, action: PadAction(buttons: [.a]))
        case .stick: c = Control(kind: .stick, x: 0.5, y: 0.55, w: 0.42, color: "#161A21")
        case .dpad: c = Control(kind: .dpad, x: 0.5, y: 0.55, w: 0.36)
        case .slider:
            c = Control(kind: .slider, x: 0.5, y: 0.55, w: 0.18, h: 0.6, shape: .rounded, color: "#1F3A2A")
        case .touchpad:
            c = Control(kind: .touchpad, x: 0.5, y: 0.55, w: 0.7, h: 0.5, shape: .rounded, color: "#12151B")
        }
        draft.pages[pageIndex].controls.append(c)
        selected = c.id
    }

    private func delete(_ id: UUID) {
        draft.pages[pageIndex].controls.removeAll { $0.id == id }
        selected = nil
    }

    private func duplicate(_ id: UUID) {
        guard var c = controls.first(where: { $0.id == id }) else { return }
        c.id = UUID()
        c.x = min(0.95, c.x + 0.05); c.y = min(0.95, c.y + 0.05)
        draft.pages[pageIndex].controls.append(c)
        selected = c.id
    }

    private func bringToFront(_ id: UUID) {
        guard let i = draft.pages[pageIndex].controls.firstIndex(where: { $0.id == id }) else { return }
        let c = draft.pages[pageIndex].controls.remove(at: i)
        draft.pages[pageIndex].controls.append(c)
    }

    private func addPage() {
        draft.pages.append(LayoutPage(name: "Page \(draft.pages.count + 1)", controls: []))
        pageIndex = draft.pages.count - 1
        selected = nil
    }
}

// MARK: - Inspector

struct ControlInspector: View {
    @Binding var control: Control
    @Binding var layout: Layout
    let pageCount: Int
    var onDelete: () -> Void
    var onDuplicate: () -> Void
    var onFront: () -> Void
    var onClose: () -> Void

    private var fill: Binding<Color> {
        Binding(get: { Color(hex: control.color) }, set: { control.color = $0.hexString })
    }
    private var ink: Binding<Color> {
        Binding(get: { Color(hex: control.textColor) }, set: { control.textColor = $0.hexString })
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text(control.kind.title).font(.headline)
                Spacer()
                Button(action: onFront) { Image(systemName: "square.3.layers.3d.top.filled") }
                Button(action: onDuplicate) { Image(systemName: "plus.square.on.square") }
                Button(role: .destructive, action: onDelete) { Image(systemName: "trash") }.tint(Theme.bad)
                Button(action: onClose) { Image(systemName: "xmark.circle.fill").foregroundColor(Theme.dim) }
            }
            .buttonStyle(.borderless)
            .padding(12)
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    group("Look") {
                        TextField("Label (auto if empty)", text: $control.label).textFieldStyle(.roundedBorder)
                        HStack {
                            ColorPicker("Colour", selection: fill, supportsOpacity: false)
                            ColorPicker("Text", selection: ink, supportsOpacity: false)
                        }
                        if control.kind == .button || control.kind == .slider || control.kind == .touchpad {
                            Picker("Shape", selection: $control.shape) {
                                Text("Round").tag(ControlShape.circle)
                                Text("Square").tag(ControlShape.rounded)
                                Text("Pill").tag(ControlShape.pill)
                            }.pickerStyle(.segmented)
                        }
                        slider("Size", $control.w, 0.06...1.2) { if control.shape == .circle || control.kind == .stick || control.kind == .dpad { control.h = $0 } }
                        if !(control.shape == .circle || control.kind == .stick || control.kind == .dpad) {
                            slider("Height", $control.h, 0.06...1.0)
                        }
                        slider("Opacity", $control.opacity, 0.1...1)
                    }

                    switch control.kind {
                    case .button: buttonSection
                    case .stick: stickSection
                    case .dpad:
                        group("D-pad") { Toggle("Diagonals (8-way)", isOn: $control.eightWay) }
                    case .slider:
                        group("Slider") {
                            Picker("Controls", selection: $control.sliderTarget) {
                                ForEach(SliderTarget.allCases) { Text($0.title).tag($0) }
                            }
                            Toggle("Spring back when released", isOn: $control.spring)
                        }
                    case .touchpad:
                        group("Touchpad") {
                            Picker("Moves", selection: $control.touchOut) {
                                ForEach(TouchOutput.allCases) { Text($0.title).tag($0) }
                            }.pickerStyle(.segmented)
                            slider("Sensitivity", $control.sensitivity, 0.2...4)
                        }
                    }
                }
                .padding(12)
            }
        }
        .font(.subheadline)
    }

    @ViewBuilder private var buttonSection: some View {
        group("Does") {
            ActionEditor(action: $control.action, layout: layout, pageCount: pageCount, padType: layout.padType)
        }
        group("Behaviour") {
            Picker("Mode", selection: $control.mode) {
                ForEach(ButtonMode.allCases) { Text($0.title).tag($0) }
            }
            if control.mode == .turbo {
                slider("Turbo speed (\(Int(control.turboHz))/s)", $control.turboHz, 3...25)
            }
        }
        group("Drag-out (aim → fire)") {
            Toggle("Slide outward for a second action", isOn: Binding(
                get: { control.pull != nil },
                set: { control.pull = $0 ? (control.pull ?? PadAction(rt: 1)) : nil }))
            if control.pull != nil {
                ActionEditor(action: Binding(get: { control.pull ?? .init() }, set: { control.pull = $0 }),
                             layout: layout, pageCount: 0, padType: layout.padType, compact: true)
            }
        }
    }

    @ViewBuilder private var stickSection: some View {
        group("Joystick") {
            Picker("Stick", selection: $control.stick) {
                Text("Left stick").tag(StickSide.left)
                Text("Right stick").tag(StickSide.right)
            }.pickerStyle(.segmented)
            Toggle("Floating (starts where your thumb lands)", isOn: $control.floating)
            slider("Dead zone", $control.deadzone, 0...0.4)
        }
        group("Push to the edge") {
            Toggle("Extra action at the edge (e.g. sprint)", isOn: Binding(
                get: { control.edge != nil },
                set: { control.edge = $0 ? (control.edge ?? PadAction(buttons: [control.stick == .left ? .ls : .rs])) : nil }))
            if control.edge != nil {
                slider("Edge starts at", $control.edgeAt, 0.6...0.99)
                ActionEditor(action: Binding(get: { control.edge ?? .init() }, set: { control.edge = $0 }),
                             layout: layout, pageCount: 0, padType: layout.padType, compact: true)
            }
        }
    }

    private func group<C: View>(_ title: String, @ViewBuilder _ content: () -> C) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title.uppercased()).font(.caption2.weight(.bold)).tracking(1).foregroundColor(Theme.dim)
            content()
        }
    }

    private func slider(_ title: String, _ v: Binding<Double>, _ r: ClosedRange<Double>, onChange: ((Double) -> Void)? = nil) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title).font(.caption).foregroundColor(Theme.dim)
            Slider(value: Binding(get: { v.wrappedValue }, set: { v.wrappedValue = $0; onChange?($0) }), in: r)
        }
    }
}

/// Pick buttons, triggers, a macro, a page switch or gyro-hold for a control.
struct ActionEditor: View {
    @Binding var action: PadAction
    let layout: Layout
    let pageCount: Int
    let padType: PadType
    var compact = false

    private let cols = [GridItem(.adaptive(minimum: 46), spacing: 6)]

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            LazyVGrid(columns: cols, spacing: 6) {
                ForEach(PadButton.allCases.filter { padType == .ds4 || $0 != .touchpad }) { b in
                    chip(b.label(padType), on: action.buttons.contains(b)) {
                        if let i = action.buttons.firstIndex(of: b) { action.buttons.remove(at: i) } else { action.buttons.append(b) }
                    }
                }
                chip(padType == .ds4 ? "L2" : "LT", on: action.lt > 0) { action.lt = action.lt > 0 ? 0 : 1 }
                chip(padType == .ds4 ? "R2" : "RT", on: action.rt > 0) { action.rt = action.rt > 0 ? 0 : 1 }
            }
            if !compact {
                Picker("Run macro", selection: $action.macro) {
                    Text("No macro").tag(UUID?.none)
                    ForEach(layout.macros) { Text($0.name).tag(UUID?.some($0.id)) }
                }
                if pageCount > 1 {
                    Picker("Go to page", selection: $action.page) {
                        Text("No page switch").tag(Int?.none)
                        ForEach(0..<pageCount, id: \.self) { i in Text(layout.pages.indices.contains(i) ? layout.pages[i].name : "Page \(i + 1)").tag(Int?.some(i)) }
                    }
                }
                Toggle("Turns gyro on while held", isOn: $action.gyroHold)
            }
        }
    }

    private func chip(_ t: String, on: Bool, _ tap: @escaping () -> Void) -> some View {
        Button(action: tap) {
            Text(t).font(.system(size: 12, weight: .bold)).lineLimit(1).minimumScaleFactor(0.6)
                .frame(maxWidth: .infinity, minHeight: 30)
                .background(RoundedRectangle(cornerRadius: 8).fill(on ? Theme.accent.opacity(0.85) : Theme.edge))
                .foregroundColor(on ? .black : Theme.text)
        }
        .buttonStyle(.plain)
    }
}

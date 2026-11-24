import SwiftUI

/// Layout-wide settings: name, controller type, pages, gyro, PC games that auto-select it.
struct LayoutSettingsSheet: View {
    @Binding var layout: Layout
    @Binding var pageIndex: Int
    @EnvironmentObject var conn: Connection
    @Environment(\.dismiss) var dismiss
    @State private var newGame = ""

    private var bg: Binding<Color> { Binding(get: { Color(hex: layout.background) }, set: { layout.background = $0.hexString }) }

    var body: some View {
        NavigationStack {
            Form {
                Section("Layout") {
                    TextField("Name", text: $layout.name)
                    Picker("Shows up on the PC as", selection: $layout.padType) {
                        ForEach(PadType.allCases) { Text($0.title + " controller").tag($0) }
                    }
                    ColorPicker("Background", selection: bg, supportsOpacity: false)
                }

                Section {
                    Picker("Gyro", selection: $layout.gyro.mode) {
                        ForEach(GyroMode.allCases) { Text($0.title).tag($0) }
                    }
                    if layout.gyro.mode != .off {
                        Picker("Gyro controls", selection: $layout.gyro.target) {
                            ForEach(GyroTarget.allCases) { Text($0.title).tag($0) }
                        }
                        VStack(alignment: .leading) {
                            Text("Sensitivity \(layout.gyro.sensitivity, specifier: "%.1f")×").font(.caption).foregroundColor(Theme.dim)
                            Slider(value: $layout.gyro.sensitivity, in: 0.2...4)
                        }
                        Toggle("Invert left/right", isOn: $layout.gyro.invertX)
                        if layout.gyro.target != .steer { Toggle("Invert up/down", isOn: $layout.gyro.invertY) }
                    }
                } header: { Text("Gyro") } footer: {
                    Text("“While a Gyro button…” uses any button set to “Turns gyro on while held” (set it to Toggle to make it an on/off switch).")
                }

                Section {
                    ForEach(layout.games, id: \.self) { g in Text(g) }
                        .onDelete { layout.games.remove(atOffsets: $0) }
                    if !conn.foregroundExe.isEmpty && !layout.games.contains(conn.foregroundExe) {
                        Button {
                            layout.games.append(conn.foregroundExe)
                        } label: {
                            Label("Add what's open on the PC now: \(conn.foregroundExe)", systemImage: "plus.circle")
                        }
                    }
                    HStack {
                        TextField("game.exe", text: $newGame)
                            .textInputAutocapitalization(.never).autocorrectionDisabled()
                        Button("Add") {
                            let g = newGame.trimmingCharacters(in: .whitespaces).lowercased()
                            guard !g.isEmpty else { return }
                            layout.games.append(g.hasSuffix(".exe") ? g : g + ".exe")
                            newGame = ""
                        }
                    }
                } header: { Text("Auto-switch for these PC games") } footer: {
                    Text("Open the game on your PC, then tap “Add what's open on the PC now”.")
                }

                Section("Pages") {
                    ForEach(layout.pages.indices, id: \.self) { i in
                        HStack {
                            TextField("Page name", text: $layout.pages[i].name)
                            Text("\(layout.pages[i].controls.count) controls").font(.caption).foregroundColor(Theme.dim)
                        }
                    }
                    .onDelete { idx in
                        guard layout.pages.count - idx.count >= 1 else { return }
                        layout.pages.remove(atOffsets: idx)
                        pageIndex = min(pageIndex, layout.pages.count - 1)
                    }
                    .onMove { layout.pages.move(fromOffsets: $0, toOffset: $1) }
                }
            }
            .navigationTitle("Layout settings")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() } }
                ToolbarItem(placement: .navigationBarLeading) { EditButton() }
            }
        }
    }
}

/// Macros: named sequences of button presses the PC plays back with exact timing.
struct MacroListSheet: View {
    @Binding var layout: Layout
    @Environment(\.dismiss) var dismiss
    @EnvironmentObject var conn: Connection

    var body: some View {
        NavigationStack {
            List {
                ForEach($layout.macros) { $m in
                    NavigationLink {
                        MacroEditor(macro: $m, padType: layout.padType)
                    } label: {
                        VStack(alignment: .leading) {
                            Text(m.name).font(.body.weight(.semibold))
                            Text("\(m.steps.count) steps · \(m.steps.reduce(0) { $0 + $1.ms }) ms\(m.loop ? " · repeats while held" : "")")
                                .font(.caption).foregroundColor(Theme.dim)
                        }
                    }
                    .swipeActions { Button("Test") { conn.runMacro(m) }.tint(Theme.accent) }
                }
                .onDelete { idx in
                    let removed = idx.map { layout.macros[$0].id }
                    layout.macros.remove(atOffsets: idx)
                    for p in layout.pages.indices {
                        for c in layout.pages[p].controls.indices where removed.contains(where: { $0 == layout.pages[p].controls[c].action.macro }) {
                            layout.pages[p].controls[c].action.macro = nil
                        }
                    }
                }
                Button {
                    layout.macros.append(Macro(name: "Macro \(layout.macros.count + 1)", steps: [MacroStep(buttons: [.a], ms: 60)]))
                } label: { Label("New macro", systemImage: "plus") }
            }
            .navigationTitle("Macros")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() } } }
            .safeAreaInset(edge: .bottom) {
                Text("Then pick a macro in a button's “Run macro”. Swipe a macro to test it on the PC.")
                    .font(.caption).foregroundColor(Theme.dim).padding()
            }
        }
    }
}

struct MacroEditor: View {
    @Binding var macro: Macro
    let padType: PadType
    @EnvironmentObject var conn: Connection

    var body: some View {
        Form {
            Section {
                TextField("Name", text: $macro.name)
                Toggle("Repeat while the button is held", isOn: $macro.loop)
                Button { conn.runMacro(macro) } label: { Label("Test on PC", systemImage: "play.fill") }
                    .disabled(!conn.status.isConnected)
            }
            Section {
                ForEach($macro.steps) { $step in
                    StepRow(step: $step, padType: padType)
                }
                .onDelete { macro.steps.remove(atOffsets: $0) }
                .onMove { macro.steps.move(fromOffsets: $0, toOffset: $1) }
                Button { macro.steps.append(MacroStep(ms: 60)) } label: { Label("Add step", systemImage: "plus") }
            } header: { Text("Steps (played in order)") } footer: {
                Text("A step with nothing selected is a pause. Each step is held for its time.")
            }
        }
        .navigationTitle(macro.name)
        .toolbar { EditButton() }
    }
}

private struct StepRow: View {
    @Binding var step: MacroStep
    let padType: PadType

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            ActionEditor(action: Binding(
                get: { PadAction(buttons: step.buttons, lt: step.lt, rt: step.rt) },
                set: { step.buttons = $0.buttons; step.lt = $0.lt; step.rt = $0.rt }),
                layout: Layout(name: "", pages: []), pageCount: 0, padType: padType, compact: true)
            HStack {
                Picker("Left stick", selection: Binding(
                    get: { Dir(lx: step.lx, ly: step.ly) },
                    set: { (step.lx, step.ly) = $0.vector })) {
                    ForEach(Dir.allCases) { Text($0.title).tag($0) }
                }
                Stepper("\(step.ms) ms", value: $step.ms, in: 10...3000, step: 10)
            }
        }
        .padding(.vertical, 4)
    }

    enum Dir: String, CaseIterable, Identifiable {
        case none, up, down, left, right
        var id: String { rawValue }
        var title: String { self == .none ? "Stick: none" : "Stick: " + rawValue }
        var vector: (Double, Double) {
            switch self {
            case .none: return (0, 0)
            case .up: return (0, 1)
            case .down: return (0, -1)
            case .left: return (-1, 0)
            case .right: return (1, 0)
            }
        }
        init(lx: Double, ly: Double) {
            if ly > 0.5 { self = .up } else if ly < -0.5 { self = .down }
            else if lx < -0.5 { self = .left } else if lx > 0.5 { self = .right } else { self = .none }
        }
    }
}

import SwiftUI

struct RootView: View {
    @EnvironmentObject var model: AppModel
    @EnvironmentObject var settings: AppSettings
    @EnvironmentObject var conn: Connection
    @State private var showMenu = false
    @State private var pairPrompt = false

    var body: some View {
        Group {
            if settings.isConfigured {
                PlayView(showMenu: $showMenu)
            } else {
                ConnectView(firstRun: true)
            }
        }
        .fullScreenCover(isPresented: $showMenu) { MenuView() }
        .onAppear { if settings.isConfigured && conn.status == .idle { model.connect() } }
        .onChange(of: conn.status) { s in
            if case .needsCode = s, settings.isConfigured { pairPrompt = true }
        }
        .sheet(isPresented: $pairPrompt) { PairSheet() }
    }
}

/// Shown when the PC needs the pairing code (first time, or after "Forget" on the PC).
struct PairSheet: View {
    @EnvironmentObject var model: AppModel
    @EnvironmentObject var conn: Connection
    @EnvironmentObject var settings: AppSettings
    @Environment(\.dismiss) var dismiss
    @State private var code = ""

    var body: some View {
        VStack(spacing: 14) {
            Text("Pair with \(settings.pcName.isEmpty ? "your PC" : settings.pcName)").font(.title3.bold())
            Text(conn.status.text).font(.footnote).foregroundColor(Theme.dim).multilineTextAlignment(.center)
            TextField("6-digit code from the PC", text: $code)
                .keyboardType(.numberPad)
                .font(.system(size: 28, weight: .bold, design: .monospaced))
                .multilineTextAlignment(.center)
                .padding(10)
                .background(RoundedRectangle(cornerRadius: 12).fill(Theme.panel))
                .frame(maxWidth: 320)
            HStack {
                Button("Cancel") { dismiss() }.buttonStyle(.bordered)
                Button("Pair") { model.connect(code: code); dismiss() }
                    .buttonStyle(.borderedProminent).tint(Theme.accent).foregroundColor(.black)
                    .disabled(code.count < 6)
            }
        }
        .padding(24)
        .presentationDetents([.medium])
    }
}

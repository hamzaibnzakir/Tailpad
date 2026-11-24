import SwiftUI

/// Pick the PC: Tailscale IP (or MagicDNS name) + pairing code. Scanning the QR in the PC app fills this in.
struct ConnectView: View {
    var firstRun = false
    @EnvironmentObject var model: AppModel
    @EnvironmentObject var settings: AppSettings
    @EnvironmentObject var conn: Connection
    @State private var host = ""
    @State private var port = "47800"
    @State private var code = ""

    var body: some View {
        ScrollView {
            HStack(alignment: .top, spacing: 28) {
                VStack(alignment: .leading, spacing: 10) {
                    if firstRun {
                        Text("TailPad").font(.system(size: 34, weight: .heavy, design: .rounded))
                        Text("Your iPhone becomes a controller for your PC, anywhere your Tailscale reaches.")
                            .foregroundColor(Theme.dim)
                    }
                    step(1, "Run **TailPad** on your PC (it sits in the tray).")
                    step(2, "Turn **Tailscale** on here and on the PC.")
                    step(3, "Easiest: open the iPhone **Camera** and point it at the QR code in the PC app.")
                    step(4, "Or type the address and code shown in the PC app →")
                }
                .frame(maxWidth: 340, alignment: .leading)

                VStack(alignment: .leading, spacing: 12) {
                    field("PC address", text: $host, prompt: "100.x.y.z or pc-name", keyboard: .URL)
                    field("Port", text: $port, prompt: "47800", keyboard: .numberPad)
                    field("Pairing code (first time only)", text: $code, prompt: "123456", keyboard: .numberPad)
                    Button {
                        settings.host = host.trimmingCharacters(in: .whitespacesAndNewlines)
                        settings.port = Int(port) ?? 47800
                        model.connect(code: code)
                    } label: {
                        Text(conn.status == .connecting ? "Connecting…" : "Connect")
                            .font(.headline).frame(maxWidth: .infinity).padding(.vertical, 10)
                    }
                    .buttonStyle(.borderedProminent).tint(Theme.accent).foregroundColor(.black)
                    .disabled(host.trimmingCharacters(in: .whitespaces).isEmpty)

                    statusLine
                }
                .frame(maxWidth: 360)
            }
            .padding(28)
            .frame(maxWidth: .infinity)
        }
        .background(Theme.bg.ignoresSafeArea())
        .onAppear {
            host = settings.host
            port = String(settings.port)
        }
    }

    @ViewBuilder private var statusLine: some View {
        switch conn.status {
        case .idle: EmptyView()
        case .connected:
            Label("Connected to \(conn.pcName)", systemImage: "checkmark.circle.fill").foregroundColor(Theme.accent)
        case .connecting:
            ProgressView().tint(Theme.accent)
        case .needsCode(let r), .failed(let r):
            Label(r, systemImage: "exclamationmark.triangle.fill").foregroundColor(Theme.warn).font(.footnote)
        }
    }

    private func step(_ n: Int, _ text: LocalizedStringKey) -> some View {
        HStack(alignment: .top, spacing: 10) {
            Text("\(n)").font(.caption.bold()).frame(width: 22, height: 22)
                .background(Circle().fill(Theme.accent.opacity(0.2))).foregroundColor(Theme.accent)
            Text(text).font(.subheadline).foregroundColor(Theme.text)
        }
    }

    private func field(_ title: String, text: Binding<String>, prompt: String, keyboard: UIKeyboardType) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title).font(.caption).foregroundColor(Theme.dim)
            TextField(prompt, text: text)
                .keyboardType(keyboard)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .padding(10)
                .background(RoundedRectangle(cornerRadius: 10).fill(Theme.panel))
                .overlay(RoundedRectangle(cornerRadius: 10).stroke(Theme.edge))
        }
    }
}

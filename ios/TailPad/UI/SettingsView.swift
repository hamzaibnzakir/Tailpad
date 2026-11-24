import SwiftUI

struct SettingsView: View {
    @EnvironmentObject var settings: AppSettings
    @EnvironmentObject var model: AppModel

    var body: some View {
        Form {
            Section("Feel") {
                Toggle("Click on button press", isOn: $settings.haptics)
                Toggle("Rumble when the game vibrates", isOn: $settings.rumble)
                Toggle("Show status at the top", isOn: $settings.showStatus)
            }
            Section {
                Picker("Quality", selection: $settings.streamQuality) {
                    Text("540p · 60 fps · 6 Mbps").tag("540p60")
                    Text("720p · 30 fps · 6 Mbps").tag("720p30")
                    Text("720p · 60 fps · 10 Mbps").tag("720p60")
                    Text("1080p · 60 fps · 20 Mbps").tag("1080p60")
                }
                Toggle("Game sound on the phone", isOn: $settings.streamAudio)
                VStack(alignment: .leading) {
                    Text("Touch controls over the game: \(Int(settings.overlayOpacity * 100))%").font(.caption).foregroundColor(Theme.dim)
                    Slider(value: $settings.overlayOpacity, in: 0.15...1)
                }
                Toggle("Hide touch controls when a controller is connected", isOn: $settings.hideTouchWithController)
                ControllerRow()
            } header: { Text("Remote play") } footer: {
                Text("Tap ▶︎ TV at the top of the controller to stream the game that's open on your PC. If it stutters away from home, pick a lower quality.")
            }
            Section {
                Toggle("Switch layout to match the PC game", isOn: $settings.autoSwitch)
            } footer: {
                Text("When the focused program on your PC is in a layout's game list, TailPad switches to it, and back when you leave.")
            }
            Section {
                Toggle("Send phone motion to the PC", isOn: $settings.sendMotion)
                Toggle("Flip motion X", isOn: $settings.flipX)
                Toggle("Flip motion Y", isOn: $settings.flipY)
                Toggle("Flip motion Z", isOn: $settings.flipZ)
                Button("Re-centre tilt steering") { model.motion.recenter() }
            } header: {
                Text("Motion for PlayStation mode & emulators")
            } footer: {
                Text("Gives PlayStation-mode games and emulators (Cemu, Dolphin, Ryujinx, Citra… via DSU at 127.0.0.1:26760 on the PC) real gyro. If motion moves the wrong way in one emulator, flip that axis.")
            }
            Section("About") {
                LabeledContent("Version", value: Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0")
                LabeledContent("Phone ID", value: String(settings.deviceId.prefix(8)))
            }
        }
        .navigationTitle("Settings")
    }
}

private struct ControllerRow: View {
    @EnvironmentObject var controllers: Controllers
    var body: some View {
        LabeledContent("Controller on this iPhone", value: controllers.name ?? "None — pair one in iPhone Settings → Bluetooth")
            .font(.subheadline)
    }
}

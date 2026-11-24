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

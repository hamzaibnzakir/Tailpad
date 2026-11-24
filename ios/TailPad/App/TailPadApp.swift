import SwiftUI

@main
struct TailPadApp: App {
    @StateObject private var model = AppModel()
    @Environment(\.scenePhase) private var phase

    var body: some Scene {
        WindowGroup {
            RootView()
                .environmentObject(model)
                .environmentObject(model.settings)
                .environmentObject(model.store)
                .environmentObject(model.connection)
                .environmentObject(model.engine)
                .preferredColorScheme(.dark)
                .onOpenURL { model.open($0) }
        }
        .onChange(of: phase) { p in
            switch p {
            case .active: model.connection.resume()
            case .background: model.engine.releaseAll(); model.connection.suspend()
            default: break
            }
        }
    }
}

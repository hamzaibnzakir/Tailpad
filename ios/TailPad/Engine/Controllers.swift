import GameController

/// A real controller (Xbox, PlayStation, MFi…) paired with the iPhone. Its input is mixed in with touch,
/// so you can play the streamed game with it, or use it as an extra pad for the PC.
final class Controllers: ObservableObject {
    @Published private(set) var name: String?
    private let mixer: InputMixer
    private let id = UUID()
    private var current: GCController?

    init(mixer: InputMixer) {
        self.mixer = mixer
        GCController.shouldMonitorBackgroundEvents = false
        NotificationCenter.default.addObserver(forName: .GCControllerDidConnect, object: nil, queue: .main) { [weak self] n in
            if let c = n.object as? GCController { self?.attach(c) }
        }
        NotificationCenter.default.addObserver(forName: .GCControllerDidDisconnect, object: nil, queue: .main) { [weak self] n in
            guard let self, let c = n.object as? GCController, c === self.current else { return }
            self.current = nil
            self.name = nil
            self.mixer.clear(self.id)
            if let next = GCController.controllers().first(where: { $0.extendedGamepad != nil }) { self.attach(next) }
        }
        GCController.startWirelessControllerDiscovery {}
        if let c = GCController.controllers().first(where: { $0.extendedGamepad != nil }) { attach(c) }
    }

    private func attach(_ c: GCController) {
        guard let pad = c.extendedGamepad else { return }
        current = c
        name = c.vendorName ?? "Controller"
        c.playerIndex = .index1
        pad.valueChangedHandler = { [weak self] gp, _ in self?.update(gp) }
        update(pad)
    }

    private func update(_ g: GCExtendedGamepad) {
        var c = Contribution()
        func b(_ e: GCControllerButtonInput?, _ p: PadButton) { if e?.isPressed == true { c.buttons |= p.bit } }
        b(g.buttonA, .a); b(g.buttonB, .b); b(g.buttonX, .x); b(g.buttonY, .y)
        b(g.leftShoulder, .lb); b(g.rightShoulder, .rb)
        b(g.buttonMenu, .start); b(g.buttonOptions, .back); b(g.buttonHome, .guide)
        b(g.leftThumbstickButton, .ls); b(g.rightThumbstickButton, .rs)
        b(g.dpad.up, .up); b(g.dpad.down, .down); b(g.dpad.left, .left); b(g.dpad.right, .right)
        if let ds = g as? GCDualShockGamepad { b(ds.touchpadButton, .touchpad) }
        if #available(iOS 14.0, *), let dse = g as? GCDualSenseGamepad { b(dse.touchpadButton, .touchpad) }
        c.lx = Double(g.leftThumbstick.xAxis.value); c.ly = Double(g.leftThumbstick.yAxis.value)
        c.rx = Double(g.rightThumbstick.xAxis.value); c.ry = Double(g.rightThumbstick.yAxis.value)
        c.lt = Double(g.leftTrigger.value); c.rt = Double(g.rightTrigger.value)
        mixer.set(id, c)
    }
}

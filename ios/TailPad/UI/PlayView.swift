import SwiftUI

/// The controller itself, full screen.
struct PlayView: View {
    @EnvironmentObject var model: AppModel
    @EnvironmentObject var engine: PadEngine
    @EnvironmentObject var conn: Connection
    @EnvironmentObject var settings: AppSettings
    @EnvironmentObject var controllers: Controllers
    @Binding var showMenu: Bool

    private var streaming: Bool { conn.streamState != .off }
    /// with a real controller connected, the touch controls get out of the way of the game
    private var touchHidden: Bool { streaming && controllers.name != nil && settings.hideTouchWithController && model.demo == nil }

    var body: some View {
        GeometryReader { geo in
            let size = geo.size
            let layout = engine.layout
            let page = engine.page
            let frames = touchHidden ? [:] : Dictionary(uniqueKeysWithValues: page.controls.map { ($0.id, controlFrame($0, in: size)) })
            ZStack(alignment: .topLeading) {
                if streaming {
                    StreamVideoView(player: conn.streamPlayer)
                        .frame(width: size.width, height: size.height)
                        .allowsHitTesting(false)
                    if conn.streamState == .starting {
                        VStack(spacing: 10) {
                            ProgressView().tint(Theme.accent)
                            Text("Starting remote play…").font(.footnote).foregroundColor(Theme.dim)
                        }
                        .frame(width: size.width, height: size.height)
                    }
                }
                if let light = conn.lightColor, layout.padType == .ds4 {
                    // DS4 lightbar colour from the game, as a soft glow along the top
                    LinearGradient(colors: [Color(light).opacity(0.35), .clear], startPoint: .top, endPoint: .center)
                        .ignoresSafeArea().allowsHitTesting(false)
                }

                ForEach(touchHidden ? [] : page.controls) { c in
                    let f = frames[c.id] ?? .zero
                    ControlView(control: c, padType: layout.padType, visual: engine.visual)
                        .frame(width: f.width, height: f.height)
                        .position(x: f.midX, y: f.midY)
                        .opacity(streaming ? settings.overlayOpacity : 1)
                        .allowsHitTesting(false)
                }

                // floating sticks draw where your thumb landed
                ForEach(page.controls.filter { $0.kind == .stick && $0.floating }) { c in
                    if let o = engine.visual.origins[c.id], let f = frames[c.id] {
                        let s = min(f.width, f.height) * 0.8
                        let k = engine.visual.knobs[c.id] ?? .zero
                        ZStack {
                            Circle().stroke(Color.white.opacity(0.25), lineWidth: 2)
                            Circle().fill(Color(hex: "#2B3140")).frame(width: s * 0.44, height: s * 0.44)
                                .offset(x: k.x * s * 0.42, y: k.y * s * 0.42)
                        }
                        .frame(width: s, height: s)
                        .position(o)
                        .allowsHitTesting(false)
                    }
                }

                // touchpad finger dots
                ForEach(Array(engine.visual.touchpadDot.values.enumerated()), id: \.offset) { _, p in
                    Circle().fill(Theme.accent.opacity(0.25)).frame(width: 46, height: 46).position(p).allowsHitTesting(false)
                }

                HUD(showMenu: $showMenu)
                    .frame(width: size.width)

                TouchSurface(engine: engine, frames: frames) { o in model.motion.setOrientation(o) }
            }
        }
        .background((streaming ? Color.black : Color(hex: engine.layout.background)).ignoresSafeArea())
        .persistentSystemOverlays(.hidden)
        .defersSystemGestures(on: .all)
    }
}

/// Top strip: connection dot + ping, layout name, page dots, menu button.
struct HUD: View {
    @EnvironmentObject var model: AppModel
    @EnvironmentObject var controllers: Controllers
    @EnvironmentObject var engine: PadEngine
    @EnvironmentObject var conn: Connection
    @EnvironmentObject var settings: AppSettings
    @Binding var showMenu: Bool

    var body: some View {
        VStack(spacing: 6) {
            HStack(spacing: 8) {
                Button { showMenu = true } label: {
                    Image(systemName: "line.3.horizontal")
                        .font(.system(size: 13, weight: .bold))
                        .frame(width: 30, height: 26)
                }
                Button { model.toggleStream() } label: {
                    Image(systemName: conn.streamState == .off ? "play.tv" : "stop.fill")
                        .font(.system(size: 13, weight: .bold))
                        .foregroundColor(conn.streamState == .off ? Theme.text : Theme.bad)
                        .frame(width: 30, height: 26)
                }
                .disabled(!conn.status.isConnected)
                if conn.streamState != .off {
                    // background play: move the PC's game onto the hidden screen, or bring it back
                    Button { conn.isInBackground ? conn.bringBack() : conn.moveToBackground() } label: {
                        Image(systemName: conn.isInBackground ? "rectangle.on.rectangle.slash" : "rectangle.on.rectangle")
                            .font(.system(size: 13, weight: .bold))
                            .foregroundColor(conn.isInBackground ? Theme.accent : Theme.text)
                            .frame(width: 30, height: 26)
                    }
                }
                if settings.showStatus {
                    Circle().fill(dotColor).frame(width: 8, height: 8)
                    Text(statusText).font(.system(size: 11, weight: .medium, design: .rounded)).foregroundColor(Theme.dim)
                        .lineLimit(1)
                }
            }
            .padding(.horizontal, 10).padding(.vertical, 3)
            .background(Capsule().fill(Theme.panel.opacity(0.92)))
            .overlay(Capsule().stroke(Theme.edge, lineWidth: 1))
            .foregroundColor(Theme.text)

            if engine.layout.pages.count > 1 {
                HStack(spacing: 6) {
                    ForEach(engine.layout.pages.indices, id: \.self) { i in
                        Button { engine.setPage(i) } label: {
                            Text(engine.layout.pages[i].name)
                                .font(.system(size: 10, weight: .semibold))
                                .padding(.horizontal, 8).padding(.vertical, 3)
                                .background(Capsule().fill(i == engine.pageIndex ? Theme.accent.opacity(0.25) : Theme.panel))
                                .foregroundColor(i == engine.pageIndex ? Theme.accent : Theme.dim)
                        }
                    }
                }
            }

            if let t = model.toast {
                Text(t).font(.system(size: 12, weight: .semibold))
                    .padding(.horizontal, 12).padding(.vertical, 6)
                    .background(Capsule().fill(Theme.panel))
                    .overlay(Capsule().stroke(Theme.accent.opacity(0.5), lineWidth: 1))
                    .transition(.opacity)
            }
        }
        .padding(.top, 6)
        .animation(.easeInOut(duration: 0.2), value: model.toast)
    }

    private var dotColor: Color {
        switch conn.status {
        case .connected: return Theme.accent
        case .connecting: return Theme.warn
        default: return Theme.bad
        }
    }

    private var statusText: String {
        let name = engine.layout.name
        if conn.streamState == .playing {
            let i = conn.streamInfo
            let ping = conn.pingMs.map { "\($0) ms · " } ?? ""
            let pad = controllers.name.map { " · 🎮 \($0)" } ?? ""
            if case .on(let game) = conn.background {
                return "\(ping)\(i.height)p · \(i.fps) fps · \(game) · PC free\(pad)"
            }
            return "\(ping)\(i.height)p · \(i.fps) fps · \(String(format: "%.1f", Double(i.kbps) / 1000)) Mbps · \(i.encoder)\(pad)"
        }
        switch conn.status {
        case .connected:
            let p = conn.pingMs.map { "\($0) ms" } ?? "…"
            let player = conn.player > 0 ? " · P\(conn.player)" : ""
            return "\(p)\(player) · \(name)"
        case .connecting: return "Connecting… · \(name)"
        default: return "Offline · \(name)"
        }
    }
}

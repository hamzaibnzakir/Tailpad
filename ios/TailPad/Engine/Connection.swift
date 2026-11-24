import Foundation
import Network
import UIKit

struct PCGame: Identifiable, Hashable {
    let id: String
    let name: String
    let source: String
    let art: URL?
}

/// Talks to the TailPad PC app: TCP for control messages, UDP for the ~120 Hz input stream.
final class Connection: ObservableObject {
    enum Status: Equatable {
        case idle, connecting, connected
        case needsCode(String)
        case failed(String)

        var isConnected: Bool { self == .connected }
        var text: String {
            switch self {
            case .idle: return "Not connected"
            case .connecting: return "Connecting…"
            case .connected: return "Connected"
            case .needsCode(let r): return r
            case .failed(let r): return r
            }
        }
    }

    @Published private(set) var status: Status = .idle
    @Published private(set) var pingMs: Int?
    @Published private(set) var pcName = ""
    @Published private(set) var player = 0
    @Published private(set) var lightColor: UIColor?
    @Published private(set) var games: [PCGame] = []
    @Published private(set) var foregroundExe = ""
    @Published private(set) var foregroundTitle = ""
    @Published var toast: String?

    var onForeground: ((String) -> Void)?
    var onTokenSaved: ((String, String) -> Void)?   // host, token
    var onPCName: ((String) -> Void)?

    let mixer: InputMixer
    let haptics: Haptics
    private let queue = DispatchQueue(label: "tailpad.net", qos: .userInteractive)
    private var tcp: NWConnection?
    private var udp: NWConnection?
    private var timer: DispatchSourceTimer?
    private var pingTimer: DispatchSourceTimer?
    private var buffer = Data()
    private var key: UInt32 = 0
    private var seq: UInt32 = 0
    private var wantConnected = false
    private var retryWork: DispatchWorkItem?
    private var generation = 0

    // connection parameters (network-queue owned)
    private var host = ""
    private var port = 47800
    private var deviceId = ""
    private var token = ""
    private var code = ""
    private var padType: PadType = .x360
    private var deviceName = UIDevice.current.name

    init(mixer: InputMixer, haptics: Haptics) {
        self.mixer = mixer
        self.haptics = haptics
    }

    // MARK: public API (main thread)

    func connect(host: String, port: Int, deviceId: String, token: String, code: String = "", padType: PadType) {
        let h = host.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !h.isEmpty else { status = .failed("Enter your PC's Tailscale IP"); return }
        status = .connecting
        queue.async {
            self.host = h; self.port = port; self.deviceId = deviceId
            self.token = token; self.code = code; self.padType = padType
            self.wantConnected = true
            self.openTCP()
        }
    }

    func disconnect() {
        queue.async {
            self.wantConnected = false
            self.sendJSON(["t": "bye"])
            self.teardown()
            DispatchQueue.main.async { self.status = .idle; self.pingMs = nil }
        }
    }

    /// App came back to the foreground — reconnect if we were connected before.
    func resume() {
        queue.async { if self.wantConnected && self.tcp == nil { self.openTCP() } }
    }

    /// App went to the background — release everything and close.
    func suspend() {
        mixer.clearAll()
        queue.async {
            self.sendFrame()
            self.teardown()
        }
        DispatchQueue.main.async { self.haptics.stopRumble() }
    }

    func setPadType(_ t: PadType) {
        queue.async {
            guard t != self.padType else { return }
            self.padType = t
            self.sendJSON(["t": "pad", "pad": t.rawValue])
        }
    }

    func runMacro(_ m: Macro) {
        queue.async {
            self.sendJSON(["t": "macro", "id": m.id.uuidString, "loop": m.loop, "steps": m.steps.map(\.json)])
        }
    }

    func stopMacro(_ id: UUID) { queue.async { self.sendJSON(["t": "macroStop", "id": id.uuidString]) } }
    func requestGames() { queue.async { self.sendJSON(["t": "games"]) } }
    func launch(_ g: PCGame) { queue.async { self.sendJSON(["t": "launch", "id": g.id]) } }

    /// Screenshot/demo mode: pretend we're connected with sample data.
    func demo() {
        status = .connected
        pingMs = 9
        pcName = "DELL-5540"
        player = 1
        foregroundExe = "eldenring.exe"
        let ids: [(String, String)] = [("1245620", "ELDEN RING"), ("1551360", "Forza Horizon 5"), ("1091500", "Cyberpunk 2077"),
                                       ("1364780", "Street Fighter 6"), ("1145360", "Hades"), ("252950", "Rocket League")]
        games = ids.map { PCGame(id: "steam:\($0.0)", name: $0.1, source: "Steam",
                                 art: URL(string: "https://cdn.cloudflare.steamstatic.com/steam/apps/\($0.0)/header.jpg")) }
            + [PCGame(id: "scan:1", name: "Grand Theft Auto V", source: "PC", art: Self.demoIcon()),
               PCGame(id: "scan:2", name: "eFootball", source: "PC", art: Self.demoIcon())]
    }

    /// sample icon for screenshots (a real one comes from the game's .exe on the PC)
    private static func demoIcon() -> URL? {
        let r = UIGraphicsImageRenderer(size: CGSize(width: 96, height: 96))
        let img = r.image { ctx in
            UIColor(red: 0.13, green: 0.16, blue: 0.21, alpha: 1).setFill()
            UIBezierPath(roundedRect: CGRect(x: 4, y: 4, width: 88, height: 88), cornerRadius: 20).fill()
            let a: [NSAttributedString.Key: Any] = [.font: UIFont.systemFont(ofSize: 40, weight: .heavy), .foregroundColor: UIColor(red: 0.22, green: 1, blue: 0.53, alpha: 1)]
            ("▶" as NSString).draw(at: CGPoint(x: 30, y: 22), withAttributes: a)
        }
        guard let png = img.pngData() else { return nil }
        return URL(string: "data:image/png;base64," + png.base64EncodedString())
    }

    // MARK: TCP

    private func openTCP() {
        teardown()
        generation += 1
        let gen = generation
        let tcpOpts = NWProtocolTCP.Options()
        tcpOpts.noDelay = true
        tcpOpts.connectionTimeout = 5
        tcpOpts.enableKeepalive = true
        tcpOpts.keepaliveIdle = 5
        let params = NWParameters(tls: nil, tcp: tcpOpts)
        guard let p = NWEndpoint.Port(rawValue: UInt16(clamping: port)) else { return }
        let c = NWConnection(host: NWEndpoint.Host(host), port: p, using: params)
        tcp = c
        DispatchQueue.main.async { if self.status != .connected { self.status = .connecting } }
        c.stateUpdateHandler = { [weak self] st in
            guard let self, gen == self.generation else { return }
            switch st {
            case .ready:
                self.buffer.removeAll()
                self.sendJSON([
                    "t": "hello", "v": 1, "device": self.deviceId, "name": self.deviceName,
                    "token": self.token, "code": self.code, "pad": self.padType.rawValue,
                ])
                self.receive(on: c, gen: gen)
            case .waiting(let e):
                self.fail("Can't reach PC (\(Self.describe(e))). Is Tailscale on, and TailPad running on the PC?")
            case .failed(let e):
                self.fail("Connection lost: \(Self.describe(e))")
            case .cancelled:
                break
            default: break
            }
        }
        c.start(queue: queue)
    }

    private static func describe(_ e: NWError) -> String {
        switch e {
        case .posix(let code) where code == .ECONNREFUSED: return "refused"
        case .posix(let code) where code == .ETIMEDOUT: return "timed out"
        case .posix(let code) where code == .ENETUNREACH || code == .EHOSTUNREACH: return "unreachable"
        default: return e.localizedDescription
        }
    }

    private func fail(_ msg: String) {
        let wasConnected = timer != nil
        teardown()
        DispatchQueue.main.async {
            self.status = .failed(msg)
            self.pingMs = nil
            self.haptics.stopRumble()
            if wasConnected { self.toast = "Disconnected — reconnecting…" }
        }
        scheduleRetry()
    }

    private func scheduleRetry() {
        guard wantConnected else { return }
        retryWork?.cancel()
        let w = DispatchWorkItem { [weak self] in
            guard let self, self.wantConnected, self.tcp == nil else { return }
            self.openTCP()
        }
        retryWork = w
        queue.asyncAfter(deadline: .now() + 1.5, execute: w)
    }

    private func receive(on c: NWConnection, gen: Int) {
        c.receive(minimumIncompleteLength: 1, maximumLength: 64 * 1024) { [weak self] data, _, done, err in
            guard let self, gen == self.generation else { return }
            if let data, !data.isEmpty {
                self.buffer.append(data)
                while let nl = self.buffer.firstIndex(of: 0x0A) {
                    let line = self.buffer[self.buffer.startIndex..<nl]
                    self.buffer.removeSubrange(self.buffer.startIndex...nl)
                    if let obj = try? JSONSerialization.jsonObject(with: Data(line)) as? [String: Any] { self.handle(obj) }
                }
                if self.buffer.count > 16 * 1024 * 1024 { self.buffer.removeAll() } // game lists with icons can be a few MB
            }
            if done || err != nil {
                if self.wantConnected { self.fail("PC closed the connection") }
                return
            }
            self.receive(on: c, gen: gen)
        }
    }

    private func handle(_ m: [String: Any]) {
        switch m["t"] as? String {
        case "welcome":
            key = (m["key"] as? NSNumber)?.uint32Value ?? 0
            let newToken = m["token"] as? String ?? ""
            let udpPort = (m["udp"] as? NSNumber)?.intValue ?? 47801
            let pc = m["pc"] as? String ?? ""
            let p = (m["player"] as? NSNumber)?.intValue ?? 0
            token = newToken
            code = ""
            let h = host
            openUDP(port: udpPort)
            startPing()
            DispatchQueue.main.async {
                self.status = .connected
                self.pcName = pc
                self.player = p
                self.onTokenSaved?(h, newToken)
                self.onPCName?(pc)
                UIApplication.shared.isIdleTimerDisabled = true
            }
        case "denied":
            let reason = m["reason"] as? String ?? "PC refused the connection"
            wantConnected = false
            teardown()
            DispatchQueue.main.async { self.status = .needsCode(reason) }
        case "pong":
            if let ts = (m["ts"] as? NSNumber)?.doubleValue {
                let ms = Int((ProcessInfo.processInfo.systemUptime * 1000 - ts).rounded())
                DispatchQueue.main.async { self.pingMs = max(0, ms) }
            }
        case "rumble":
            let l = (m["l"] as? NSNumber)?.intValue ?? 0, s = (m["s"] as? NSNumber)?.intValue ?? 0
            DispatchQueue.main.async { self.haptics.rumble(large: l, small: s) }
        case "light":
            let r = (m["r"] as? NSNumber)?.doubleValue ?? 0, g = (m["g"] as? NSNumber)?.doubleValue ?? 0, b = (m["b"] as? NSNumber)?.doubleValue ?? 0
            DispatchQueue.main.async { self.lightColor = UIColor(red: r / 255, green: g / 255, blue: b / 255, alpha: 1) }
        case "player":
            let n = (m["n"] as? NSNumber)?.intValue ?? 0
            DispatchQueue.main.async { self.player = n }
        case "games":
            let list = (m["list"] as? [[String: Any]] ?? []).compactMap { g -> PCGame? in
                guard let id = g["id"] as? String, let name = g["name"] as? String else { return nil }
                return PCGame(id: id, name: name, source: g["source"] as? String ?? "", art: (g["art"] as? String).flatMap(URL.init(string:)))
            }
            DispatchQueue.main.async { self.games = list }
        case "fg":
            let exe = m["exe"] as? String ?? "", title = m["title"] as? String ?? ""
            DispatchQueue.main.async {
                self.foregroundExe = exe
                self.foregroundTitle = title
                self.onForeground?(exe)
            }
        case "info":
            let msg = m["msg"] as? String ?? ""
            DispatchQueue.main.async { self.toast = msg }
        default: break
        }
    }

    private func sendJSON(_ obj: [String: Any]) {
        guard let tcp, var data = try? JSONSerialization.data(withJSONObject: obj) else { return }
        data.append(0x0A)
        tcp.send(content: data, completion: .contentProcessed { _ in })
    }

    // MARK: UDP input stream

    private func openUDP(port: Int) {
        udp?.cancel()
        guard let p = NWEndpoint.Port(rawValue: UInt16(clamping: port)) else { return }
        let params = NWParameters.udp
        params.serviceClass = .interactiveVideo // lowest-latency QoS marking available
        let u = NWConnection(host: NWEndpoint.Host(host), port: p, using: params)
        udp = u
        u.start(queue: queue)

        timer?.cancel()
        let t = DispatchSource.makeTimerSource(flags: .strict, queue: queue)
        t.schedule(deadline: .now(), repeating: .microseconds(8333), leeway: .microseconds(500)) // 120 Hz
        t.setEventHandler { [weak self] in self?.sendFrame() }
        t.resume()
        timer = t
    }

    private func sendFrame() {
        guard let udp, key != 0 else { return }
        let f = mixer.snapshot()
        seq &+= 1
        udp.send(content: Self.packet(key: key, seq: seq, f), completion: .idempotent)
    }

    static func packet(key: UInt32, seq: UInt32, _ f: InputFrame) -> Data {
        var d = Data(capacity: 68)
        func u32(_ v: UInt32) { withUnsafeBytes(of: v.littleEndian) { d.append(contentsOf: $0) } }
        func i16(_ v: Double) { let s = Int16(max(-32767, min(32767, (v * 32767).rounded()))); withUnsafeBytes(of: s.littleEndian) { d.append(contentsOf: $0) } }
        func u8(_ v: Double) { d.append(UInt8(max(0, min(255, (v * 255).rounded())))) }
        func f32(_ v: Double) { withUnsafeBytes(of: Float(v).bitPattern.littleEndian) { d.append(contentsOf: $0) } }
        u32(0x3144_5054)
        u32(key)
        u32(seq)
        u32(f.buttons)
        i16(f.lx); i16(f.ly); i16(f.rx); i16(f.ry)
        u8(f.lt); u8(f.rt)
        d.append(f.motion != nil ? 1 : 0)
        d.append(0)
        f32(f.mouseDx); f32(f.mouseDy)
        let m = f.motion ?? MotionFrame()
        f32(m.accel.0); f32(m.accel.1); f32(m.accel.2)
        f32(m.gyro.0); f32(m.gyro.1); f32(m.gyro.2)
        withUnsafeBytes(of: m.timestampUs.littleEndian) { d.append(contentsOf: $0) }
        return d
    }

    private func startPing() {
        pingTimer?.cancel()
        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now(), repeating: 1)
        t.setEventHandler { [weak self] in self?.sendJSON(["t": "ping", "ts": ProcessInfo.processInfo.systemUptime * 1000]) }
        t.resume()
        pingTimer = t
    }

    private func teardown() {
        retryWork?.cancel()
        timer?.cancel(); timer = nil
        pingTimer?.cancel(); pingTimer = nil
        udp?.cancel(); udp = nil
        tcp?.stateUpdateHandler = nil
        tcp?.cancel(); tcp = nil
        key = 0
        DispatchQueue.main.async { UIApplication.shared.isIdleTimerDisabled = false }
    }
}

import CoreMotion
import UIKit

/// Reads the gyro ~100×/s and turns it into aiming, steering, mouse movement and raw motion for the PC.
final class MotionEngine {
    private let manager = CMMotionManager()
    private let queue: OperationQueue = {
        let q = OperationQueue()
        q.name = "tailpad.motion"
        q.maxConcurrentOperationCount = 1
        q.qualityOfService = .userInteractive
        return q
    }()
    private let mixer: InputMixer
    private let lock = NSLock()

    // set from the main thread
    private var config = GyroConfig()
    private var holdCount = 0
    private var toggledOn = false
    private var sendRaw = true
    private var flips = (false, false, false)
    /// true when the home indicator / charging port is on the right
    private var portOnRight = true
    private var steerZero: Double?

    init(mixer: InputMixer) { self.mixer = mixer }

    var available: Bool { manager.isDeviceMotionAvailable }

    func configure(_ c: GyroConfig, sendRaw: Bool, flips: (Bool, Bool, Bool)) {
        lock.lock()
        config = c
        self.sendRaw = sendRaw
        self.flips = flips
        steerZero = nil
        lock.unlock()
        updateRunning()
    }

    func setOrientation(_ o: UIInterfaceOrientation) {
        lock.lock(); portOnRight = (o != .landscapeLeft); lock.unlock()
    }

    /// A "Gyro" button was pressed (+1) or released (−1).
    func hold(_ delta: Int) { lock.lock(); holdCount = max(0, holdCount + delta); lock.unlock() }
    func setToggle(_ on: Bool) { lock.lock(); toggledOn = on; lock.unlock() }

    /// Re-centre tilt steering on the current angle.
    func recenter() { lock.lock(); steerZero = nil; lock.unlock() }

    private func updateRunning() {
        lock.lock()
        let need = config.mode != .off || sendRaw
        lock.unlock()
        if need { start() } else { stop() }
    }

    func start() {
        guard manager.isDeviceMotionAvailable, !manager.isDeviceMotionActive else { return }
        manager.deviceMotionUpdateInterval = 1.0 / 100.0
        manager.startDeviceMotionUpdates(using: .xArbitraryZVertical, to: queue) { [weak self] m, _ in
            guard let self, let m else { return }
            self.process(m)
        }
    }

    func stop() {
        manager.stopDeviceMotionUpdates()
        mixer.setGyroStick(0, 0)
        mixer.setSteer(nil)
        mixer.setMotion(nil)
    }

    private var lastTime: TimeInterval?

    private func process(_ m: CMDeviceMotion) {
        lock.lock()
        let cfg = config, held = holdCount > 0 || toggledOn, raw = sendRaw, f = flips, right = portOnRight
        lock.unlock()

        let dt = lastTime.map { min(max(m.timestamp - $0, 0.001), 0.05) } ?? 0.01
        lastTime = m.timestamp
        let deg = 180.0 / Double.pi

        // Device frame (portrait): x right, y up, z out of screen. Convert to the landscape screen frame.
        let r = m.rotationRate, g = m.gravity, u = m.userAcceleration
        func screen(_ x: Double, _ y: Double, _ z: Double) -> (Double, Double, Double) {
            right ? (-y, x, z) : (y, -x, z)
        }
        let w = screen(r.x, r.y, r.z)            // rad/s about screen X (pitch), Y (yaw), Z (roll)
        let grav = screen(g.x, g.y, g.z)
        let acc = screen(g.x + u.x, g.y + u.y, g.z + u.z)

        if raw {
            let sx = f.0 ? -1.0 : 1.0, sy = f.1 ? -1.0 : 1.0, sz = f.2 ? -1.0 : 1.0
            mixer.setMotion(MotionFrame(
                accel: (acc.0 * sx, acc.1 * sy, acc.2 * sz),
                gyro: (w.0 * deg * sx, w.1 * deg * sy, w.2 * deg * sz),
                timestampUs: UInt64(m.timestamp * 1_000_000)))
        } else {
            mixer.setMotion(nil)
        }

        let active: Bool
        switch cfg.mode {
        case .off: active = false
        case .always: active = true
        case .whileButton: active = held
        case .whileAiming: active = mixer.leftTrigger > 0.5 || held
        }
        guard active else {
            mixer.setGyroStick(0, 0)
            mixer.setSteer(nil)
            return
        }

        let ix = cfg.invertX ? -1.0 : 1.0, iy = cfg.invertY ? -1.0 : 1.0
        // turning right = negative rotation about the screen's up axis; aiming up = positive about its X axis
        let yawRight = -w.1 * deg, pitchUp = w.0 * deg
        let s = cfg.sensitivity

        switch cfg.target {
        case .rightStick:
            // small rotations still register: 0.5 °/s dead zone, ~150 °/s = full stick
            func curve(_ v: Double) -> Double {
                let a = abs(v)
                guard a > 0.5 else { return 0 }
                return copysign(min(1, pow((a - 0.5) / 150 * s * 1.6, 0.8)), v)
            }
            mixer.setGyroStick(curve(yawRight) * ix, curve(pitchUp) * iy)
            mixer.setSteer(nil)
        case .mouse:
            let pxPerDeg = 18.0 * s
            mixer.addMouse(yawRight * dt * pxPerDeg * ix, -pitchUp * dt * pxPerDeg * iy)
            mixer.setGyroStick(0, 0)
            mixer.setSteer(nil)
        case .steer:
            // tilt like a steering wheel: rotation of gravity within the screen plane
            let angle = atan2(grav.0, -grav.1) * deg
            lock.lock()
            if steerZero == nil { steerZero = abs(angle) < 30 ? angle : 0 } // centre on how you're holding it
            let zero = steerZero ?? 0
            lock.unlock()
            let v = max(-1, min(1, (angle - zero) / (45 / s))) * ix
            mixer.setSteer(abs(v) < 0.03 ? 0 : v)
            mixer.setGyroStick(0, 0)
        }
    }
}

import Foundation

/// One control's (or one macro's / the gyro's) share of the controller state.
struct Contribution {
    var buttons: UInt32 = 0
    var turbo: UInt32 = 0
    var turboHz: Double = 12
    var lx = 0.0, ly = 0.0, rx = 0.0, ry = 0.0
    var lt = 0.0, rt = 0.0
}

/// Raw phone motion in the TailPad/DSU frame: g and deg/s.
struct MotionFrame {
    var accel: (Double, Double, Double) = (0, 0, 0)
    var gyro: (Double, Double, Double) = (0, 0, 0) // pitch, yaw, roll
    var timestampUs: UInt64 = 0
}

/// Everything that goes into one input packet.
struct InputFrame {
    var buttons: UInt32 = 0
    var lx = 0.0, ly = 0.0, rx = 0.0, ry = 0.0
    var lt = 0.0, rt = 0.0
    var mouseDx = 0.0, mouseDy = 0.0
    var motion: MotionFrame?
}

/// Combines every finger, the gyro and motion into one controller state. Thread-safe:
/// touches write from the main thread, gyro from the motion queue, the sender reads from the network queue.
final class InputMixer {
    private let lock = NSLock()
    private var parts: [UUID: Contribution] = [:]
    private var gyroStick: (Double, Double) = (0, 0)
    private var steer: Double?
    private var mouseX = 0.0, mouseY = 0.0
    private var motion: MotionFrame?

    func set(_ id: UUID, _ c: Contribution) { lock.lock(); parts[id] = c; lock.unlock() }
    func clear(_ id: UUID) { lock.lock(); parts[id] = nil; lock.unlock() }

    func clearAll() {
        lock.lock()
        parts.removeAll()
        gyroStick = (0, 0); steer = nil; mouseX = 0; mouseY = 0
        lock.unlock()
    }

    func addMouse(_ dx: Double, _ dy: Double) { lock.lock(); mouseX += dx; mouseY += dy; lock.unlock() }
    func setGyroStick(_ x: Double, _ y: Double) { lock.lock(); gyroStick = (x, y); lock.unlock() }
    func setSteer(_ v: Double?) { lock.lock(); steer = v; lock.unlock() }
    func setMotion(_ m: MotionFrame?) { lock.lock(); motion = m; lock.unlock() }

    /// Left trigger amount from fingers — used for "gyro while aiming".
    var leftTrigger: Double {
        lock.lock(); defer { lock.unlock() }
        return parts.values.map(\.lt).max() ?? 0
    }

    /// Builds the frame to send; consumes the accumulated mouse movement.
    func snapshot(now: TimeInterval = ProcessInfo.processInfo.systemUptime) -> InputFrame {
        lock.lock(); defer { lock.unlock() }
        var f = InputFrame()
        for c in parts.values {
            f.buttons |= c.buttons
            if c.turbo != 0 && Int(now * c.turboHz * 2) % 2 == 0 { f.buttons |= c.turbo }
            f.lx += c.lx; f.ly += c.ly; f.rx += c.rx; f.ry += c.ry
            f.lt = max(f.lt, c.lt); f.rt = max(f.rt, c.rt)
        }
        f.rx += gyroStick.0; f.ry += gyroStick.1
        if let steer { f.lx = steer }
        (f.lx, f.ly) = Self.clampStick(f.lx, f.ly)
        (f.rx, f.ry) = Self.clampStick(f.rx, f.ry)
        f.lt = min(max(f.lt, 0), 1); f.rt = min(max(f.rt, 0), 1)
        f.mouseDx = mouseX; f.mouseDy = mouseY
        mouseX = 0; mouseY = 0
        f.motion = motion
        return f
    }

    static func clampStick(_ x: Double, _ y: Double) -> (Double, Double) {
        let m = (x * x + y * y).squareRoot()
        return m > 1 ? (x / m, y / m) : (x, y)
    }
}

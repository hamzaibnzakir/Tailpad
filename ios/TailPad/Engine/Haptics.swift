import CoreHaptics
import UIKit

/// Button clicks + real rumble from the game (the native app can do what Safari can't).
final class Haptics {
    var enabled = true
    var rumbleEnabled = true

    private let light = UIImpactFeedbackGenerator(style: .light)
    private let rigid = UIImpactFeedbackGenerator(style: .rigid)
    private var engine: CHHapticEngine?
    private var player: CHHapticAdvancedPatternPlayer?
    private var rumbling = false

    init() {
        light.prepare()
        rigid.prepare()
        guard CHHapticEngine.capabilitiesForHardware().supportsHaptics else { return }
        engine = try? CHHapticEngine()
        engine?.playsHapticsOnly = true
        engine?.isAutoShutdownEnabled = true
        engine?.resetHandler = { [weak self] in
            self?.player = nil
            try? self?.engine?.start()
        }
        engine?.stoppedHandler = { [weak self] _ in self?.player = nil }
    }

    func tap() { if enabled { light.impactOccurred(intensity: 0.7) } }
    func bump() { if enabled { rigid.impactOccurred() } }

    /// large / small motor 0-255 from the game
    func rumble(large: Int, small: Int) {
        guard rumbleEnabled, let engine else { return }
        let intensity = Float(max(large, small)) / 255
        if intensity < 0.02 {
            if rumbling { try? player?.stop(atTime: CHHapticTimeImmediate); rumbling = false }
            return
        }
        // heavy motor → dull rumble, light motor → buzzier
        let sharpness: Float = large >= small ? 0.15 : 0.6
        do {
            if player == nil {
                try engine.start()
                let event = CHHapticEvent(eventType: .hapticContinuous, parameters: [
                    CHHapticEventParameter(parameterID: .hapticIntensity, value: 1),
                    CHHapticEventParameter(parameterID: .hapticSharpness, value: 0.3),
                ], relativeTime: 0, duration: 30)
                player = try engine.makeAdvancedPlayer(with: CHHapticPattern(events: [event], parameters: []))
                player?.loopEnabled = true
            }
            try player?.sendParameters([
                CHHapticDynamicParameter(parameterID: .hapticIntensityControl, value: intensity, relativeTime: 0),
                CHHapticDynamicParameter(parameterID: .hapticSharpnessControl, value: sharpness - 0.3, relativeTime: 0),
            ], atTime: CHHapticTimeImmediate)
            if !rumbling { try player?.start(atTime: CHHapticTimeImmediate); rumbling = true }
        } catch {
            player = nil
            rumbling = false
        }
    }

    func stopRumble() { rumble(large: 0, small: 0) }
}

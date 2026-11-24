import SwiftUI
import UIKit

/// Transparent multi-touch layer over the pad. Touches that start on a control are routed to the
/// engine; touches anywhere else fall through to the buttons underneath (menu, page dots).
struct TouchSurface: UIViewRepresentable {
    let engine: PadEngine
    let frames: [UUID: CGRect]
    var onOrientation: (UIInterfaceOrientation) -> Void = { _ in }

    func makeUIView(context: Context) -> SurfaceView {
        let v = SurfaceView()
        v.engine = engine
        v.onOrientation = onOrientation
        return v
    }

    func updateUIView(_ v: SurfaceView, context: Context) {
        v.engine = engine
        v.onOrientation = onOrientation
        engine.frames = frames
    }

    final class SurfaceView: UIView {
        var engine: PadEngine?
        var onOrientation: (UIInterfaceOrientation) -> Void = { _ in }

        override init(frame: CGRect) {
            super.init(frame: frame)
            isMultipleTouchEnabled = true
            isExclusiveTouch = false
            backgroundColor = .clear
        }

        required init?(coder: NSCoder) { fatalError() }

        override func layoutSubviews() {
            super.layoutSubviews()
            if let o = window?.windowScene?.interfaceOrientation { onOrientation(o) }
        }

        override func hitTest(_ point: CGPoint, with event: UIEvent?) -> UIView? {
            guard let engine, engine.control(at: point) != nil else { return nil }
            return self
        }

        override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {
            guard let engine else { return }
            for t in touches { engine.began(ObjectIdentifier(t), at: t.location(in: self)) }
        }

        override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) {
            guard let engine else { return }
            for t in touches { engine.moved(ObjectIdentifier(t), to: t.location(in: self)) }
        }

        override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) {
            guard let engine else { return }
            for t in touches { engine.ended(ObjectIdentifier(t)) }
        }

        override func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?) {
            touchesEnded(touches, with: event)
        }
    }
}
